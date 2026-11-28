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

    /// <summary>Descriptor extensions whose files reference other files in their set.</summary>
    private static readonly HashSet<string> DescriptorExtensions = new(
        [
            FileExtensions.Cue,
            FileExtensions.Gdi,
            FileExtensions.Toc,
            FileExtensions.Ccd,
            FileExtensions.Mds,
            FileExtensions.Mdx
        ],
        StringComparer.OrdinalIgnoreCase
    );

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
        var movedSources = new HashSet<string>(PathComparer);

        if (!PathUtils.TryCreateDirectory(destinationFolder, out var destinationError))
        {
            logError(
                $" The selected files could not be moved: the destination folder is not available ({destinationError})."
            );
            return new MoveSelectedResult(moved, [.. inputFiles]);
        }

        foreach (var input in DescriptorsFirst(inputFiles))
        {
            token.ThrowIfCancellationRequested();

            try
            {
                if (movedSources.Contains(Path.GetFullPath(input)))
                {
                    moved.Add(input);
                    log($" '{Path.GetFileName(input)}' moved with its set.");
                    continue;
                }

                if (IsInsideDirectory(input, destinationFolder))
                {
                    failed.Add(input);
                    logError($" '{input}' is already in the Failed folder.");
                    continue;
                }

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

                // Preserve the folder layout a descriptor uses. A cue that references
                // "tracks\01.bin" must keep that relative path after the move, or the moved set
                // no longer converts. Absolute references keep working in their original place,
                // so those files stay put rather than being flattened next to the descriptor.
                var inputDirectory = Path.GetDirectoryName(Path.GetFullPath(input))
                    ?? Path.GetFullPath(input);
                var relativeSources = new List<(string Source, string Relative)>();
                foreach (var source in sources)
                {
                    var relative = Path.GetRelativePath(
                        inputDirectory,
                        Path.GetFullPath(source)
                    );
                    if (Path.IsPathRooted(relative))
                    {
                        log(
                            $" Note: '{Path.GetFileName(source)}' is referenced by an absolute path and stays in place."
                        );
                        continue;
                    }

                    if (IsOutsideDirectory(relative))
                    {
                        throw new InvalidOperationException(
                            $"the set references '{source}', which is outside its own folder; move it manually."
                        );
                    }

                    relativeSources.Add((source, relative));
                }

                var isSet = relativeSources.Count > 1;
                var targetDirectory = isSet
                    ? ReserveDirectory(
                        destinationFolder,
                        Path.GetFileNameWithoutExtension(input)
                    )
                    : destinationFolder;

                var moves = relativeSources
                    .OrderBy(source => PathComparer.Equals(source.Source, input) ? 1 : 0)
                    .Select(source => (
                        source.Source,
                        Target: Path.Join(targetDirectory, source.Relative)
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

                Directory.CreateDirectory(targetDirectory);
                var targetDirectories = moves
                    .Select(static move => Path.GetDirectoryName(move.Target))
                    .Where(static directory => !string.IsNullOrEmpty(directory))
                    .Distinct(PathComparer);
                foreach (var targetParent in targetDirectories)
                {
                    Directory.CreateDirectory(targetParent!);
                }

                if (!await MoveSetAsync(moves, logError, token).ConfigureAwait(false))
                {
                    failed.Add(input);
                    continue;
                }

                foreach (var move in moves)
                    movedSources.Add(Path.GetFullPath(move.Source));

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
    ///     Orders descriptor inputs before raw data inputs. A failed descriptor and one of its
    ///     data files can both be listed when the data file lives in a subfolder: the batch
    ///     filter only suppresses a covered data file in the descriptor's own folder. Moving the
    ///     data file on its own first would separate it from its set; once the descriptor has
    ///     moved the set, the data file's listed path no longer exists. Descriptors first keeps
    ///     each set together and lets the already-moved check account for the companion input.
    /// </summary>
    /// <param name="inputFiles">Primary inputs to move.</param>
    /// <returns>The same inputs with descriptor files first, preserving the order within each group.</returns>
    private static IEnumerable<string> DescriptorsFirst(IEnumerable<string> inputFiles)
    {
        return inputFiles.OrderBy(static input =>
            DescriptorExtensions.Contains(Path.GetExtension(input)) ? 0 : 1
        );
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

        try
        {
            foreach (var move in moves)
            {
                if (
                    !await RetryingFileOperations
                        .TryMoveAsync(move.Source, move.Target, token)
                        .ConfigureAwait(false)
                )
                {
                    await RollbackAsync(completed, logError).ConfigureAwait(false);
                    logError(
                        $" Could not move '{move.Source}' to '{move.Target}' (it may be locked by another program)."
                    );
                    return false;
                }

                completed.Add(move);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            // A cancellation (window close or Cancel) must not leave a descriptor without the
            // data files that were already moved.
            await RollbackAsync(completed, logError).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Puts the files moved so far back where they came from, newest first.</summary>
    /// <param name="completed">Moves that finished before the failure or cancellation.</param>
    /// <param name="logError">Error callback for a rollback that also failed.</param>
    /// <returns>A task that completes when the rollback has been attempted.</returns>
    private static async Task RollbackAsync(
        IReadOnlyList<(string Source, string Target)> completed,
        Action<string> logError
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
    }

    /// <summary>
    ///     Returns the primary input plus every existing companion and split volume that belongs to
    ///     its set. Used to package a cue/bin, gdi, toc, ccd or mds set for one upload.
    /// </summary>
    /// <param name="input">Primary input path.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>Existing paths that make up the set.</returns>
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
    ///     Returns the upload set with relative ZIP entry names, so a crate descriptor that
    ///     references a subfolder keeps that folder in the diagnostic sample.
    /// </summary>
    /// <param name="input">Primary input path.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>The files and their relative archive names.</returns>
    internal static async Task<IReadOnlyList<(string FullPath, string EntryName)>> GetUploadSetEntriesAsync(
        string input,
        CancellationToken token
    )
    {
        var paths = await GetUploadSetAsync(input, token).ConfigureAwait(false);
        var inputDirectory = Path.GetDirectoryName(Path.GetFullPath(input))
            ?? Path.GetFullPath(input);
        return paths
            .Select(path => (
                FullPath: path,
                EntryName: GetUploadEntryName(inputDirectory, path)
            ))
            .ToList();
    }

    /// <summary>Builds a ZIP entry name that preserves paths below the descriptor folder.</summary>
    /// <param name="directory">Descriptor folder.</param>
    /// <param name="path">File in the set.</param>
    /// <returns>A forward-slash relative entry name, or the bare file name for an external path.</returns>
    private static string GetUploadEntryName(string directory, string path)
    {
        var relative = Path.GetRelativePath(directory, Path.GetFullPath(path));
        return Path.IsPathRooted(relative) || IsOutsideDirectory(relative)
            ? Path.GetFileName(path)
            : relative.Replace('\\', '/');
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

    /// <summary>Compares full paths using the host file system's case rules.</summary>
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>True when a path is inside a directory (or is that directory).</summary>
    /// <param name="path">Path to test.</param>
    /// <param name="directory">Directory to test against.</param>
    /// <returns>True when the path is under the directory.</returns>
    private static bool IsInsideDirectory(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path);
        var fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (string.Equals(fullPath, fullDirectory, PathComparison)) return true;

        var prefix = fullDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? fullDirectory
            : fullDirectory + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, PathComparison);
    }

    /// <summary>True when a relative path points at or above its base directory.</summary>
    /// <param name="relativePath">Relative path to test.</param>
    /// <returns>True when the path escapes the base directory.</returns>
    private static bool IsOutsideDirectory(string relativePath)
    {
        return string.Equals(relativePath, "..", PathComparison)
               || relativePath.StartsWith(
                   ".." + Path.DirectorySeparatorChar,
                   PathComparison
               )
               || relativePath.StartsWith(
                   ".." + Path.AltDirectorySeparatorChar,
                   PathComparison
               );
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
