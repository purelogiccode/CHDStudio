using CHDSharp;
using CHDSharp.Encoder;
using CHDSharp.Encoder.Models;
using CHDSharp.Models;
using CHDStudio.Models;
using CHDStudio.Utilities;

namespace CHDStudio.Services;

/// <summary>
///     Recompresses an existing CHD with a selected codec preset. A differential child source is
///     resolved against its parent and written as a standalone CHD, which also makes it usable by
///     tools that do not understand parent/child chains.
/// </summary>
internal static class ChdRecompressService
{
    /// <summary>
    ///     Recompresses <paramref name="sourcePath" /> into <paramref name="outputPath" />.
    /// </summary>
    /// <param name="sourcePath">Source CHD path (standalone or child).</param>
    /// <param name="outputPath">Destination CHD path.</param>
    /// <param name="preset">Codec preset for the output.</param>
    /// <param name="parentPath">Optional parent of a child source.</param>
    /// <param name="upgradeMetadata">True to upgrade legacy CD/GD metadata tags.</param>
    /// <param name="taskCount">Parallel compression workers.</param>
    /// <param name="token">Cancellation token.</param>
    /// <param name="onHunkCompleted">Optional per-hunk progress callback.</param>
    /// <exception cref="InvalidDataException">The source cannot be opened or is not a CHD.</exception>
    internal static void Recompress(
        string sourcePath,
        string outputPath,
        ChdCompressionPreset preset,
        string? parentPath,
        bool upgradeMetadata,
        int taskCount,
        CancellationToken token,
        Action<HunkProgress>? onHunkCompleted = null
    )
    {
        var openError = ChdFile.Open(sourcePath, parentPath, out var source, token);
        if (openError != ChdError.Chderrnone || source is null)
        {
            throw new InvalidDataException(
                $"Cannot open CHD '{sourcePath}' ({openError.GetMessage()})."
            );
        }

        DiscPlatform? platform = null;
        string command;
        using (source)
        {
            command = DetectCommand(source);
            if (
                preset == ChdCompressionPreset.Auto
                && command is "createcd" or "createdvd"
            )
            {
                platform = DiscDetector.Detect(source).Platform;
            }
        }

        if (preset == ChdCompressionPreset.Uncompressed && command is "createld")
        {
            // Laserdisc output requires AVHuff; there is no uncompressed A/V codec. CD images
            // support -c none in both chdman and the built-in encoder, so they keep the preset.
            preset = ChdCompressionPreset.Compatible;
        }

        var codecTags = ChdCompressionPresets.GetCodecTags(preset, command, platform);
        var options = new ChdEncodeOptions
        {
            TaskCount = Math.Clamp(taskCount, 1, 64),
            HunkCompleted = onHunkCompleted,
            SourceParentPath = parentPath,
            NoMetadataUpgrade = !upgradeMetadata
        };

        ChdEncoder.Copy(sourcePath, outputPath, codecTags, options, token);
    }

    /// <summary>Selects the creation command that matches the source media type.</summary>
    /// <param name="chd">The opened source CHD.</param>
    /// <returns>The command name used for codec-preset selection.</returns>
    private static string DetectCommand(ChdFile chd)
    {
        if (chd.IsCd || chd.IsGdRom)
            return "createcd";
        if (chd.IsDvd)
            return "createdvd";
        if (chd.IsHdd)
            return "createhd";

        foreach (var metadata in chd.Metadata)
        {
            if (string.Equals(metadata.Tag, "AVAV", StringComparison.Ordinal))
                return "createld";
        }

        return "createraw";
    }
}
