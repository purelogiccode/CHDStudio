using CHDStudio.Interfaces;
using CHDStudio.Models;

namespace CHDStudio.Utilities;

/// <summary>
///     Prepares an isolated ASCII work directory for a cue/toc descriptor when chdman cannot be handed
///     the original file as-is: UTF-8 BOM (which chdman's parser does not skip, producing
///     "couldn't find bin file []"), non-UTF-8 cue text, non-ASCII names or paths, paths at or beyond
///     MAX_PATH (chdman's ANSI file APIs cannot open them), or referenced names
///     that needed zero-padding correction. The work directory contains a canonicalized descriptor
///     (named "game" with the source's own extension, so a cdrdao .toc keeps its parser in both
///     encoders) plus every referenced file under safe ASCII names (trackNN.ext), so chdman sees a
///     self-contained set.
///     When the only problem is a BOM and the bins are on the same drive as the work directory, the
///     bins are NOT copied — the canonical cue instead references them via relative paths.
///     WAVE tracks whose header declares more audio than the file contains are clamped in a private
///     work copy, because both encoders abort when the header runs past the end of the file.
/// </summary>
internal static class CueWorkDirectory
{
    /// <summary>
    ///     Creates the work directory and copies the cue set into it, or returns a result with null
    ///     paths when the descriptor can be converted directly. On failure, the work directory is
    ///     removed and the exception is rethrown.
    /// </summary>
    /// <param name="cuePath">Path of the .cue or .toc descriptor.</param>
    /// <param name="tempDirPrefix">Prefix used for the work directory name (e.g. "CHDStudio_Temp_").</param>
    /// <param name="mp3Decoder">
    ///     Optional MP3 decoder; when provided, MP3 audio tracks are decoded to WAV in the work directory
    ///     instead of being copied.
    /// </param>
    /// <param name="onLog">Optional logging callback.</param>
    /// <param name="token">Cancellation token.</param>
    internal static async Task<CueWorkDirectoryResult> PrepareAsync(
        string cuePath,
        string tempDirPrefix,
        IMp3Decoder? mp3Decoder = null,
        Action<string>? onLog = null,
        CancellationToken token = default
    )
    {
        var result = await CueNormalizer.NormalizeAsync(cuePath, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        if (result.UnresolvedNames.Count > 0) return new CueWorkDirectoryResult(null, null, result.UnresolvedNames);

        // WAVE tracks whose header declares more audio than the file contains make both chdman
        // ("Error reading input file") and the built-in encoder ("Unexpected end of file") fail.
        // They are repaired in a private copy below.
        var overDeclaredWaves = FindOverDeclaredWaves(result.References);

        var isUtf8 = string.Equals(
            result.SourceEncoding.WebName,
            "utf-8",
            StringComparison.OrdinalIgnoreCase
        );
        var hasMp3Tracks =
            mp3Decoder is not null
            && result.References.Any(static r =>
                string.Equals(r.TrackType, "MP3", StringComparison.Ordinal)
            );
        var namesNeedAscii =
            cuePath.Any(static c => c > 127)
            || result.References.Any(static r => r.ReferencedName.Any(static c => c > 127));

        // chdman's CRT file APIs are capped at MAX_PATH (260): a descriptor or referenced file at
        // or beyond that length fails with "No such file or directory" even though every file
        // exists. The copy-based work directory below gives chdman short ASCII names instead.
        // The in-place fast path is declined for these cues because its relative FILE references
        // rejoin into the same overlong paths.
        var pathTooLong =
            cuePath.Length >= PathUtils.MaxChdmanPath
            || result.References.Any(r => r.ResolvedFullPath.Length >= PathUtils.MaxChdmanPath);

        var needsWorkDir =
            !isUtf8
            || result.HasBom
            || result.NeedsRewrite
            || result.ReferencesChanged
            || hasMp3Tracks
            || namesNeedAscii
            || pathTooLong
            || overDeclaredWaves.Count > 0;
        if (!needsWorkDir) return new CueWorkDirectoryResult(null, null, []);

        // Repair-only fast path: when the set is otherwise chdman-safe, put the work directory on
        // the cue's own volume and reference everything except the damaged WAVE tracks relatively,
        // so only the (small) WAVE copies need to be written instead of the entire image set.
        if (overDeclaredWaves.Count > 0 && !hasMp3Tracks && !namesNeedAscii && !pathTooLong)
        {
            var repair = await TryPrepareWaveRepairWorkDirAsync(
                    cuePath,
                    result.References,
                    overDeclaredWaves,
                    tempDirPrefix,
                    onLog,
                    token
                )
                .ConfigureAwait(false);
            if (repair is not null)
                return new CueWorkDirectoryResult(repair.Value.WorkCuePath, repair.Value.WorkDir, []);
        }

        var workDir = PathUtils.GetBestTempDirectory(cuePath, cuePath, tempDirPrefix);
        Directory.CreateDirectory(workDir);

        try
        {
            // Fast path for BOM-only cues (canonical content, ASCII names, no MP3, safe path
            // lengths): write the BOM-free canonical cue into the work directory and reference
            // each bin via a relative path from the work directory, so chdman reads the bins in
            // place without copying them. chdman prepends the cue's directory to every FILE name,
            // so the relative path must stay relative — a rooted path (bins on another drive)
            // forces the copy-based fallback below.
            if (
                result.HasBom
                && !hasMp3Tracks
                && !namesNeedAscii
                && !pathTooLong
                && overDeclaredWaves.Count == 0
            )
            {
                var inPlaceWorkCue = await TryWriteInPlaceWorkCueAsync(cuePath, workDir, token)
                    .ConfigureAwait(false);
                if (inPlaceWorkCue is not null) return new CueWorkDirectoryResult(inPlaceWorkCue, workDir, []);
            }

            // Assign a unique ASCII work name (trackNN.ext) to every referenced file.
            // MP3 tracks are decoded to trackNN.wav so chdman can consume them.
            var workNames = new Dictionary<string, string>(StringComparer.Ordinal); // FullPath -> work name
            var workTypes =
                new Dictionary<string, string>(StringComparer.Ordinal); // FullPath -> replacement track type
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < result.References.Count; i++)
            {
                var reference = result.References[i];
                string workName;
                if (
                    mp3Decoder is not null
                    && string.Equals(reference.TrackType, "MP3", StringComparison.Ordinal)
                )
                {
                    workName = $"track{i + 1:D2}.wav";
                    workTypes[reference.FullPath] = "WAVE";
                }
                else
                {
                    var extension = Path.GetExtension(reference.ResolvedFullPath);
                    workName = $"track{i + 1:D2}{extension}";
                }

                var baseName = workName;
                var suffix = 1;
                while (!usedNames.Add(workName))
                {
                    workName =
                        $"{Path.GetFileNameWithoutExtension(baseName)}_{suffix++}{Path.GetExtension(baseName)}";
                }

                workNames[reference.FullPath] = workName;
            }

            // Copy (or decode) every referenced file into the work directory.
            foreach (var reference in result.References)
            {
                var workName = workNames[reference.FullPath];
                onLog?.Invoke(
                    $"Preparing {Path.GetFileName(reference.ResolvedFullPath)} for conversion..."
                );
                if (
                    mp3Decoder is not null
                    && string.Equals(reference.TrackType, "MP3", StringComparison.Ordinal)
                )
                {
                    await mp3Decoder
                        .DecodeAsync(
                            reference.ResolvedFullPath,
                            Path.Combine(workDir, workName),
                            onLog,
                            token
                        )
                        .ConfigureAwait(false);
                }
                else
                {
                    var destination = Path.Combine(workDir, workName);
                    await CopyWithRetryAsync(reference.ResolvedFullPath, destination, token)
                        .ConfigureAwait(false);
                    if (overDeclaredWaves.Contains(reference.ResolvedFullPath))
                        RepairWaveHeader(destination, reference.ResolvedFullPath, onLog);
                }
            }

            var normalized = await CueNormalizer
                .NormalizeAsync(cuePath, token, Transform)
                .ConfigureAwait(false);
            var workCue = GetWorkDescriptorPath(workDir, cuePath);
            await CueNormalizer
                .WriteCanonicalCueAsync(workCue, normalized, token)
                .ConfigureAwait(false);
            return new CueWorkDirectoryResult(workCue, workDir, []);

            // Rewrite the cue so its FILE lines reference the ASCII work names.
            (string Name, string? TrackType)? Transform(CueFileReference reference)
            {
                if (!workNames.TryGetValue(reference.FullPath, out var workName)) return null;

                return workTypes.TryGetValue(reference.FullPath, out var workType)
                    ? (workName, workType)
                    : (workName, reference.TrackType);
            }
        }
        catch
        {
            try
            {
                if (Directory.Exists(workDir))
                    Directory.Delete(workDir, true);
            }
            catch
            {
                // ignored
            }

            throw;
        }
    }

    /// <summary>
    ///     Attempts the in-place fast path: writes a BOM-free canonical descriptor into
    ///     <paramref name="workDir" /> whose FILE lines reference every bin via a path relative to
    ///     <paramref name="workDir" /> (chdman prepends the cue's directory to every FILE name, so the
    ///     bins must be addressed relative to where the cue lives). No bin files are copied.
    ///     Returns null when any bin cannot be referenced relatively (e.g. it is on another drive),
    ///     in which case the caller must fall back to the copy-based path.
    /// </summary>
    internal static async Task<string?> TryWriteInPlaceWorkCueAsync(
        string cuePath,
        string workDir,
        CancellationToken token
    )
    {
        var normalized = await CueNormalizer
            .NormalizeAsync(cuePath, token, TransformRelative)
            .ConfigureAwait(false);
        try
        {
            if (
                normalized.UnresolvedNames.Count > 0
                || normalized.References.Any(r =>
                    Path.IsPathRooted(Path.GetRelativePath(workDir, r.ResolvedFullPath))
                )
            )
            {
                return null;
            }
        }
        catch (ArgumentException)
        {
            // Partially-qualified or drive-vs-UNC root mismatches make GetRelativePath throw;
            // decline the in-place fast path so the caller falls back to the copy-based path.
            return null;
        }

        var workCue = GetWorkDescriptorPath(workDir, cuePath);
        await CueNormalizer
            .WriteCanonicalCueAsync(workCue, normalized, token)
            .ConfigureAwait(false);
        return workCue;

        // Rewrite the cue so its FILE lines reference each bin via a path relative to the
        // work directory (chdman prepends the cue's directory to every FILE name, so the
        // bins must be addressed relative to where the cue lives).
        (string Name, string? TrackType)? TransformRelative(CueFileReference reference)
        {
            return (Path.GetRelativePath(workDir, reference.ResolvedFullPath), reference.TrackType);
        }
    }

    /// <summary>
    ///     Attempts a repair-only work set for a cue whose only defect is one or more WAVE tracks
    ///     whose headers declare more audio than their files contain: the work directory is created
    ///     on the cue's own volume and the canonical cue references every other referenced file
    ///     relatively, so only the damaged WAVE tracks are copied (and clamped), not the whole
    ///     image set.
    /// </summary>
    /// <param name="cuePath">Path of the .cue or .toc descriptor.</param>
    /// <param name="references">Every FILE line, with its resolved on-disk name.</param>
    /// <param name="overDeclaredWaves">Full paths of the WAVE tracks whose headers over-declare.</param>
    /// <param name="tempDirPrefix">Prefix used for the work directory name.</param>
    /// <param name="onLog">Optional logging callback.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>
    ///     The repaired work cue and its work directory, or null when the set cannot be prepared this
    ///     way (no writable location on the cue's volume, or a referenced file on another volume);
    ///     the caller then falls back to the full copy-based work directory.
    /// </returns>
    private static async Task<(string WorkCuePath, string WorkDir)?> TryPrepareWaveRepairWorkDirAsync(
        string cuePath,
        IReadOnlyList<CueFileReference> references,
        HashSet<string> overDeclaredWaves,
        string tempDirPrefix,
        Action<string>? onLog,
        CancellationToken token
    )
    {
        var workDir = PathUtils.CreateTempDirectoryOnSameVolume(cuePath, tempDirPrefix);
        if (workDir is null) return null;

        var succeeded = false;
        try
        {
            // One repaired copy per damaged file; several tracks may share the same WAVE file.
            var repairNames = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var reference in references)
            {
                if (!reference.IsResolved) return null;
                if (!overDeclaredWaves.Contains(reference.ResolvedFullPath)) continue;
                if (repairNames.ContainsKey(reference.ResolvedFullPath)) continue;

                repairNames[reference.ResolvedFullPath] = $"track{repairNames.Count + 1:D2}.wav";
            }

            if (repairNames.Count == 0) return null;

            // Everything except the repaired WAVE tracks stays where it is and must be reachable
            // from the work directory by a relative path: chdman joins FILE names to the cue's own
            // directory and cannot follow an absolute path.
            foreach (var reference in references)
            {
                if (Path.IsPathRooted(Path.GetRelativePath(workDir, reference.ResolvedFullPath)))
                    return null;
            }

            var normalized = await CueNormalizer
                .NormalizeAsync(cuePath, token, Transform)
                .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (normalized.UnresolvedNames.Count > 0) return null;

            foreach (var (source, workName) in repairNames)
            {
                var destination = Path.Combine(workDir, workName);
                onLog?.Invoke($"Preparing {Path.GetFileName(source)} for conversion...");
                await CopyWithRetryAsync(source, destination, token).ConfigureAwait(false);
                RepairWaveHeader(destination, source, onLog);
            }

            var workCue = GetWorkDescriptorPath(workDir, cuePath);
            await CueNormalizer
                .WriteCanonicalCueAsync(workCue, normalized, token)
                .ConfigureAwait(false);

            succeeded = true;
            return (workCue, workDir);

            // Damaged WAVE tracks point at their repaired work copy; every other file is
            // addressed relative to the work directory.
            (string Name, string? TrackType)? Transform(CueFileReference reference)
            {
                return repairNames.TryGetValue(reference.ResolvedFullPath, out var workName)
                    ? (workName, "WAVE")
                    : (Path.GetRelativePath(workDir, reference.ResolvedFullPath), reference.TrackType);
            }
        }
        catch (ArgumentException)
        {
            // Partially-qualified or drive-vs-UNC root mismatches make GetRelativePath throw;
            // decline the repair path so the caller falls back to the copy-based path.
            return null;
        }
        finally
        {
            if (!succeeded) TryDeleteDirectory(workDir);
        }
    }

    /// <summary>
    ///     Finds the resolved WAVE tracks whose header declares more audio bytes than the file
    ///     contains (truncated or mislabelled rip). A file that is also referenced as a data track is
    ///     excluded: clamping it would alter its bytes, and such a cue cannot be converted
    ///     meaningfully anyway.
    /// </summary>
    /// <param name="references">Every FILE line, with its resolved on-disk name.</param>
    /// <returns>The full paths of the over-declared, WAVE-only files.</returns>
    private static HashSet<string> FindOverDeclaredWaves(IReadOnlyList<CueFileReference> references)
    {
        var wavePaths = new HashSet<string>(StringComparer.Ordinal);
        var nonWavePaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in references)
        {
            if (!reference.IsResolved) continue;
            if (string.Equals(reference.TrackType, "WAVE", StringComparison.OrdinalIgnoreCase))
                wavePaths.Add(reference.ResolvedFullPath);
            else
                nonWavePaths.Add(reference.ResolvedFullPath);
        }

        wavePaths.ExceptWith(nonWavePaths);

        var overDeclared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in wavePaths)
        {
            if (WavHeaderSanitizer.IsOverDeclared(path)) overDeclared.Add(path);
        }

        return overDeclared;
    }

    /// <summary>
    ///     Clamps an over-declared WAVE header on a work copy and logs how much audio the header had
    ///     over-declared.
    /// </summary>
    /// <param name="workCopyPath">Path of the copied WAVE file to repair.</param>
    /// <param name="sourcePath">Path of the original file, for the log message.</param>
    /// <param name="onLog">Optional logging callback.</param>
    private static void RepairWaveHeader(
        string workCopyPath,
        string sourcePath,
        Action<string>? onLog
    )
    {
        var overDeclaredBytes = WavHeaderSanitizer.ClampDeclaredSizes(workCopyPath);
        if (overDeclaredBytes <= 0) return;

        onLog?.Invoke(
            $"Repaired {Path.GetFileName(sourcePath)}: its WAV header declares {overDeclaredBytes:N0} more audio bytes than the file contains; using the audio that is present."
        );
    }

    /// <summary>
    ///     Deletes a directory tree, ignoring any failure.
    /// </summary>
    /// <param name="directory">Directory to delete.</param>
    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        catch
        {
            // ignored
        }
    }

    /// <summary>
    ///     Returns the work descriptor path, keeping the source descriptor's extension: both encoders
    ///     dispatch by extension (chdman's parse_toc and the built-in CdImageParser), so a cdrdao
    ///     .toc must stay ".toc" — renamed to .cue, its cdrdao TRACK lines would be read by the CUE
    ///     parser and fail with "Malformed TRACK command".
    /// </summary>
    /// <param name="workDir">The work directory.</param>
    /// <param name="sourceDescriptorPath">The original .cue or .toc path.</param>
    /// <returns>Path of the canonical descriptor inside the work directory.</returns>
    private static string GetWorkDescriptorPath(string workDir, string sourceDescriptorPath)
    {
        var extension = Path.GetExtension(sourceDescriptorPath);
        var name = string.IsNullOrEmpty(extension)
            ? "game.cue"
            : "game" + extension.ToLowerInvariant();
        return Path.Combine(workDir, name);
    }

    /// <summary>
    ///     Copies a file into the work directory, retrying a few times while the source is
    ///     temporarily locked by another process.
    /// </summary>
    /// <param name="source">Path of the file to copy.</param>
    /// <param name="dest">Destination path in the work directory.</param>
    /// <param name="token">Cancellation token.</param>
    private static async Task CopyWithRetryAsync(
        string source,
        string dest,
        CancellationToken token
    )
    {
        const int maxAttempts = 4;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();

            try
            {
                await Task.Run(() => File.Copy(source, dest, true), token).ConfigureAwait(false);
                return;
            }
            catch (IOException) when (attempt < maxAttempts - 1)
            {
                await Task.Delay(300 * (attempt + 1), token).ConfigureAwait(false);
            }
        }
    }
}
