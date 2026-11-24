using CHDSharp.Encoder;
using CHDSharp.Encoder.Models;

namespace CHDStudio.Services;

/// <summary>
///     In-process CHD encoder backed by the CHDSharp library. Replaces the bundled CHDSharp
///     command-line tool so every platform has an encoder without shipping a native executable;
///     on Windows it is the automatic fallback behind chdman.
/// </summary>
internal static class ChdSharpEncoderService
{
    /// <summary>CD hunk size: 8 frames (chdman's <c>createcd</c> default).</summary>
    private const uint CdHunkBytes = 19584;

    /// <summary>CD frame size with subcode (chdman's <c>createcd</c> unit size).</summary>
    private const uint CdUnitBytes = 2448;

    /// <summary>Default hunk size for raw images (chdman's raw/DVD/HDD default).</summary>
    private const uint RawHunkBytes = 4096;

    /// <summary>DVD sector size (chdman's <c>createdvd</c> unit size).</summary>
    private const uint DvdUnitBytes = 2048;

    /// <summary>Hard-disk sector size (chdman's <c>createhd</c> default).</summary>
    private const uint HardDiskUnitBytes = 512;

    /// <summary>Raw CD sector size used by <c>createraw -us 2352</c>.</summary>
    private const uint RawCdUnitBytes = 2352;

    /// <summary>Default CD codecs (chdman's <c>createcd</c> defaults).</summary>
    private static readonly uint[] CdCodecs = ChdCodecs.ParseCodecTags("cdlz,cdzl,cdfl");

    /// <summary>
    ///     Fallback CD codec used when the default set produced an output that failed its own
    ///     verification: zlib is the simplest and most widely compatible CD codec.
    /// </summary>
    private static readonly uint[] CdCodecsCompatible = ChdCodecs.ParseCodecTags("cdzl");

    /// <summary>Default raw/DVD/HDD codecs (chdman's defaults for those commands).</summary>
    private static readonly uint[] RawCodecs = ChdCodecs.ParseCodecTags("lzma,zlib,huff,flac");

    /// <summary>
    ///     Fallback raw/DVD/HDD codec used when the default set produced an output that failed
    ///     its own verification.
    /// </summary>
    private static readonly uint[] RawCodecsCompatible = ChdCodecs.ParseCodecTags("zlib");

    /// <summary>Laserdisc codec (chdman's <c>createld</c> default).</summary>
    private static readonly uint[] AvhuCodecs = ChdCodecs.ParseCodecTags("avhu");

    /// <summary>
    ///     Encodes <paramref name="inputPath" /> into <paramref name="outputPath" /> using the same
    ///     commands and defaults as chdman: <c>createcd</c>, <c>createdvd</c>, <c>createhd</c> and
    ///     <c>createraw</c>. The output is written to a temporary staging path by the caller and
    ///     moved into place on success.
    /// </summary>
    /// <param name="command">The chdman-compatible command selecting the encoding mode.</param>
    /// <param name="inputPath">Path of the prepared source (cue/gdi/toc/iso/img/raw).</param>
    /// <param name="outputPath">Path of the CHD file to create (created/overwritten).</param>
    /// <param name="rawUnits2352">
    ///     True when the source is a raw 2352-byte-sector image (<c>createraw -us 2352</c>).
    /// </param>
    /// <param name="taskCount">Parallel compression workers (chdman's <c>-np</c>).</param>
    /// <param name="token">Cancels the encode; throws <see cref="OperationCanceledException" />.</param>
    /// <param name="onHunkCompleted">
    ///     Optional per-hunk progress callback (invoked in hunk order while compressing), or
    ///     <c>null</c> for no reporting.
    /// </param>
    /// <param name="conservativeCodecs">
    ///     True to use the smallest, most widely compatible codec set (zlib only) instead of the
    ///     chdman defaults. Used for the automatic retry when the first output fails verification.
    /// </param>
    /// <param name="codecTags">
    ///     Optional codec tags for the output; when <c>null</c> the command's chdman-default list is
    ///     used. Ignored by the conservative retry and by <c>createld</c> (which always uses avhu).
    /// </param>
    /// <param name="parentPath">
    ///     Optional parent CHD for delta (child) output. The parent's hunk and unit sizes must
    ///     match the command's; the library throws when they do not.
    /// </param>
    /// <param name="hunkBytesOverride">
    ///     Optional hunk size for the output. Used for the PSP/PPSSPP-compatible
    ///     <c>createdvd -hs 2048</c> mode; the value must be a whole multiple of the command's unit
    ///     size. When null the command's chdman-compatible default is used.
    /// </param>
    /// <exception cref="ArgumentException">The command or hunk size is not supported.</exception>
    internal static void Encode(
        string command,
        string inputPath,
        string outputPath,
        bool rawUnits2352,
        int taskCount,
        CancellationToken token,
        Action<HunkProgress>? onHunkCompleted = null,
        bool conservativeCodecs = false,
        IReadOnlyList<uint>? codecTags = null,
        string? parentPath = null,
        uint? hunkBytesOverride = null
    )
    {
        var options = new ChdEncodeOptions
        {
            TaskCount = Math.Clamp(taskCount, 1, 64),
            HunkCompleted = onHunkCompleted,
            ParentPath = parentPath
        };

        var cdCodecs = conservativeCodecs ? CdCodecsCompatible : codecTags ?? CdCodecs;
        var rawCodecs = conservativeCodecs ? RawCodecsCompatible : codecTags ?? RawCodecs;

        if (hunkBytesOverride is { } requestedHunk)
            ValidateHunkSize(command, rawUnits2352, requestedHunk);

        // chdman refuses inputs whose size is not a whole number of units for the raw commands.
        // The built-in encoder has to reject them too: encoding a partial trailing unit would drop
        // the tail and still report success, after which the source could be deleted.
        if (command is "createdvd" or "createhd" or "createraw")
        {
            var unitBytes = command switch
            {
                "createdvd" => DvdUnitBytes,
                "createhd" => HardDiskUnitBytes,
                _ => rawUnits2352 ? RawCdUnitBytes : HardDiskUnitBytes,
            };

            var fileSize = (ulong)new FileInfo(inputPath).Length;
            if (fileSize % unitBytes != 0)
            {
                throw new InvalidDataException(
                    $"Data size {fileSize:N0} is not divisible by sector size {unitBytes}."
                );
            }
        }

        switch (command)
        {
            case "createcd":
                // EncodeCd parses CUE/GDI/TOC/ISO descriptors and writes standard 2448-byte CD
                // frames; a 2352-byte source has its 96-byte subcode portion zero-filled by the
                // reader, so the raw-units flag is not needed here.
                ChdEncoder.EncodeCd(
                    inputPath,
                    outputPath,
                    hunkBytesOverride ?? CdHunkBytes,
                    CdUnitBytes,
                    cdCodecs,
                    options,
                    token
                );
                break;

            case "createdvd":
                // createdvd always stamps the 'DVD ' metadata tag (that is what makes it a DVD).
                options.Metadata = [MetadataWriter.BuildDvdMetadata()];
                ChdEncoder.EncodeRaw(
                    inputPath,
                    outputPath,
                    hunkBytesOverride ?? RawHunkBytes,
                    DvdUnitBytes,
                    rawCodecs,
                    options,
                    token
                );
                break;

            case "createhd":
            {
                // chdman createhd guesses CHS geometry from the image size, writes 'GDDD' metadata
                // and makes the logical size the geometry product (which rounds sub-geometry
                // inputs up past the actual file length).
                var fileSize = (ulong)new FileInfo(inputPath).Length;
                var geometry = MetadataWriter.GuessChs(fileSize, HardDiskUnitBytes);
                options.Metadata =
                [
                    MetadataWriter.BuildHardDiskMetadata(
                        geometry.Cylinders,
                        geometry.Heads,
                        geometry.Sectors,
                        HardDiskUnitBytes
                    )
                ];
                options.LogicalLengthBytes = (long)(
                    (ulong)geometry.Cylinders * geometry.Heads * geometry.Sectors * HardDiskUnitBytes
                );

                ChdEncoder.EncodeRaw(
                    inputPath,
                    outputPath,
                    hunkBytesOverride ?? RawHunkBytes,
                    HardDiskUnitBytes,
                    rawCodecs,
                    options,
                    token
                );
                break;
            }

            case "createraw":
            {
                var unitBytes = rawUnits2352 ? RawCdUnitBytes : HardDiskUnitBytes;
                var hunkBytes =
                    hunkBytesOverride ?? Math.Max(RawHunkBytes / unitBytes * unitBytes, unitBytes);
                ChdEncoder.EncodeRaw(
                    inputPath,
                    outputPath,
                    hunkBytes,
                    unitBytes,
                    rawCodecs,
                    options,
                    token
                );
                break;
            }

            case "createld":
                // AVI laserdisc input; a hunk size of 0 selects one raw frame per hunk, matching
                // chdman's createld default.
                ChdEncoder.EncodeLaserDisc(
                    inputPath,
                    outputPath,
                    0,
                    AvhuCodecs,
                    options,
                    0,
                    null,
                    token
                );
                break;

            default:
                throw new ArgumentException(
                    $"Unsupported encoder command '{command}'.",
                    nameof(command)
                );
        }
    }

    /// <summary>
    ///     Rejects a requested hunk size that cannot be represented for the selected command.
    ///     chdman requires the hunk size to be a whole multiple of the unit size; the built-in
    ///     encoder enforces the same rule instead of writing a file it cannot describe correctly.
    /// </summary>
    /// <param name="command">The chdman-compatible creation command.</param>
    /// <param name="rawUnits2352">Whether a raw source uses 2352-byte units.</param>
    /// <param name="hunkBytes">The requested hunk size in bytes.</param>
    private static void ValidateHunkSize(string command, bool rawUnits2352, uint hunkBytes)
    {
        var unitBytes = command switch
        {
            "createcd" => CdUnitBytes,
            "createdvd" => DvdUnitBytes,
            "createhd" => HardDiskUnitBytes,
            "createraw" => rawUnits2352 ? RawCdUnitBytes : HardDiskUnitBytes,
            _ => 0u,
        };

        if (unitBytes == 0)
        {
            throw new ArgumentException(
                $"A custom hunk size is not supported for '{command}'.",
                nameof(command)
            );
        }

        if (hunkBytes is < 16 or > 1024 * 1024 || hunkBytes % unitBytes != 0)
        {
            throw new ArgumentException(
                $"Hunk size {hunkBytes:N0} must be a multiple of the {unitBytes:N0}-byte unit size and between 16 and 1,048,576 bytes.",
                nameof(hunkBytes)
            );
        }
    }
}

