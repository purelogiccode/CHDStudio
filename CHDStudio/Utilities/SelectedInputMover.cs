using CCDSharp;
using MDSSharp;

namespace CHDStudio.Utilities;

/// <summary>The result of moving a set of conversion inputs.</summary>
/// <param name="MovedInputs">Primary inputs that were moved successfully.</param>
/// <param name="FailedInputs">Primary inputs that could not be moved.</param>
internal sealed record MoveSelectedResult(
    IReadOnlyList<string> MovedInputs,
    IReadOnlyList<string> FailedInputs
);

/// <summary>
///     Moves selected conversion inputs to another folder, carrying the data files their
///     cue/gdi/toc/ccd/mds descriptors reference and every volume of a split image or archive set.
///     Descriptor sets are placed in a subfolder named after the input, so cue/bin and mds/mdf
///     pairs stay together and cannot collide with another set's files.
/// </summary>
internal static class SelectedInputMover
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    /// <summary>
    ///     Moves <paramref name="inputFiles" /> (and their referenced files) into
    ///     <paramref name="destinationFolder" />.
    /// </summary>
    /// <param name="destinationFolder">Folder that receives the moved sets.</param>
    /// <param name="inputFiles">Primary input paths to move.</param>
    /// <param name="log">Information callback for successful moves.</param>
    /// <param name="logError">Error callback for a set that could not be moved.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>The primary inputs that were moved and those that failed.</returns>
    internal static async Task<MoveSelectedResult> MoveAsync(
        string destinationFolder,
        IReadOnlyCollection<string> inputFiles,
        Action<string> log,
        Action<string> logError,
        CancellationToken token
    )
    {
        var moved = new List<string>();
        var failed = new List<string>();

        if (!PathUtils.TryCreateDirectory(destinationFolder, out var destinationError))
        {
            logError(
                $" The selected files could not be moved: the destination folder is not available ({destinationError})."
            );
            return new MoveSelectedResult(moved, [.. inputFiles]);
        }

        foreach (var input in inputFiles)
        {
            token.ThrowIfCancellationRequested();

            try
            {
                var companions = await GetCompanionFilesAsync(input, token).ConfigureAwait(false);
                var missingCompanions = companions.Where(companion => !File.Exists(companion)).ToList();
                if (missingCompanions.Count > 0)
                {
                    log(
                        $" Warning: {Path.GetFileName(input)} references missing file(s): {string.Join(", ", missingCompanions.Select(Path.GetFileName))}. Moving only the files that exist."
                    );
                }

                var sources = ExpandVolumeSets(companions.Append(input))
                    .Where(File.Exists)
                    .Distinct(PathComparer)
                    .ToList();

                if (sources.Count == 0)
                {
                    failed.Add(input);
                    logError($" Could not move '{input}': the file no longer exists.");
                    continue;
                }

                var isSet = sources.Count > 1;
                var targetDirectory = isSet
                    ? ReserveDirectory(
                        destinationFolder,
                        Path.GetFileNameWithoutExtension(input)
                    )
                    : destinationFolder;

                if (isSet)
                    Directory.CreateDirectory(targetDirectory);

                var moves = sources
                    .OrderBy(source => PathComparer.Equals(source, input) ? 1 : 0)
                    .Select(source => (
                        Source: source,
                        Target: Path.Join(targetDirectory, Path.GetFileName(source))
                    ))
                    .ToList();

                if (
                    moves
                        .Select(static move => move.Target)
                        .Distinct(PathComparer)
                        .Count() != moves.Count
                )
                {
                    failed.Add(input);
                    logError(
                        $" Could not move '{input}': the set contains two files with the same name."
                    );
                    continue;
                }

                if (!isSet && File.Exists(moves[0].Target))
                    moves[0] = (
                        moves[0].Source,
                        ReserveFileName(destinationFolder, Path.GetFileName(moves[0].Source))
                    );

                if (
                    PathComparer.Equals(
                        Path.GetFullPath(moves[0].Source),
                        Path.GetFullPath(moves[0].Target)
                    )
                )
                {
                    failed.Add(input);
                    logError($" '{input}' is already in that folder.");
                    continue;
                }

                if (!await MoveSetAsync(moves, logError, token).ConfigureAwait(false))
                {
                    failed.Add(input);
                    continue;
                }

                moved.Add(input);
                log(
                    $" Moved {moves.Count} file(s) to: {targetDirectory}"
                );
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failed.Add(input);
                logError($" Could not move '{input}': {ex.Message}");
            }
        }

        return new MoveSelectedResult(moved, failed);
    }

    /// <summary>
    ///     Moves a pre-validated set, companions first and the primary input last. If any move
    ///     fails, the files already moved are put back so a descriptor never loses its data files.
    /// </summary>
    /// <param name="moves">Source/target pairs in move order.</param>
    /// <param name="logError">Error callback for a rollback that also failed.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>True when every file in the set was moved.</returns>
    private static async Task<bool> MoveSetAsync(
        IReadOnlyList<(string Source, string Target)> moves,
        Action<string> logError,
        CancellationToken token
    )
    {
        var completed = new List<(string Source, string Target)>();

        foreach (var move in moves)
        {
            if (
                !await RetryingFileOperations
                    .TryMoveAsync(move.Source, move.Target, token)
                    .ConfigureAwait(false)
            )
            {
                for (var i = completed.Count - 1; i >= 0; i--)
                {
                    var movedBack = await RetryingFileOperations
                        .TryMoveAsync(
                            completed[i].Target,
                            completed[i].Source,
                            CancellationToken.None
                        )
                        .ConfigureAwait(false);
                    if (!movedBack)
                    {
                        logError(
                            $" Could not put '{completed[i].Target}' back at '{completed[i].Source}'."
                        );
                    }
                }

                logError(
                    $" Could not move '{move.Source}' to '{move.Target}' (it may be locked by another program)."
                );
                return false;
            }

            completed.Add(move);
        }

        return true;
    }

    /// <summary>
    ///     Returns the primary input plus every existing companion and split volume that belongs to
    ///     its set. Used to package a cue/bin, gdi, toc, ccd or mds set for one upload.
    /// </summary>
    /// <param name="input">Primary input path.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>Existing paths that make up the set, primary first.</returns>
    internal static async Task<IReadOnlyList<string>> GetUploadSetAsync(
        string input,
        CancellationToken token
    )
    {
        var companions = await GetCompanionFilesAsync(input, token).ConfigureAwait(false);
        return ExpandVolumeSets(companions.Append(input))
            .Where(File.Exists)
            .Distinct(PathComparer)
            .ToList();
    }

    /// <summary>
    ///     Returns the data files referenced by a descriptor, plus the descriptor that owns an
    ///     image selected on its own (a bare .mdf/.mdx gets its .mds, an .img/.sub gets its .ccd).
    /// </summary>
    /// <param name="input">Primary input path.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>The companion paths; empty for a single-file image.</returns>
    internal static async Task<List<string>> GetCompanionFilesAsync(
        string input,
        CancellationToken token
    )
    {
        var companions = new List<string>();
        var extension = Path.GetExtension(input);

        if (extension.Equals(FileExtensions.Cue, StringComparison.OrdinalIgnoreCase))
        {
            companions.AddRange(
                await GameFileParser
                    .GetReferencedFilesFromCueAsync(input, static _ => { }, token)
                    .ConfigureAwait(false)
            );
        }
        else if (extension.Equals(FileExtensions.Gdi, StringComparison.OrdinalIgnoreCase))
        {
            companions.AddRange(
                await GameFileParser
                    .GetReferencedFilesFromGdiAsync(input, static _ => { }, token)
                    .ConfigureAwait(false)
            );
        }
        else if (extension.Equals(FileExtensions.Toc, StringComparison.OrdinalIgnoreCase))
        {
            companions.AddRange(
                await GameFileParser
                    .GetReferencedFilesFromTocAsync(input, static _ => { }, token)
                    .ConfigureAwait(false)
            );
        }
        else if (extension.Equals(FileExtensions.Ccd, StringComparison.OrdinalIgnoreCase))
        {
            var disc = CcdConverter.Parse(input);
            if (disc.ImgFilePath is not null) companions.Add(disc.ImgFilePath);
            if (disc.SubFilePath is not null) companions.Add(disc.SubFilePath);

            var cdtPath = Path.ChangeExtension(input, ".cdt");
            if (File.Exists(cdtPath)) companions.Add(cdtPath);
        }
        else if (extension.Equals(FileExtensions.Mds, StringComparison.OrdinalIgnoreCase))
        {
            var disc = await Task.Run(() => MdsParser.Parse(input), token).ConfigureAwait(false);
            companions.AddRange(disc.DataFilePaths);
            if (disc.MdfPath is not null) companions.Add(disc.MdfPath);
        }
        else if (
            extension.Equals(".mdf", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(FileExtensions.Mdx, StringComparison.OrdinalIgnoreCase)
        )
        {
            var descriptor = Path.ChangeExtension(input, FileExtensions.Mds);
            if (File.Exists(descriptor)) companions.Add(descriptor);
        }
        else if (
            extension.Equals(FileExtensions.Img, StringComparison.OrdinalIgnoreCase)
            || extension.Equals(FileExtensions.Sub, StringComparison.OrdinalIgnoreCase)
        )
        {
            var descriptor = Path.ChangeExtension(input, FileExtensions.Ccd);
            if (File.Exists(descriptor)) companions.Add(descriptor);
        }

        companions.RemoveAll(companion => PathComparer.Equals(companion, input));
        return companions;
    }

    /// <summary>
    ///     Expands every split image and multi-part archive in the set to all of its on-disk
    ///     volumes, so a move never separates a set from its later parts.
    /// </summary>
    /// <param name="paths">Paths to expand.</param>
    /// <returns>Every path plus any volume-set members.</returns>
    internal static IEnumerable<string> ExpandVolumeSets(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            yield return path;

            var imageVolumes = SplitImageJoiner.TryGetVolumeSet(path);
            if (imageVolumes is { Count: > 1 })
                foreach (var volume in imageVolumes)
                    yield return volume;

            var archiveVolumes = RarVolumeSet.GetVolumePaths(path);
            if (archiveVolumes.Count > 1)
                foreach (var volume in archiveVolumes)
                    yield return volume;
        }
    }

    /// <summary>
    ///     Reserves a subfolder named after the input, adding " (2)", " (3)" and so on until the
    ///     name is free. The directory is not created.
    /// </summary>
    /// <param name="parentDirectory">Folder that receives the new subfolder.</param>
    /// <param name="baseName">Preferred folder name.</param>
    /// <returns>A path that does not exist yet.</returns>
    private static string ReserveDirectory(string parentDirectory, string baseName)
    {
        var safeName = PathUtils.SanitizeFileName(baseName);
        if (safeName.Length == 0) safeName = Guid.NewGuid().ToString("N");

        var candidate = Path.Join(parentDirectory, safeName);
        if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;

        for (var suffix = 2; suffix <= 999; suffix++)
        {
            candidate = Path.Join(parentDirectory, $"{safeName} ({suffix})");
            if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
        }

        return Path.Join(parentDirectory, $"{safeName}_{Guid.NewGuid():N}");
    }

    /// <summary>
    ///     Reserves a file name in <paramref name="directory" />, adding " (2)", " (3)" and so on
    ///     until it is free. The file is not created.
    /// </summary>
    /// <param name="directory">Target directory.</param>
    /// <param name="fileName">Preferred file name.</param>
    /// <returns>A path that does not exist yet.</returns>
    private static string ReserveFileName(string directory, string fileName)
    {
        var candidate = Path.Join(directory, fileName);
        if (!File.Exists(candidate)) return candidate;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var suffix = 2; suffix <= 999; suffix++)
        {
            candidate = Path.Join(directory, $"{stem} ({suffix}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }

        return Path.Join(directory, $"{stem}_{Guid.NewGuid():N}{extension}");
    }
}
