using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CHDStudio.Models;

namespace CHDStudio.Utilities;

/// <summary>
///     Parses a CUE sheet with encoding detection, resolves every referenced file against the filesystem
///     (exact match, then case-insensitive, then zero-padding-tolerant like "(Track 2)" vs "(Track 02)"),
///     and produces a canonical UTF-8 rewrite of the cue that chdman can consume reliably.
///     TOC (cdrdao) descriptors are normalized the same way: their FILE/DATAFILE/AUDIOFILE lines have
///     the referenced name resolved and rewritten while the SWAP/offset/length arguments after the
///     name are preserved verbatim.
/// </summary>
internal static class CueNormalizer
{
    private static readonly Regex TrackNumberRegex = new(
        @"(?<prefix>.*\(Track\s+)(?<num>\d+)(?<suffix>\).*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.NonBacktracking
    );

    private static readonly string[] KnownTrackTypes =
    [
        "BINARY",
        "WAVE",
        "MP3",
        "AIFF",
        "MOTOROLA",
        "AUDIO"
    ];

    /// <summary>Extensions a cue's data track can legitimately be stored under.</summary>
    private static readonly HashSet<string> DataFileExtensions = new(
        [FileExtensions.Bin, FileExtensions.Img, FileExtensions.Iso, FileExtensions.Raw],
        StringComparer.OrdinalIgnoreCase
    );

    /// <summary>Whitespace characters separating descriptor tokens.</summary>
    private static readonly char[] TokenSeparators = [' ', '\t'];

    /// <summary>Compares resolved file paths the way the host file system does.</summary>
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    /// <summary>
    ///     Normalizes the cue at <paramref name="cuePath" />.
    /// </summary>
    /// <param name="cuePath">Path of the .cue or .toc descriptor to normalize.</param>
    /// <param name="token">Cancellation token.</param>
    /// <param name="transform">Optional transform applied to each resolved FILE line.</param>
    internal static async Task<CueNormalizationResult> NormalizeAsync(
        string cuePath,
        CancellationToken token,
        CueFileLineTransform? transform = null
    )
    {
        var (lines, encoding, hasBom) = await GameFileParser
            .ReadLinesWithDetectedEncodingAsync(cuePath, token)
            .ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        var directory = Path.GetDirectoryName(cuePath) ?? string.Empty;
        var references = new List<CueFileReference>();
        var unresolved = new List<string>();
        var canonicalLines = new List<string>(lines.Length);
        var needsRewrite = false;
        var referencesChanged = false;

        // Two FILE lines that name different files but resolve to the same one cannot describe the
        // layout the cue declares: the encoders would treat them as separate images, and the
        // rewrite would collapse them into one and mangle the track lengths. The later line is
        // left unresolved so the caller reports the missing file instead.
        var claimedFiles = new Dictionary<string, string>(PathComparer);

        var isToc = string.Equals(
            Path.GetExtension(cuePath),
            FileExtensions.Toc,
            StringComparison.OrdinalIgnoreCase
        );

        // A descriptor with exactly one reference line describes one data file, which allows a
        // last-resort match by elimination when the recorded name is unusable.
        var isSingleFileCue = CountReferenceLines(lines, isToc) == 1;

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();

            if (isToc)
            {
                if (
                    !GameFileParser.IsTocReferenceLine(trimmedLine)
                    || !GameFileParser.TryGetTocFileNameAndTail(
                        trimmedLine,
                        out var tocName,
                        out var tocTail
                    )
                    || tocName is null
                )
                {
                    canonicalLines.Add(line);
                    continue;
                }

                var tocKind = InferTrackTypeFromExtension(tocName);
                var tocReference = ResolveReference(directory, tocName, tocKind, isSingleFileCue);
                if (tocReference.IsResolved && !TryClaimFile(claimedFiles, tocReference, tocName))
                    tocReference = AsUnresolved(tocReference, tocName);

                references.Add(tocReference);

                if (!tocReference.IsResolved) unresolved.Add(tocName);

                var tocLineName = tocReference.ResolvedName ?? tocName;
                if (tocReference.IsResolved && transform is not null)
                {
                    var transformed = transform(tocReference);
                    if (transformed is not null)
                    {
                        tocLineName = transformed.Value.Name;
                        referencesChanged = true;
                    }
                }

                if (tocReference.WasNameCorrected) referencesChanged = true;

                var canonicalTocLine = BuildCanonicalTocLine(trimmedLine, tocLineName, tocTail);
                if (!string.Equals(canonicalTocLine, trimmedLine, StringComparison.Ordinal))
                    needsRewrite = true;

                canonicalLines.Add(canonicalTocLine);
                continue;
            }

            if (
                !trimmedLine.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase)
                || !GameFileParser.TryGetFileNameFromFileLine(trimmedLine, out var referencedName)
                || referencedName is null
            )
            {
                canonicalLines.Add(line);
                continue;
            }

            // A missing track type token (some cue writers omit "BINARY") is inferred from the
            // referenced file's extension so the canonical rewrite carries a type both chdman and
            // the built-in encoder accept.
            var trackType =
                GetTrackType(trimmedLine) ?? InferTrackTypeFromExtension(referencedName);
            var reference = ResolveReference(directory, referencedName, trackType, isSingleFileCue);
            if (reference.IsResolved && !TryClaimFile(claimedFiles, reference, referencedName))
                reference = AsUnresolved(reference, referencedName);

            references.Add(reference);

            if (!reference.IsResolved) unresolved.Add(referencedName);

            var lineName = reference.ResolvedName ?? referencedName;
            var lineType = reference.TrackType;
            if (reference.IsResolved && transform is not null)
            {
                var transformed = transform(reference);
                if (transformed is not null)
                {
                    lineName = transformed.Value.Name;
                    lineType = transformed.Value.TrackType ?? reference.TrackType;
                    referencesChanged = true;
                }
            }

            if (reference.WasNameCorrected) referencesChanged = true;

            var canonicalLine = BuildCanonicalFileLine(lineName, lineType);
            if (!string.Equals(canonicalLine, trimmedLine, StringComparison.Ordinal)) needsRewrite = true;

            canonicalLines.Add(canonicalLine);
        }

        return new CueNormalizationResult(
            encoding,
            hasBom,
            references,
            unresolved,
            canonicalLines,
            needsRewrite,
            referencesChanged
        );
    }

    /// <summary>
    ///     Writes the canonical cue content to <paramref name="outputPath" /> as UTF-8 (no BOM, CRLF line endings).
    /// </summary>
    /// <param name="outputPath">Destination file path for the canonical cue.</param>
    /// <param name="result">The normalization result whose canonical content is written.</param>
    /// <param name="token">Cancellation token.</param>
    internal static async Task WriteCanonicalCueAsync(
        string outputPath,
        CueNormalizationResult result,
        CancellationToken token
    )
    {
        await File.WriteAllTextAsync(
                outputPath,
                result.CanonicalCueText,
                new UTF8Encoding(false),
                token
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Resolves one FILE line reference against the filesystem: as written, then beside the cue,
    ///     then by extension swap, then by elimination for single-file data cues.
    /// </summary>
    /// <param name="directory">Directory holding the cue.</param>
    /// <param name="referencedName">Name exactly as written in the cue.</param>
    /// <param name="trackType">Track type token from the FILE line, or null.</param>
    /// <param name="isSingleFileCue">True when the cue has exactly one FILE line.</param>
    /// <returns>The resolved reference (unresolved when no strategy matched).</returns>
    private static CueFileReference ResolveReference(
        string directory,
        string referencedName,
        string? trackType,
        bool isSingleFileCue
    )
    {
        var fullPath = Path.Combine(directory, referencedName);
        var referencedFileName = Path.GetFileName(fullPath);
        var referencedDirectory = Path.GetDirectoryName(fullPath) ?? directory;

        // Strategy 1: the reference as written, resolved wherever it points.
        var match = FindMatch(
            GetFiles(referencedDirectory),
            referencedFileName,
            out var wasNameCorrected
        );

        var cueDirectoryFiles = GetFiles(directory);

        // Strategy 2: the same file name next to the cue, ignoring any directory the reference
        // carried. Cues written elsewhere keep that machine's absolute path - real examples include
        // "C:\DOCUMENTS AND SETTINGS\BILL\DESKTOP\..." - which resolves nowhere here even though the
        // data file is sitting beside the cue.
        if (
            match is null
            && !string.Equals(referencedDirectory, directory, StringComparison.OrdinalIgnoreCase)
        )
        {
            match = FindMatch(cueDirectoryFiles, referencedFileName, out wasNameCorrected);
            if (match is not null) wasNameCorrected = true;
        }

        // Strategy 3: same base name, different extension. Rips get re-saved between .bin, .img and
        // .iso without the cue being updated. Restricted to the data track so a missing WAVE or MP3
        // audio track is never silently answered with the disc image.
        if (match is null && IsBinaryTrack(trackType))
        {
            match = FindExtensionSwapMatch(cueDirectoryFiles, referencedFileName);
            wasNameCorrected = match is not null;
        }

        // Strategy 4: a cue with a single FILE line, sitting next to exactly one data file, can only
        // mean that file - however little the recorded name resembles it ("SOULEDGE.bin",
        // "Legend Of Legaia Iso"). Restricted to the data track so a missing WAVE or MP3 audio
        // track is never silently answered with the disc image.
        if (match is null && isSingleFileCue && IsBinaryTrack(trackType))
        {
            var dataFiles = cueDirectoryFiles
                .Where(static f => DataFileExtensions.Contains(Path.GetExtension(f)))
                .ToList();
            if (dataFiles.Count == 1)
            {
                match = dataFiles[0];
                wasNameCorrected = true;
            }
        }

        if (match is null)
        {
            return new CueFileReference(
                referencedName,
                null,
                fullPath,
                trackType,
                false,
                directory
            );
        }

        // Anchor the record on the file that was actually found, so a redirected reference reports
        // the real path rather than the one the cue asked for.
        return new CueFileReference(
            referencedName,
            Path.GetRelativePath(directory, match),
            match,
            trackType,
            wasNameCorrected,
            directory
        );
    }

    /// <summary>
    ///     Claims the on-disk file behind a resolved reference. A repeated reference to the same
    ///     file is accepted only when the cue names it the same way; a different name for the same
    ///     file is rejected because the encoders would treat the two FILE lines as separate images
    ///     and the rewrite would silently merge them.
    /// </summary>
    /// <param name="claimedFiles">Resolved full path to the name that first claimed it.</param>
    /// <param name="reference">The resolved reference to claim.</param>
    /// <param name="referencedName">File name exactly as written in the descriptor.</param>
    /// <returns>True when the reference may keep its resolved file.</returns>
    private static bool TryClaimFile(
        Dictionary<string, string> claimedFiles,
        CueFileReference reference,
        string referencedName
    )
    {
        var path = reference.ResolvedFullPath;
        if (claimedFiles.TryGetValue(path, out var first))
            return IsSameWrittenName(first, referencedName);

        claimedFiles[path] = referencedName;
        return true;
    }

    /// <summary>
    ///     Returns whether two cue references name the same file the same way, tolerating the
    ///     separators and "./" prefixes cue writers use interchangeably.
    /// </summary>
    /// <param name="left">First referenced name.</param>
    /// <param name="right">Second referenced name.</param>
    /// <returns>True when both references mean the same written file name.</returns>
    private static bool IsSameWrittenName(string left, string right)
    {
        return string.Equals(
            NormalizeWrittenName(left),
            NormalizeWrittenName(right),
            StringComparison.OrdinalIgnoreCase
        );
    }

    /// <summary>
    ///     Normalizes the separator style and a leading "./" away from a referenced file name.
    /// </summary>
    /// <param name="name">Referenced file name as written.</param>
    /// <returns>The comparable form.</returns>
    private static string NormalizeWrittenName(string name)
    {
        var normalized = name.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];

        return normalized;
    }

    /// <summary>
    ///     Returns a copy of a resolved reference that is reported as unresolved.
    /// </summary>
    /// <param name="reference">The reference whose file is already claimed by another name.</param>
    /// <param name="referencedName">File name exactly as written in the descriptor.</param>
    /// <returns>The unresolved reference.</returns>
    private static CueFileReference AsUnresolved(CueFileReference reference, string referencedName) =>
        new(
            referencedName,
            null,
            reference.FullPath,
            reference.TrackType,
            false,
            reference.CueDirectory
        );

    /// <summary>
    ///     Counts reference lines that carry a parseable file name: FILE lines for cues, and
    ///     FILE/DATAFILE/AUDIOFILE lines for cdrdao TOCs.
    /// </summary>
    /// <param name="lines">Lines of the descriptor.</param>
    /// <param name="isToc">True when the descriptor is a cdrdao TOC.</param>
    /// <returns>The number of usable reference lines.</returns>
    private static int CountReferenceLines(string[] lines, bool isToc)
    {
        var count = 0;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (isToc)
            {
                if (
                    GameFileParser.IsTocReferenceLine(trimmed)
                    && GameFileParser.TryGetTocFileNameAndTail(trimmed, out var name, out _)
                    && name is not null
                )
                {
                    count++;
                }

                continue;
            }

            if (
                trimmed.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase)
                && GameFileParser.TryGetFileNameFromFileLine(trimmed, out var fileName)
                && fileName is not null
            )
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    ///     Returns the files in <paramref name="directory" />, or an empty array when it cannot be
    ///     listed.
    /// </summary>
    /// <param name="directory">Directory to enumerate.</param>
    /// <returns>The file paths, or an empty array.</returns>
    private static string[] GetFiles(string directory)
    {
        try
        {
            return Directory.Exists(directory) ? Directory.GetFiles(directory) : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    ///     Resolves <paramref name="fileName" /> against <paramref name="files" /> by exact name, then
    ///     case-insensitively, then tolerating zero-padding differences in a "(Track N)" suffix.
    /// </summary>
    private static string? FindMatch(string[] files, string fileName, out bool wasNameCorrected)
    {
        wasNameCorrected = false;
        if (files.Length == 0) return null;

        var match =
            files.FirstOrDefault(f =>
                string.Equals(Path.GetFileName(f), fileName, StringComparison.Ordinal)
            )
            ?? files.FirstOrDefault(f =>
                string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase)
            );
        if (match is not null) return match;

        match = FindPadTolerantMatch(files, fileName);
        wasNameCorrected = match is not null;

        return match;
    }

    /// <summary>
    ///     Finds a file with the same base name as <paramref name="fileName" /> but a different disc
    ///     image extension.
    /// </summary>
    private static string? FindExtensionSwapMatch(string[] files, string fileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        if (baseName.Length == 0) return null;

        return files.FirstOrDefault(f =>
            DataFileExtensions.Contains(Path.GetExtension(f))
            && string.Equals(
                Path.GetFileNameWithoutExtension(f),
                baseName,
                StringComparison.OrdinalIgnoreCase
            )
        );
    }

    /// <summary>
    ///     Returns whether the track is a data track (no type token or BINARY).
    /// </summary>
    /// <param name="trackType">Track type token from the FILE line, or null.</param>
    /// <returns><see langword="true" /> for data tracks.</returns>
    private static bool IsBinaryTrack(string? trackType)
    {
        return trackType is null || string.Equals(trackType, "BINARY", StringComparison.Ordinal);
    }

    /// <summary>
    ///     Finds a file whose "(Track N)" suffix differs from the reference only by zero-padding.
    /// </summary>
    /// <param name="files">Files in the directory being searched.</param>
    /// <param name="fileName">The referenced name.</param>
    /// <returns>The matching file, or null.</returns>
    private static string? FindPadTolerantMatch(string[] files, string fileName)
    {
        var match = TrackNumberRegex.Match(fileName);
        if (!match.Success) return null;

        if (
            !int.TryParse(
                match.Groups["num"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var trackNumber
            )
        )
        {
            return null;
        }

        var variants = new[]
        {
            trackNumber.ToString(CultureInfo.InvariantCulture),
            trackNumber.ToString("D2", CultureInfo.InvariantCulture),
            trackNumber.ToString("D3", CultureInfo.InvariantCulture)
        };

        foreach (var variant in variants)
        {
            var candidateName =
                match.Groups["prefix"].Value + variant + match.Groups["suffix"].Value;
            var found = files.FirstOrDefault(f =>
                string.Equals(
                    Path.GetFileName(f),
                    candidateName,
                    StringComparison.OrdinalIgnoreCase
                )
            );
            if (found is not null) return found;
        }

        return null;
    }

    /// <summary>
    ///     Builds the canonical quoted FILE line for a name and optional track type.
    /// </summary>
    /// <param name="name">The resolved file name to write.</param>
    /// <param name="trackType">Track type token, or null.</param>
    /// <returns>The canonical FILE line.</returns>
    private static string BuildCanonicalFileLine(string name, string? trackType)
    {
        return trackType is null ? $"FILE \"{name}\"" : $"FILE \"{name}\" {trackType}";
    }

    /// <summary>
    ///     Builds the canonical cdrdao TOC reference line: the original keyword (FILE, DATAFILE or
    ///     AUDIOFILE), the resolved name quoted, and the trailing argument tokens (SWAP, #decimal
    ///     or MSF offsets/lengths) preserved verbatim — they describe positions inside the file,
    ///     which copying the file does not change.
    /// </summary>
    /// <param name="trimmedLine">The original trimmed line.</param>
    /// <param name="name">The resolved file name to write.</param>
    /// <param name="tail">The argument tokens after the name (possibly empty).</param>
    /// <returns>The canonical TOC reference line.</returns>
    private static string BuildCanonicalTocLine(string trimmedLine, string name, string tail)
    {
        var keywordEnd = trimmedLine.IndexOfAny(TokenSeparators);
        var keyword = keywordEnd < 0 ? trimmedLine : trimmedLine[..keywordEnd];
        return tail.Length == 0 ? $"{keyword} \"{name}\"" : $"{keyword} \"{name}\" {tail}";
    }

    /// <summary>
    ///     Infers the track type token for a FILE line that does not carry one, from the referenced
    ///     file's extension. Returns null for unknown extensions so the line is left untouched.
    /// </summary>
    /// <param name="name">The referenced (or resolved) file name.</param>
    /// <returns>The inferred type token, or null.</returns>
    private static string? InferTrackTypeFromExtension(string name)
    {
        return Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".wav" => "WAVE",
            ".mp3" => "MP3",
            ".aif" or ".aiff" => "AIFF",
            ".bin" or ".img" or ".iso" or ".raw" => "BINARY",
            _ => null,
        };
    }

    /// <summary>
    ///     Extracts the track type token that follows the quoted file name, if any.
    /// </summary>
    /// <param name="trimmedFileLine">A trimmed FILE line.</param>
    /// <returns>The track type token, or null.</returns>
    private static string? GetTrackType(string trimmedFileLine)
    {
        var firstQuote = trimmedFileLine.IndexOf('"');
        var lastQuote = trimmedFileLine.LastIndexOf('"');

        string tail;
        if (firstQuote != -1 && lastQuote > firstQuote)
            tail = trimmedFileLine[(lastQuote + 1)..].Trim();
        else
            tail = trimmedFileLine;

        // The track type is the first known type token anywhere in the tail — some descriptors
        // (e.g. cdrdao TOCs) append extra columns after the type.
        foreach (var token in tail.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (KnownTrackTypes.Contains(token, StringComparer.OrdinalIgnoreCase))
                return token.ToUpperInvariant();
        }

        return null;
    }
}