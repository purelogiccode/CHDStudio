using CHDStudio.Services;
using CHDSharp;
using CHDSharp.Encoder.Models;
using CHDSharp.Models;

namespace CHDStudio.Tests;

/// <summary>
///     Tests for the in-process CHDSharp encoder used on every platform (and as the Windows
///     fallback behind chdman).
/// </summary>
public class ChdSharpEncoderServiceTests : IDisposable
{
    private readonly string _tempDir;

    public ChdSharpEncoderServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ChdSharpEncoderServiceTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
        }
        catch
        {
            /* ignore */
        }

        GC.SuppressFinalize(this);
    }

    private string PathFor(string name)
    {
        return Path.Combine(_tempDir, name);
    }

    private static ChdHeaderInfo ReadHeader(string chdPath)
    {
        var error = Chd.ReadHeader(chdPath, out var header);
        Assert.Equal(ChdError.Chderrnone, error);
        return header!;
    }

    private static void AssertVerifies(string chdPath)
    {
        using var stream = File.OpenRead(chdPath);
        var result = Chd.CheckFile(stream, Path.GetFileName(chdPath), true);
        Assert.True(result.IsSuccess, $"CHD verification failed: {result.Error.GetMessage()}");
    }

    [Fact]
    public void CreateCd_EncodesVerifiableCdChd()
    {
        File.WriteAllBytes(PathFor("game.bin"), new byte[2352 * 5]);
        var cuePath = PathFor("game.cue");
        File.WriteAllText(
            cuePath,
            "FILE \"game.bin\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n"
        );
        var chdPath = PathFor("game.chd");

        ChdSharpEncoderService.Encode(
            "createcd",
            cuePath,
            chdPath,
            false,
            2,
            CancellationToken.None
        );

        AssertVerifies(chdPath);
        Assert.Equal(2448u, ReadHeader(chdPath).UnitBytes);
    }

    [Fact]
    public void CreateCd_ZeroLeadOutMultisessionCue_EncodesInsteadOfGoingNegative()
    {
        // A single-bin multisession cue whose REM LEAD-OUT sits before the session's last track
        // used to trim that track to a negative length (the reported -11400/-53048/-122045
        // failures). chdman skips the trim with its unsigned comparison, and CHDSharp 1.5.1 does
        // the same, so the encode must succeed.
        File.WriteAllBytes(PathFor("disc.bin"), new byte[2352 * 100]);
        var cuePath = PathFor("disc.cue");
        File.WriteAllText(
            cuePath,
            "REM SESSION 01\n"
                + "FILE \"disc.bin\" BINARY\n"
                + "  TRACK 01 AUDIO\n"
                + "    INDEX 01 00:00:00\n"
                + "  TRACK 02 AUDIO\n"
                + "    INDEX 01 00:00:10\n"
                + "REM LEAD-OUT 00:00:00\n"
                + "REM SESSION 02\n"
                + "  TRACK 03 AUDIO\n"
                + "    INDEX 01 00:00:20\n"
        );
        var chdPath = PathFor("disc.chd");

        ChdSharpEncoderService.Encode(
            "createcd",
            cuePath,
            chdPath,
            false,
            2,
            CancellationToken.None
        );

        AssertVerifies(chdPath);
    }

    [Fact]
    public void CreateDvd_EncodesVerifiableDvdChd()
    {
        File.WriteAllBytes(PathFor("disc.iso"), new byte[2048 * 20]);
        var chdPath = PathFor("disc.chd");

        ChdSharpEncoderService.Encode(
            "createdvd",
            PathFor("disc.iso"),
            chdPath,
            false,
            2,
            CancellationToken.None
        );

        AssertVerifies(chdPath);
        Assert.Equal(2048u, ReadHeader(chdPath).UnitBytes);
    }

    [Fact]
    public void CreateHd_EncodesVerifiableHardDiskChd()
    {
        File.WriteAllBytes(PathFor("disk.img"), new byte[512 * 2048]);
        var chdPath = PathFor("disk.chd");

        ChdSharpEncoderService.Encode(
            "createhd",
            PathFor("disk.img"),
            chdPath,
            false,
            2,
            CancellationToken.None
        );

        AssertVerifies(chdPath);
        Assert.Equal(512u, ReadHeader(chdPath).UnitBytes);
    }

    [Fact]
    public void CreateRaw_EncodesVerifiableRawChd()
    {
        File.WriteAllBytes(PathFor("track.raw"), new byte[2352 * 10]);
        var chdPath = PathFor("track.chd");

        ChdSharpEncoderService.Encode(
            "createraw",
            PathFor("track.raw"),
            chdPath,
            true,
            2,
            CancellationToken.None
        );

        AssertVerifies(chdPath);
        Assert.Equal(2352u, ReadHeader(chdPath).UnitBytes);
    }

    [Fact]
    public void CreateDvd_ReportsHunkProgress()
    {
        File.WriteAllBytes(PathFor("disc.iso"), new byte[2048 * 20]);
        var chdPath = PathFor("disc.chd");
        var reported = new List<HunkProgress>();

        ChdSharpEncoderService.Encode(
            "createdvd",
            PathFor("disc.iso"),
            chdPath,
            false,
            2,
            CancellationToken.None,
            reported.Add
        );

        Assert.Equal(10, reported.Count);
        Assert.All(reported, p => Assert.Equal(10u, p.HunkCount));
        Assert.Equal(0u, reported[0].HunkIndex);
        Assert.Equal(9u, reported[^1].HunkIndex);
    }

    [Fact]
    public void CreateLd_EncodesVerifiableLaserdiscChd()
    {
        var aviPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "laserdisc-small.avi");
        Assert.True(File.Exists(aviPath), $"AVI fixture missing: {aviPath}");
        var chdPath = PathFor("laserdisc.chd");
        var reported = new List<HunkProgress>();

        ChdSharpEncoderService.Encode(
            "createld",
            aviPath,
            chdPath,
            false,
            2,
            CancellationToken.None,
            reported.Add
        );

        AssertVerifies(chdPath);
        Assert.Equal(4u, ReadHeader(chdPath).TotalHunks);
        Assert.Equal(4, reported.Count);
    }

    [Fact]
    public void CreateLd_NonAviInput_Throws()
    {
        var input = PathFor("not-an-avi.bin");
        File.WriteAllBytes(input, new byte[4096]);

        Assert.Throws<InvalidDataException>(() =>
            ChdSharpEncoderService.Encode(
                "createld",
                input,
                PathFor("not-an-avi.chd"),
                false,
                1,
                CancellationToken.None
            )
        );
    }

    [Fact]
    public async Task DetectExtractCommand_CdChdIsExtractCd()
    {
        File.WriteAllBytes(PathFor("game.bin"), new byte[2352 * 5]);
        var cuePath = PathFor("game.cue");
        File.WriteAllText(
            cuePath,
            "FILE \"game.bin\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n"
        );
        var chdPath = PathFor("game.chd");
        ChdSharpEncoderService.Encode(
            "createcd",
            cuePath,
            chdPath,
            false,
            2,
            CancellationToken.None
        );

        var command = await MainWindow.DetectChdExtractCommandAsync(
            chdPath,
            CancellationToken.None
        );

        Assert.Equal("extractcd", command);
    }

    [Fact]
    public async Task DetectExtractCommand_DvdChdIsExtractDvd()
    {
        File.WriteAllBytes(PathFor("disc.iso"), new byte[2048 * 20]);
        var chdPath = PathFor("disc.chd");
        ChdSharpEncoderService.Encode(
            "createdvd",
            PathFor("disc.iso"),
            chdPath,
            false,
            2,
            CancellationToken.None
        );

        var command = await MainWindow.DetectChdExtractCommandAsync(
            chdPath,
            CancellationToken.None
        );

        Assert.Equal("extractdvd", command);
    }

    [Fact]
    public async Task DetectExtractCommand_LaserdiscChdIsExtractLd()
    {
        var aviPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "laserdisc-small.avi");
        Assert.True(File.Exists(aviPath), $"AVI fixture missing: {aviPath}");
        var chdPath = PathFor("laserdisc.chd");
        ChdSharpEncoderService.Encode(
            "createld",
            aviPath,
            chdPath,
            false,
            2,
            CancellationToken.None
        );

        var command = await MainWindow.DetectChdExtractCommandAsync(
            chdPath,
            CancellationToken.None
        );

        Assert.Equal("extractld", command);
    }

    [Theory]
    [InlineData("createdvd", 2048, false)]
    [InlineData("createhd", 512, false)]
    [InlineData("createraw", 2352, true)]
    public void PartialTrailingUnit_IsRejected(string command, int unitBytes, bool rawUnits)
    {
        // chdman refuses a size that is not a whole number of units; the built-in encoder must
        // too, otherwise the tail is silently dropped and the source could be deleted as success.
        var input = PathFor("partial.img");
        File.WriteAllBytes(input, new byte[unitBytes * 3 + 10]);

        Assert.Throws<InvalidDataException>(() =>
            ChdSharpEncoderService.Encode(
                command,
                input,
                PathFor("partial.chd"),
                rawUnits,
                1,
                CancellationToken.None
            )
        );
        Assert.False(File.Exists(PathFor("partial.chd")));
    }

    [Fact]
    public void UnsupportedCommand_Throws()
    {
        var input = PathFor("in.bin");
        File.WriteAllBytes(input, new byte[16]);

        Assert.Throws<ArgumentException>(() =>
            ChdSharpEncoderService.Encode(
                "createx",
                input,
                PathFor("out.chd"),
                false,
                1,
                CancellationToken.None
            )
        );
    }

    [Fact]
    public void ExtractCd_CookedMatchesOriginalBin_RawAddsSubcode()
    {
        // Regression test for Armored Core (USA) (Rerelease): the app extracted with
        // cooked:false (full 2448-byte raw frames), producing a 546,178,176-byte BIN
        // (223112 x 2448, SHA1 072ea00f...) instead of chdman's cooked 524,759,424-byte
        // BIN (223112 x 2352, SHA1 9b4c1b51...). The extraction must use cooked:true.
        const int frames = 8;
        const int dataSize = 2352;
        const int rawFrameSize = 2448;
        var original = new byte[frames * dataSize];
        for (var i = 0; i < original.Length; i++)
            original[i] = (byte)(i % 251);
        File.WriteAllBytes(PathFor("mode2.bin"), original);
        var cuePath = PathFor("mode2.cue");
        File.WriteAllText(
            cuePath,
            "FILE \"mode2.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n"
        );
        var chdPath = PathFor("mode2.chd");
        ChdSharpEncoderService.Encode(
            "createcd",
            cuePath,
            chdPath,
            false,
            2,
            CancellationToken.None
        );
        AssertVerifies(chdPath);

        var err = ChdFile.Open(chdPath, out var chd);
        Assert.Equal(ChdError.Chderrnone, err);
        using (chd)
        {
            var cookedDir = PathFor("cooked");
            var cooked = chd!.ExtractToDirectoryWithReporting(
                cookedDir,
                "game",
                null,
                CancellationToken.None,
                cooked: true
            );
            Assert.Equal(ChdError.Chderrnone, cooked.Error);
            var cookedBin = Path.Combine(cookedDir, "game.bin");
            Assert.True(File.Exists(cookedBin));
            Assert.Equal(original.Length, new FileInfo(cookedBin).Length);
            Assert.Equal(original, File.ReadAllBytes(cookedBin));

            var rawDir = PathFor("raw");
            var raw = chd.ExtractToDirectoryWithReporting(
                rawDir,
                "game",
                null,
                CancellationToken.None,
                cooked: false
            );
            Assert.Equal(ChdError.Chderrnone, raw.Error);
            var rawBin = Path.Combine(rawDir, "game.bin");
            Assert.True(File.Exists(rawBin));
            Assert.Equal(frames * rawFrameSize, new FileInfo(rawBin).Length);
        }
    }
}