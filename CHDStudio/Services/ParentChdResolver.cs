using System.Buffers.Binary;
using CHDSharp;
using CHDSharp.Models;

namespace CHDStudio.Services;

/// <summary>
///     Resolves the parent of a differential (child) CHD. An explicit parent path is accepted
///     when it is a readable CHD; otherwise sibling <c>.chd</c> files are matched against the
///     parent SHA-1/MD5 stored in the child header.
/// </summary>
internal static class ParentChdResolver
{
    /// <summary>Signature at the start of every CHD file.</summary>
    private static ReadOnlySpan<byte> Magic => "MComprHD"u8;

    /// <summary>On-disk header lengths for CHD versions 1-5, indexed by version.</summary>
    private static readonly uint[] HeaderLengths = [0, 76, 80, 120, 108, 124];

    /// <summary>Size of the largest CHD header (V5).</summary>
    private const int MaxHeaderLength = 124;

    /// <summary>Result of resolving a parent CHD.</summary>
    /// <param name="ParentPath">Resolved parent path, or <c>null</c> when none was found.</param>
    /// <param name="Error">User-facing failure reason, or <c>null</c> on success.</param>
    /// <param name="Warning">Non-fatal warning (for example an explicit parent whose hash differs).</param>
    /// <param name="IsRequired">True when the child header says a parent is required.</param>
    internal sealed record Resolution(
        string? ParentPath,
        string? Error,
        string? Warning,
        bool IsRequired
    );

    /// <summary>
    ///     Ordered files and their precomputed parent resolutions for a batch operation.
    /// </summary>
    /// <param name="OrderedFiles">Batch files ordered so children come before their parents.</param>
    /// <param name="Resolutions">Parent resolutions keyed by the normalized file path.</param>
    internal sealed record BatchPlan(
        IReadOnlyList<string> OrderedFiles,
        IReadOnlyDictionary<string, Resolution> Resolutions
    )
    {
        /// <summary>Returns the parent resolution computed while preparing the batch.</summary>
        /// <param name="file">Full path of a file passed to <see cref="PrepareBatch" />.</param>
        /// <returns>The precomputed resolution for the file.</returns>
        internal Resolution ResolutionFor(string file)
        {
            return Resolutions[NormalizePath(file)];
        }
    }

    /// <summary>
    ///     Resolves the parent for <paramref name="childPath" />. When
    ///     <paramref name="explicitParentPath" /> is supplied it is validated and returned;
    ///     otherwise sibling CHD files are searched for a matching parent hash.
    /// </summary>
    /// <param name="childPath">Path of the child CHD.</param>
    /// <param name="explicitParentPath">Optional user-selected parent path.</param>
    /// <param name="autoDetect">True to search sibling CHD files when no explicit parent is supplied.</param>
    /// <returns>The resolution result.</returns>
    internal static Resolution Resolve(
        string childPath,
        string? explicitParentPath = null,
        bool autoDetect = true
    )
    {
        return ResolveCore(
            childPath,
            explicitParentPath,
            autoDetect,
            new HeaderCache(GetPathComparer())
        );
    }

    /// <summary>
    ///     Resolves every file in a batch and orders the batch so child CHDs are processed before
    ///     the parents they depend on. Header reads are cached for the whole batch, so a folder with
    ///     many children only reads each sibling header once. Call this off the UI thread: it
    ///     performs file I/O.
    /// </summary>
    /// <param name="files">Full paths of the batch's CHD files.</param>
    /// <param name="explicitParentPath">Optional user-selected parent path.</param>
    /// <param name="autoDetect">True to search sibling CHD files when no explicit parent is supplied.</param>
    /// <param name="cancellationToken">Token that stops the preparation between files.</param>
    /// <returns>The ordered files and their resolutions.</returns>
    internal static BatchPlan PrepareBatch(
        IReadOnlyList<string> files,
        string? explicitParentPath,
        bool autoDetect,
        CancellationToken cancellationToken = default
    )
    {
        var comparer = GetPathComparer();
        var cache = new HeaderCache(comparer);
        var resolutions = new Dictionary<string, Resolution>(comparer);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            resolutions[NormalizePath(file)] = ResolveCore(
                file,
                explicitParentPath,
                autoDetect,
                cache
            );
        }

        var batchFiles = new HashSet<string>(
            files.Select(static file => NormalizePath(file)),
            comparer
        );
        var parentByFile = new Dictionary<string, string?>(comparer);
        foreach (var file in files)
        {
            var normalized = NormalizePath(file);
            var resolution = resolutions[normalized];
            parentByFile[normalized] =
                resolution.ParentPath is { } parent && batchFiles.Contains(NormalizePath(parent))
                    ? NormalizePath(parent)
                    : null;
        }

        var depths = new Dictionary<string, int>(comparer);
        foreach (var file in files)
            _ = GetDepth(NormalizePath(file), new HashSet<string>(comparer));

        var orderedFiles = files
            .OrderByDescending(file => depths.GetValueOrDefault(NormalizePath(file), 0))
            .ToArray();

        return new BatchPlan(orderedFiles, resolutions);

        int GetDepth(string path, HashSet<string> visiting)
        {
            if (depths.TryGetValue(path, out var known))
                return known;

            // A cycle can only come from a malformed parent chain; treat it as a root so the
            // batch still runs instead of recursing forever.
            if (!visiting.Add(path))
                return 0;

            var depth =
                parentByFile.TryGetValue(path, out var parent) && parent is not null
                    ? GetDepth(parent, visiting) + 1
                    : 0;

            visiting.Remove(path);
            depths[path] = depth;
            return depth;
        }
    }

    /// <summary>
    ///     Orders a batch so child CHDs are processed before the parents they depend on. Moving or
    ///     deleting a parent earlier in the batch (verification's Success folder, extraction's
    ///     delete-original option) must not make a later child lose its parent.
    /// </summary>
    /// <param name="files">Full paths of the batch's CHD files.</param>
    /// <param name="explicitParentPath">Optional user-selected parent path.</param>
    /// <param name="autoDetect">True to search sibling CHD files when no explicit parent is supplied.</param>
    /// <returns>The same paths, deepest children first.</returns>
    internal static IReadOnlyList<string> OrderChildrenFirst(
        IReadOnlyList<string> files,
        string? explicitParentPath,
        bool autoDetect
    )
    {
        return PrepareBatch(files, explicitParentPath, autoDetect).OrderedFiles;
    }

    /// <summary>Resolves a parent using a shared header cache.</summary>
    /// <param name="childPath">Path of the child CHD.</param>
    /// <param name="explicitParentPath">Optional user-selected parent path.</param>
    /// <param name="autoDetect">True to search sibling CHD files when no explicit parent is supplied.</param>
    /// <param name="cache">Header cache shared by the batch.</param>
    /// <returns>The resolution result.</returns>
    private static Resolution ResolveCore(
        string childPath,
        string? explicitParentPath,
        bool autoDetect,
        HeaderCache cache
    )
    {
        var headerError = cache.Read(childPath, out var childHeader);
        if (headerError != ChdError.Chderrnone)
        {
            return new Resolution(
                null,
                $"The CHD header could not be read ({headerError.GetMessage()}).",
                null,
                false
            );
        }

        if (!HasParent(childHeader))
        {
            return new Resolution(null, null, null, false);
        }

        if (!string.IsNullOrWhiteSpace(explicitParentPath))
            return ValidateExplicitParent(childHeader, explicitParentPath, cache);

        if (!autoDetect)
        {
            return new Resolution(
                null,
                "This is a child CHD. Select its parent CHD to verify it.",
                null,
                true
            );
        }

        var directory = Path.GetDirectoryName(childPath);
        if (string.IsNullOrEmpty(directory))
        {
            return new Resolution(
                null,
                "This is a child CHD, but its folder could not be determined to search for a parent.",
                null,
                true
            );
        }

        try
        {
            foreach (var (candidatePath, candidateHeader) in cache.ReadDirectory(directory))
            {
                if (PathsEqual(candidatePath, childPath)) continue;

                if (MatchesParent(childHeader, candidateHeader))
                    return new Resolution(candidatePath, null, null, true);
            }
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return new Resolution(
                null,
                $"This is a child CHD, but its folder could not be searched for a parent: {ex.Message}",
                null,
                true
            );
        }

        return new Resolution(
            null,
            "This is a child CHD. Select its parent CHD, or place the parent in the same folder as the child.",
            null,
            true
        );
    }

    /// <summary>Validates a user-selected parent and returns it when it is a readable CHD.</summary>
    /// <param name="childHeader">Header of the child CHD.</param>
    /// <param name="parentPath">User-selected parent path.</param>
    /// <param name="cache">Header cache shared by the batch.</param>
    /// <returns>The resolution result.</returns>
    private static Resolution ValidateExplicitParent(
        Header childHeader,
        string parentPath,
        HeaderCache cache
    )
    {
        var error = cache.Read(parentPath, out var parentHeader);
        if (error == ChdError.Chderrfilenotfound)
        {
            return new Resolution(
                null,
                $"The selected parent CHD does not exist: {parentPath}",
                null,
                true
            );
        }

        if (error != ChdError.Chderrnone)
        {
            return new Resolution(
                null,
                $"The selected parent is not a readable CHD ({error.GetMessage()}): {parentPath}",
                null,
                true
            );
        }

        var warning = MatchesParent(childHeader, parentHeader)
            ? null
            : "The selected parent CHD does not match the parent hash stored in the child header. Verification may fail.";

        return new Resolution(parentPath, null, warning, true);
    }

    /// <summary>Returns whether the header stores a nonzero parent hash.</summary>
    /// <param name="header">Header to inspect.</param>
    /// <returns>True when the CHD is a differential child.</returns>
    private static bool HasParent(Header header)
    {
        return HasHash(header.ParentSha1) || HasHash(header.ParentMd5);
    }

    /// <summary>Returns whether a hash field is present and not all zeroes.</summary>
    /// <param name="hash">Hash bytes to inspect.</param>
    /// <returns>True when the hash contains at least one nonzero byte.</returns>
    private static bool HasHash(byte[]? hash)
    {
        if (hash is null) return false;

        foreach (var value in hash)
            if (value != 0)
                return true;

        return false;
    }

    /// <summary>Returns whether a candidate's hashes match the parent hashes in the child header.</summary>
    /// <param name="childHeader">Header of the child CHD.</param>
    /// <param name="candidateHeader">Header of a possible parent.</param>
    /// <returns>True when the parent SHA-1 or MD5 matches.</returns>
    private static bool MatchesParent(Header childHeader, Header candidateHeader)
    {
        if (HasHash(childHeader.ParentSha1))
        {
            return childHeader.ParentSha1 is { Length: 20 } parentSha1
                && (
                    candidateHeader.Sha1 is { Length: 20 } sha1
                    && sha1.AsSpan().SequenceEqual(parentSha1)
                    || candidateHeader.RawSha1 is { Length: 20 } rawSha1
                    && rawSha1.AsSpan().SequenceEqual(parentSha1)
                );
        }

        return childHeader.ParentMd5 is { Length: 16 } parentMd5
               && candidateHeader.Md5 is { Length: 16 } md5
               && md5.AsSpan().SequenceEqual(parentMd5);
    }

    /// <summary>Returns the path comparer for the current platform.</summary>
    /// <returns>Ordinal-ignore-case on Windows, ordinal elsewhere.</returns>
    private static IEqualityComparer<string> GetPathComparer()
    {
        return OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
    }

    /// <summary>Returns whether two paths refer to the same file on the current platform.</summary>
    /// <param name="left">First path.</param>
    /// <param name="right">Second path.</param>
    /// <returns>True when the paths are equal under the platform's path comparison.</returns>
    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            left,
            right,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal
        );
    }

    /// <summary>Returns a full path for comparison, falling back to the input when it is invalid.</summary>
    /// <param name="path">Path to normalize.</param>
    /// <returns>The normalized path, or the original path when normalization fails.</returns>
    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex)
            when (ex is ArgumentException or IOException or NotSupportedException
                or UnauthorizedAccessException
            )
        {
            return path;
        }
    }

    /// <summary>
    ///     Reads the fixed-size CHD header only. The compressed hunk map is deliberately not parsed:
    ///     parent resolution needs six small hash fields, and parsing the map for every file in a
    ///     large folder is what made batch preparation block for over a minute.
    /// </summary>
    /// <param name="path">Path of the CHD file.</param>
    /// <param name="header">Receives the parsed hash fields on success.</param>
    /// <returns><see cref="ChdError.Chderrnone" /> on success, otherwise the failure reason.</returns>
    private static ChdError TryReadHeader(string path, out Header header)
    {
        header = default;
        if (string.IsNullOrEmpty(path)) return ChdError.Chderrinvalidparameter;

        FileStream stream;
        try
        {
            stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                MaxHeaderLength,
                FileOptions.None
            );
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return ChdError.Chderrfilenotfound;
        }
        catch (UnauthorizedAccessException)
        {
            return ChdError.Chderrcannotopenfile;
        }
        catch (IOException)
        {
            return ChdError.Chderrcannotopenfile;
        }

        using (stream)
        {
            Span<byte> buffer = stackalloc byte[MaxHeaderLength];
            uint version;
            try
            {
                stream.ReadExactly(buffer[..16]);

                if (!buffer[..8].SequenceEqual(Magic)) return ChdError.Chderrinvalidfile;

                var length = BinaryPrimitives.ReadUInt32BigEndian(buffer[8..12]);
                version = BinaryPrimitives.ReadUInt32BigEndian(buffer[12..16]);
                if (version is < 1 or > 5) return ChdError.Chderrunsupportedversion;
                if (length != HeaderLengths[version]) return ChdError.Chderrinvalidfile;

                stream.ReadExactly(buffer[16..(int)length]);
            }
            catch (EndOfStreamException)
            {
                return ChdError.Chderrinvalidfile;
            }
            catch (IOException)
            {
                return ChdError.Chderrreaderror;
            }
            catch (UnauthorizedAccessException)
            {
                return ChdError.Chderrcannotopenfile;
            }

            header = ParseHeader(version, buffer);
            return ChdError.Chderrnone;
        }
    }

    /// <summary>Copies the parent-relevant hash fields out of a raw CHD header.</summary>
    /// <param name="version">CHD version, already validated to 1-5.</param>
    /// <param name="buffer">Raw header bytes.</param>
    /// <returns>The parsed header snapshot.</returns>
    private static Header ParseHeader(uint version, ReadOnlySpan<byte> buffer)
    {
        return version switch
        {
            1 or 2 => new Header(
                Md5: buffer[44..60].ToArray(),
                ParentMd5: buffer[60..76].ToArray(),
                Sha1: null,
                RawSha1: null,
                ParentSha1: null
            ),
            3 => new Header(
                Md5: buffer[44..60].ToArray(),
                ParentMd5: buffer[60..76].ToArray(),
                Sha1: null,
                RawSha1: buffer[80..100].ToArray(),
                ParentSha1: buffer[100..120].ToArray()
            ),
            4 => new Header(
                Md5: null,
                ParentMd5: null,
                Sha1: buffer[48..68].ToArray(),
                RawSha1: buffer[88..108].ToArray(),
                ParentSha1: buffer[68..88].ToArray()
            ),
            _ => new Header(
                Md5: null,
                ParentMd5: null,
                Sha1: buffer[84..104].ToArray(),
                RawSha1: buffer[64..84].ToArray(),
                ParentSha1: buffer[104..124].ToArray()
            ),
        };
    }

    /// <summary>Parent-relevant fields of a CHD header.</summary>
    /// <param name="Md5">MD5 of the raw image data (V1-V3).</param>
    /// <param name="ParentMd5">MD5 of the parent image (V1-V3).</param>
    /// <param name="Sha1">Combined SHA-1 (V4/V5), or the raw SHA-1 for V3.</param>
    /// <param name="RawSha1">SHA-1 of the raw image data (V3-V5).</param>
    /// <param name="ParentSha1">SHA-1 of the parent image (V3-V5).</param>
    private readonly record struct Header(
        byte[]? Md5,
        byte[]? ParentMd5,
        byte[]? Sha1,
        byte[]? RawSha1,
        byte[]? ParentSha1
    );

    /// <summary>Cached header read result for one path.</summary>
    /// <param name="Error">Read result.</param>
    /// <param name="Header">Parsed header, valid when <paramref name="Error" /> is none.</param>
    private readonly record struct CachedHeader(ChdError Error, Header Header);

    /// <summary>
    ///     Caches header reads for one resolution pass, so sibling scans do not re-open and re-read
    ///     the same files.
    /// </summary>
    private sealed class HeaderCache
    {
        private readonly Dictionary<string, CachedHeader> _files;
        private readonly Dictionary<string, List<(string Path, Header Header)>> _directories;

        /// <summary>Initializes a new header cache.</summary>
        /// <param name="comparer">Path comparer for the current platform.</param>
        internal HeaderCache(IEqualityComparer<string> comparer)
        {
            _files = new Dictionary<string, CachedHeader>(comparer);
            _directories = new Dictionary<string, List<(string Path, Header Header)>>(comparer);
        }

        /// <summary>Reads a header, returning the cached result when the path was already read.</summary>
        /// <param name="path">Path of the CHD file.</param>
        /// <param name="header">Receives the parsed header on success.</param>
        /// <returns><see cref="ChdError.Chderrnone" /> on success, otherwise the failure reason.</returns>
        internal ChdError Read(string path, out Header header)
        {
            var key = NormalizePath(path);
            if (_files.TryGetValue(key, out var cached))
            {
                header = cached.Header;
                return cached.Error;
            }

            var error = TryReadHeader(path, out header);
            _files[key] = new CachedHeader(error, header);
            return error;
        }

        /// <summary>Reads every readable sibling CHD header in a folder once.</summary>
        /// <param name="directory">Folder to enumerate.</param>
        /// <returns>The readable sibling files and their headers.</returns>
        internal IReadOnlyList<(string Path, Header Header)> ReadDirectory(string directory)
        {
            var key = NormalizePath(directory);
            if (_directories.TryGetValue(key, out var cached)) return cached;

            var candidates = new List<(string Path, Header Header)>();
            foreach (
                var file in Directory.EnumerateFiles(
                    directory,
                    "*.chd",
                    SearchOption.TopDirectoryOnly
                )
            )
            {
                if (Read(file, out var header) == ChdError.Chderrnone)
                    candidates.Add((file, header));
            }

            _directories[key] = candidates;
            return candidates;
        }
    }
}
