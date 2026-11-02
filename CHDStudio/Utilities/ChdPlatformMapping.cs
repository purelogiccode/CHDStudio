using CHDSharp;
using CHDSharp.Models;
using VideoGameFileSystemParser.Models;

namespace CHDStudio.Utilities;

/// <summary>
///     Maps CHDSharp's detected disc platform to the VideoGameFileSystemParser console type used by
///     the Explorer, and formats detection results for the Image Info report.
/// </summary>
internal static class ChdPlatformMapping
{
    /// <summary>Detects the platform of a CHD, returning <c>null</c> when detection fails.</summary>
    /// <param name="chdPath">Path of the CHD image.</param>
    /// <param name="parentPath">Optional parent CHD for a differential child image.</param>
    /// <returns>The detection result, or <c>null</c>.</returns>
    internal static DiscPlatformInfo? TryDetect(string chdPath, string? parentPath = null)
    {
        try
        {
            var info = DiscDetector.DetectChd(chdPath, parentPath);
            return info.Platform == DiscPlatform.Unknown ? null : info;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>Maps a detected platform to the matching Explorer parser, or <c>null</c>.</summary>
    /// <param name="platform">The detected platform.</param>
    /// <returns>The matching console type, or <c>null</c> when no specific parser applies.</returns>
    internal static ConsoleType? ToConsoleType(DiscPlatform platform)
    {
        return platform switch
        {
            DiscPlatform.ThreeDo => ConsoleType.ThreeDo,
            DiscPlatform.MegaCd => ConsoleType.SegaGenesisCd,
            DiscPlatform.Saturn => ConsoleType.Saturn,
            DiscPlatform.Dreamcast => ConsoleType.Dreamcast,
            DiscPlatform.Ps1 => ConsoleType.Ps1,
            DiscPlatform.Ps2 => ConsoleType.Ps2,
            DiscPlatform.Psp => ConsoleType.Psp,
            DiscPlatform.NeoGeoCd => ConsoleType.NeoGeoCd,
            DiscPlatform.PcEngine => ConsoleType.PcEngineCd,
            DiscPlatform.Dvd => ConsoleType.GenericIso9660,
            DiscPlatform.GenericCd => ConsoleType.GenericIsoRaw2352,
            _ => null
        };
    }

    /// <summary>Formats a detection result for the Image Info report.</summary>
    /// <param name="info">The detection result.</param>
    /// <returns>A multi-line description.</returns>
    internal static string Describe(DiscPlatformInfo info)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append("Platform:    ").AppendLine(info.Name);
        if (!string.IsNullOrWhiteSpace(info.Title))
            builder.Append("Title:       ").AppendLine(info.Title);
        if (!string.IsNullOrWhiteSpace(info.ManufacturerId))
            builder.Append("Serial:      ").AppendLine(info.ManufacturerId);
        if (!string.IsNullOrWhiteSpace(info.Source))
            builder.Append("Detected by: ").AppendLine(info.Source);
        return builder.ToString();
    }
}
