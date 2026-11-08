using System.Globalization;
using CCDSharp;
using CHDStudio.Models;
using MDSSharp;

namespace CHDStudio.Utilities;

/// <summary>
///     Builds the disk-space and failure summaries for a conversion batch. The size side answers
///     "did this actually save space?" with measured bytes instead of the encoder's own ratio, and
///     the failure side writes a persistent list of inputs that did not convert.
/// </summary>
internal static class ConversionReport
{
    /// <summary>Name of the report written next to the converted CHDs when a batch has failures.</summary>
    internal const string FailureReportFileName = "failed-conversions.txt";

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    /// <summary>
    ///     Returns the bytes a batch's inputs occupy on disk, including the data files referenced
    ///     by cue/gdi/toc/ccd/mds descriptors and every part of a split image. Files are counted
    ///     once even when several inputs reference them.
    /// </summary>
    /// <param name="files">The inputs that will be converted.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>The total source size in bytes, or 0 when nothing could be measured.</returns>
    internal static async Task<long> GetSourceBytesAsync(
        IEnumerable<string> files,
        CancellationToken token
    )
    {
        var sourceFiles = new HashSet<string>(PathComparer);

        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            await AddSourceSetAsync(file, sourceFiles, token).ConfigureAwait(false);
        }

        return SumSourceSetBytes(sourceFiles);
    }

    /// <summary>
    ///     Measures each input's own source set before a batch starts. The caller can add the size
    ///     of successful inputs while it converts them, so a deleted source (the "Delete originals"
    ///     option) or a cancelled batch does not lose the accounting.
    /// </summary>
    /// <param name="files">The inputs that will be converted.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>A map from input path to the bytes its source set occupies.</returns>
    internal static async Task<Dictionary<string, long>> GetSourceBytesByInputAsync(
        IEnumerable<string> files,
        CancellationToken token
    )
    {
        var result = new Dictionary<string, long>(PathComparer);

        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            var sourceFiles = new HashSet<string>(PathComparer);
            await AddSourceSetAsync(file, sourceFiles, token).ConfigureAwait(false);
            result[file] = SumSourceSetBytes(sourceFiles);
        }

        return result;
    }

    /// <summary>Adds up the lengths of the paths in a source set.</summary>
    /// <param name="sourceFiles">Paths to measure.</param>
    /// <returns>The total size in bytes; missing files contribute nothing.</returns>
    private static long SumSourceSetBytes(HashSet<string> sourceFiles)
    {
        long total = 0;
        foreach (var file in sourceFiles)
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file that vanished while the folder was scanned simply does not contribute.
            }
        }

        return total;
    }

    /// <summary>
    ///     Formats the difference between the source size and the produced CHD size for the activity
    ///     log and the completion dialog.
    /// </summary>
    /// <param name="sourceBytes">Total size of the input files.</param>
    /// <param name="outputBytes">Total size of the produced CHDs.</param>
    /// <returns>A phrase such as <c>saved 1.5 GB (29.2%)</c>, or an empty string without a source.</returns>
    internal static string FormatSavings(long sourceBytes, long outputBytes)
    {
        if (sourceBytes <= 0) return string.Empty;

        var saved = sourceBytes - outputBytes;
        if (saved == 0) return "no change";

        var percentage = Math.Abs(saved) * 100.0 / sourceBytes;
        var formatted = string.Create(
            CultureInfo.InvariantCulture,
            $"{percentage:0.#}%"
        );

        return saved > 0
            ? $"saved {ChdExplorerItem.FormatSize((ulong)saved)} ({formatted})"
            : $"{ChdExplorerItem.FormatSize((ulong)-saved)} larger ({formatted})";
    }

    /// <summary>
    ///     Removes a failure report left by an earlier batch, so a clean run does not leave a stale
    ///     list behind.
    /// </summary>
    /// <param name="outputFolder">Folder holding the report.</param>
    internal static void DeleteFailureReport(string outputFolder)
    {
        try
        {
            var reportPath = Path.Combine(outputFolder, FailureReportFileName);
            if (File.Exists(reportPath)) File.Delete(reportPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A report that cannot be removed is harmless; the next batch overwrites it.
        }
    }

    /// <summary>
    ///     Writes the inputs that failed to convert to <c>failed-conversions.txt</c> in the output
    ///     folder, so the list survives after the activity log is cleared.
    /// </summary>
    /// <param name="outputFolder">Folder that receives the report.</param>
    /// <param name="inputFolder">Source folder recorded in the report header.</param>
    /// <param name="failedFiles">Inputs that did not convert.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>The report path, or <c>null</c> when there were no failures or it could not be written.</returns>
    internal static async Task<string?> WriteFailureReportAsync(
        string outputFolder,
        string inputFolder,
        IReadOnlyCollection<string> failedFiles,
        CancellationToken token
    )
    {
        if (failedFiles.Count == 0) return null;

        var reportPath = Path.Combine(outputFolder, FailureReportFileName);
        var lines = new List<string>
        {
            "CHD Studio - failed conversions",
            $"Generated: {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}",
            $"Source folder: {inputFolder}",
            $"Output folder: {outputFolder}",
            $"Failed: {failedFiles.Count}",
            string.Empty,
            "See the CHD Studio activity log for the error reported for each file.",
            string.Empty
        };

        lines.AddRange(failedFiles.OrderBy(static f => f, PathComparer));

        try
        {
            await File
                .WriteAllLinesAsync(reportPath, lines, token)
                .ConfigureAwait(false);
            return reportPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Adds one input and the data files it references to the source-size set.</summary>
    /// <param name="file">The input path.</param>
    /// <param name="sourceFiles">Set that receives every source path.</param>
    /// <param name="token">Cancellation token.</param>
    private static async Task AddSourceSetAsync(
        string file,
        HashSet<string> sourceFiles,
        CancellationToken token
    )
    {
        AddFile(file, sourceFiles);

        try
        {
            var ext = Path.GetExtension(file);
            if (ext.Equals(FileExtensions.Cue, StringComparison.OrdinalIgnoreCase))
            {
                AddFiles(
                    await GameFileParser
                        .GetReferencedFilesFromCueAsync(file, static _ => { }, token)
                        .ConfigureAwait(false),
                    sourceFiles
                );
            }
            else if (ext.Equals(FileExtensions.Gdi, StringComparison.OrdinalIgnoreCase))
            {
                AddFiles(
                    await GameFileParser
                        .GetReferencedFilesFromGdiAsync(file, static _ => { }, token)
                        .ConfigureAwait(false),
                    sourceFiles
                );
            }
            else if (ext.Equals(FileExtensions.Toc, StringComparison.OrdinalIgnoreCase))
            {
                AddFiles(
                    await GameFileParser
                        .GetReferencedFilesFromTocAsync(file, static _ => { }, token)
                        .ConfigureAwait(false),
                    sourceFiles
                );
            }
            else if (ext.Equals(FileExtensions.Ccd, StringComparison.OrdinalIgnoreCase))
            {
                AddCcdFiles(file, sourceFiles);
            }
            else if (
                ext.Equals(FileExtensions.Mds, StringComparison.OrdinalIgnoreCase)
                || ext.Equals(FileExtensions.Mdx, StringComparison.OrdinalIgnoreCase)
            )
            {
                AddMdsFiles(file, sourceFiles);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Size accounting is best effort; the conversion reports the real problem.
        }
    }

    /// <summary>Adds a CloneCD set's data files to the source-size set.</summary>
    /// <param name="ccdPath">Path of the descriptor.</param>
    /// <param name="sourceFiles">Set that receives every source path.</param>
    private static void AddCcdFiles(string ccdPath, HashSet<string> sourceFiles)
    {
        try
        {
            var disc = CcdConverter.Parse(ccdPath);
            if (disc.ImgFilePath is not null)
                AddFile(disc.ImgFilePath, sourceFiles);
            if (disc.SubFilePath is not null)
                AddFile(disc.SubFilePath, sourceFiles);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            // An unreadable descriptor contributes only its own length; the conversion reports
            // the real error.
        }

        AddFile(Path.ChangeExtension(ccdPath, ".cdt"), sourceFiles);
    }

    /// <summary>Adds an Alcohol set's data files, including all split volumes, to the set.</summary>
    /// <param name="mdsPath">Path of the descriptor.</param>
    /// <param name="sourceFiles">Set that receives every source path.</param>
    private static void AddMdsFiles(string mdsPath, HashSet<string> sourceFiles)
    {
        try
        {
            var disc = MdsParser.Parse(mdsPath);
            var dataFiles = disc.DataFilePaths.Count > 0
                ? disc.DataFilePaths
                : disc.MdfPath is not null
                    ? [disc.MdfPath]
                    : [];

            foreach (var dataFile in dataFiles)
            {
                var volumes = SplitImageJoiner.TryGetVolumeSet(dataFile);
                if (volumes is null)
                    AddFile(dataFile, sourceFiles);
                else
                    AddFiles(volumes, sourceFiles);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            // Same as a malformed cue: the descriptor's own length still counts.
        }
    }

    /// <summary>Adds every path in a sequence to the source-size set.</summary>
    /// <param name="files">The paths to add.</param>
    /// <param name="sourceFiles">Set that receives every source path.</param>
    private static void AddFiles(IEnumerable<string> files, HashSet<string> sourceFiles)
    {
        foreach (var file in files)
            AddFile(file, sourceFiles);
    }

    /// <summary>Adds one path to the source-size set, expanding split-image volumes.</summary>
    /// <param name="file">The path to add.</param>
    /// <param name="sourceFiles">Set that receives every source path.</param>
    private static void AddFile(string file, HashSet<string> sourceFiles)
    {
        var volumes = SplitImageJoiner.TryGetVolumeSet(file);
        if (volumes is null)
        {
            sourceFiles.Add(file);
            return;
        }

        foreach (var volume in volumes)
            sourceFiles.Add(volume);
    }
}
