using CHDSharp.Encoder;
using CHDStudio.Models;
using CHDStudio.Utilities;

namespace CHDStudio.Tests;

/// <summary>Tests for mapping compression presets to codec tags and chdman arguments.</summary>
public class ChdCompressionPresetsTests
{
    [Fact]
    public void Default_ReturnsNullForEncoderDefaults()
    {
        Assert.Null(
            ChdCompressionPresets.GetCodecTags(ChdCompressionPreset.Default, "createdvd")
        );
        Assert.Null(
            ChdCompressionPresets.GetChdmanCodecArgument(
                ChdCompressionPreset.Default,
                "createdvd"
            )
        );
    }

    [Fact]
    public void Compatible_UsesZlibForRawAndCdzlForCd()
    {
        Assert.Equal(
            [CodecTags.Zlib],
            ChdCompressionPresets.GetCodecTags(ChdCompressionPreset.Compatible, "createdvd")!
        );
        Assert.Equal(
            [CodecTags.Cdzl],
            ChdCompressionPresets.GetCodecTags(ChdCompressionPreset.Compatible, "createcd")!
        );
    }

    [Fact]
    public void Smallest_IncludesZstdAndCdzs()
    {
        Assert.Contains(
            CodecTags.Zstd,
            ChdCompressionPresets.GetCodecTags(ChdCompressionPreset.Smallest, "createdvd")!
        );
        Assert.Contains(
            CodecTags.Cdzs,
            ChdCompressionPresets.GetCodecTags(ChdCompressionPreset.Smallest, "createcd")!
        );
    }

    [Fact]
    public void Uncompressed_IsOnlySupportedForRawMedia()
    {
        Assert.True(
            ChdCompressionPresets.IsSupported(ChdCompressionPreset.Uncompressed, "createdvd")
        );
        Assert.False(
            ChdCompressionPresets.IsSupported(ChdCompressionPreset.Uncompressed, "createcd")
        );
        Assert.Equal(
            [CodecTags.None],
            ChdCompressionPresets.GetCodecTags(ChdCompressionPreset.Uncompressed, "createdvd")!
        );
    }
}
