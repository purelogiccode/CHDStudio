using CHDSharp.Models;
using CHDStudio.Utilities;
using VideoGameFileSystemParser.Models;

namespace CHDStudio.Tests;

/// <summary>Tests for mapping detected disc platforms to Explorer parsers.</summary>
public class ChdPlatformMappingTests
{
    [Theory]
    [InlineData(DiscPlatform.Ps1, ConsoleType.Ps1)]
    [InlineData(DiscPlatform.Ps2, ConsoleType.Ps2)]
    [InlineData(DiscPlatform.Psp, ConsoleType.Psp)]
    [InlineData(DiscPlatform.Saturn, ConsoleType.Saturn)]
    [InlineData(DiscPlatform.Dreamcast, ConsoleType.Dreamcast)]
    [InlineData(DiscPlatform.ThreeDo, ConsoleType.ThreeDo)]
    [InlineData(DiscPlatform.MegaCd, ConsoleType.SegaGenesisCd)]
    [InlineData(DiscPlatform.NeoGeoCd, ConsoleType.NeoGeoCd)]
    [InlineData(DiscPlatform.PcEngine, ConsoleType.PcEngineCd)]
    public void ToConsoleType_MapsKnownPlatforms(DiscPlatform platform, ConsoleType expected)
    {
        Assert.Equal(expected, ChdPlatformMapping.ToConsoleType(platform));
    }

    [Fact]
    public void ToConsoleType_UnknownReturnsNull()
    {
        Assert.Null(ChdPlatformMapping.ToConsoleType(DiscPlatform.Unknown));
    }
}
