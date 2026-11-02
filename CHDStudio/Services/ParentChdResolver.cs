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
        var headerError = Chd.ReadHeader(childPath, out var childHeader);
        if (headerError != ChdError.Chderrnone || childHeader is null)
        {
            return new Resolution(
                null,
                $"The CHD header could not be read ({headerError.GetMessage()}).",
                null,
                false
            );
        }

        if (!childHeader.HasParent)
        {
            return new Resolution(null, null, null, false);
        }

        if (!string.IsNullOrWhiteSpace(explicitParentPath))
            return ValidateExplicitParent(childHeader, explicitParentPath);

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
            foreach (
                var candidate in Directory.EnumerateFiles(
                    directory,
                    "*.chd",
                    SearchOption.TopDirectoryOnly
                )
            )
            {
                if (string.Equals(candidate, childPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (Chd.ReadHeader(candidate, out var candidateHeader) != ChdError.Chderrnone)
                    continue;
                if (candidateHeader is null || !MatchesParent(childHeader, candidateHeader))
                    continue;

                return new Resolution(candidate, null, null, true);
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
    /// <returns>The resolution result.</returns>
    private static Resolution ValidateExplicitParent(
        ChdHeaderInfo childHeader,
        string parentPath
    )
    {
        if (!File.Exists(parentPath))
        {
            return new Resolution(
                null,
                $"The selected parent CHD does not exist: {parentPath}",
                null,
                true
            );
        }

        var error = Chd.ReadHeader(parentPath, out var parentHeader);
        if (error != ChdError.Chderrnone || parentHeader is null)
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

    /// <summary>Returns whether a candidate's hashes match the parent hashes in the child header.</summary>
    /// <param name="childHeader">Header of the child CHD.</param>
    /// <param name="candidateHeader">Header of a possible parent.</param>
    /// <returns>True when the parent SHA-1 or MD5 matches.</returns>
    private static bool MatchesParent(ChdHeaderInfo childHeader, ChdHeaderInfo candidateHeader)
    {
        if (
            childHeader.ParentSha1 is { Length: 20 } parentSha1
            && (
                candidateHeader.Sha1 is { Length: 20 } sha1
                && sha1.AsSpan().SequenceEqual(parentSha1)
                || candidateHeader.RawSha1 is { Length: 20 } rawSha1
                    && rawSha1.AsSpan().SequenceEqual(parentSha1)
            )
        )
        {
            return true;
        }

        return childHeader.ParentMd5 is { Length: 16 } parentMd5
               && candidateHeader.Md5 is { Length: 16 } md5
               && md5.AsSpan().SequenceEqual(parentMd5);
    }
}
