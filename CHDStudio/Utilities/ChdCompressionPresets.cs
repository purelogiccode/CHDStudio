using System.Diagnostics.CodeAnalysis;
using CHDSharp.Encoder;
using CHDSharp.Models;
using CHDStudio.Models;

namespace CHDStudio.Utilities;

/// <summary>
///     Maps a <see cref="ChdCompressionPreset" /> to the codec tags understood by CHDSharp and
///     chdman for a particular creation command.
/// </summary>
[SuppressMessage("ReSharper", "UnusedMember.Global")]
internal static class ChdCompressionPresets
{
    /// <summary>Returns whether the preset can be used for the given creation command.</summary>
    /// <param name="preset">The selected preset.</param>
    /// <param name="command">The chdman-style creation command (e.g. <c>createcd</c>).</param>
    /// <returns>
    ///     False only for uncompressed laserdisc output. Laserdiscs require the AVHuff codec;
    ///     CD images support <c>none</c> in both chdman and the built-in encoder.
    /// </returns>
    internal static bool IsSupported(ChdCompressionPreset preset, string command)
    {
        return preset != ChdCompressionPreset.Uncompressed
               || command is not "createld";
    }

    /// <summary>
    ///     Returns the codec tags for a preset, or <c>null</c> when the encoder's default list
    ///     should be used.
    /// </summary>
    /// <param name="preset">The selected preset.</param>
    /// <param name="command">The creation command.</param>
    /// <param name="platform">Detected platform for the <see cref="ChdCompressionPreset.Auto" /> preset.</param>
    /// <returns>The codec tags, or <c>null</c> for the command's defaults.</returns>
    internal static uint[]? GetCodecTags(
        ChdCompressionPreset preset,
        string command,
        DiscPlatform? platform = null
    )
    {
        if (string.Equals(command, "createld", StringComparison.Ordinal))
            return [CodecTags.Avhu];

        if (!IsSupported(preset, command))
            return null;

        return preset switch
        {
            ChdCompressionPreset.Default => null,
            ChdCompressionPreset.Compatible => string.Equals(command, "createcd", StringComparison.Ordinal)
                ? [CodecTags.Cdzl]
                : [CodecTags.Zlib],
            ChdCompressionPreset.Smallest => string.Equals(command, "createcd", StringComparison.Ordinal)
                ? [CodecTags.Cdlz, CodecTags.Cdzl, CodecTags.Cdzs, CodecTags.Cdfl]
                // CHD v5 has four codec slots and chdman accepts at most four tags. Keeping the
                // same four for the built-in encoder makes both encoders produce the same preset.
                : [CodecTags.Zstd, CodecTags.Lzma, CodecTags.Zlib, CodecTags.Huff],
            ChdCompressionPreset.Uncompressed => [CodecTags.None],
            ChdCompressionPreset.Auto => GetAutoCodecTags(command, platform),
            _ => null
        };
    }

    /// <summary>Returns a platform preset only when its codecs match the command's media type.</summary>
    /// <param name="command">The creation command.</param>
    /// <param name="platform">Detected platform, or <c>null</c>.</param>
    /// <returns>Compatible codec tags, or <c>null</c> for the command's defaults.</returns>
    private static uint[]? GetAutoCodecTags(string command, DiscPlatform? platform)
    {
        if (platform is null)
            return null;

        var detected = platform.Value;
        var tags = PlatformDetector.AutoCodecs(
            detected,
            string.Equals(command, "createdvd", StringComparison.Ordinal) ? "dvd" : "cd"
        );
        if (tags is null || tags.Length == 0)
            return null;

        var expectsCd = string.Equals(command, "createcd", StringComparison.Ordinal);
        foreach (var tag in tags)
        {
            var isCdTag = tag is CodecTags.Cdlz or CodecTags.Cdzl or CodecTags.Cdzs or CodecTags.Cdfl;
            if (isCdTag != expectsCd)
                return null;
        }

        return tags;
    }

    /// <summary>
    ///     Detects the platform of a source image for the auto preset, off the UI thread. Returns
    ///     <c>null</c> when detection is unavailable or fails, so the caller can fall back to the
    ///     default codec list.
    /// </summary>
    /// <param name="inputPath">Path of the disc image or descriptor.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>The detected platform, or <c>null</c>.</returns>
    internal static async Task<DiscPlatform?> DetectPlatformAsync(
        string inputPath,
        CancellationToken token
    )
    {
        try
        {
            var info = await Task.Run(() => PlatformDetector.Detect(inputPath), token)
                .ConfigureAwait(false);
            return info.Platform == DiscPlatform.Unknown ? null : info.Platform;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Returns the value for chdman's <c>-c</c> option, or <c>null</c> when no option should be
    ///     added (the command's defaults apply).
    /// </summary>
    /// <param name="preset">The selected preset.</param>
    /// <param name="command">The creation command.</param>
    /// <param name="platform">Detected platform for the auto preset.</param>
    /// <returns>The codec argument text, or <c>null</c>.</returns>
    internal static string? GetChdmanCodecArgument(
        ChdCompressionPreset preset,
        string command,
        DiscPlatform? platform = null
    )
    {
        if (string.Equals(command, "createld", StringComparison.Ordinal) || !IsSupported(preset, command))
            return null;

        var tags = GetCodecTags(preset, command, platform);
        if (tags is null || tags.Length == 0)
            return null;

        return string.Join(
            ",",
            tags.Select(static tag =>
                tag == CodecTags.None ? "none" : CodecTags.ToString(tag)
            )
        );
    }
}
