using CHDSharp;
using CHDSharp.Encoder;
using CHDSharp.Encoder.Models;
using CHDSharp.Models;
using CHDStudio.Models;
using CHDStudio.Services;

namespace CHDStudio.Tests;

/// <summary>Tests for CHD recompression, codec selection and child consolidation.</summary>
public class ChdRecompressServiceTests : IDisposable
{
    private readonly string _tempDir;

    public ChdRecompressServiceTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            $"ChdRecompressServiceTests_{Guid.NewGuid():N}"
        );
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

    private static void AssertVerifies(string chdPath)
    {
        using var stream = File.OpenRead(chdPath);
        var result = Chd.CheckFile(stream, Path.GetFileName(chdPath), true);
        Assert.True(result.IsSuccess, $"CHD verification failed: {result.Error.GetMessage()}");
    }

    [Fact]
    public void Recompress_DvdWithCompatiblePreset_UsesZlibAndVerifies()
    {
        var isoPath = PathFor("disc.iso");
        File.WriteAllBytes(isoPath, new byte[2048 * 64]);
        var sourcePath = PathFor("source.chd");
        ChdSharpEncoderService.Encode(
            "createdvd",
            isoPath,
            sourcePath,
            false,
            2,
            CancellationToken.None
        );

        var outputPath = PathFor("output.chd");
        ChdRecompressService.Recompress(
            sourcePath,
            outputPath,
            ChdCompressionPreset.Compatible,
            null,
            true,
            2,
            CancellationToken.None
        );

        AssertVerifies(outputPath);
        Assert.Equal(
            ChdError.Chderrnone,
            Chd.ReadHeader(outputPath, out var header)
        );
        Assert.Contains(ChdCodec.Zlib, header!.Compression);
    }

    [Fact]
    public void Recompress_CdWithUncompressedPreset_FallsBackToCompatible()
    {
        File.WriteAllBytes(PathFor("cd.bin"), new byte[2352 * 5]);
        var cuePath = PathFor("cd.cue");
        File.WriteAllText(
            cuePath,
            "FILE \"cd.bin\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n"
        );
        var sourcePath = PathFor("cd-source.chd");
        ChdSharpEncoderService.Encode(
            "createcd",
            cuePath,
            sourcePath,
            false,
            2,
            CancellationToken.None
        );

        var outputPath = PathFor("cd-fallback.chd");
        ChdRecompressService.Recompress(
            sourcePath,
            outputPath,
            ChdCompressionPreset.Uncompressed,
            null,
            true,
            2,
            CancellationToken.None
        );

        AssertVerifies(outputPath);
    }

    [Fact]
    public void Recompress_ChildSource_ProducesStandaloneChd()
    {
        var parentSource = PathFor("parent.bin");
        var parentBytes = new byte[4096 * 512];
        for (var i = 0; i < parentBytes.Length; i++)
            parentBytes[i] = (byte)(i * 31);

        File.WriteAllBytes(parentSource, parentBytes);
        var parentChd = PathFor("parent.chd");
        ChdEncoder.EncodeRaw(parentSource, parentChd);

        var childBytes = (byte[])parentBytes.Clone();
        for (var i = 0; i < 4096; i++)
            childBytes[i] = (byte)(255 - childBytes[i]);

        var childSource = PathFor("child.bin");
        File.WriteAllBytes(childSource, childBytes);
        var childChd = PathFor("child.chd");
        ChdEncoder.EncodeRaw(
            childSource,
            childChd,
            options: new ChdEncodeOptions { ParentPath = parentChd }
        );

        Assert.Equal(ChdError.Chderrnone, Chd.ReadHeader(childChd, out var childHeader));
        Assert.True(childHeader!.HasParent);

        var outputPath = PathFor("standalone.chd");
        ChdRecompressService.Recompress(
            childChd,
            outputPath,
            ChdCompressionPreset.Default,
            parentChd,
            true,
            2,
            CancellationToken.None
        );

        AssertVerifies(outputPath);
        Assert.Equal(ChdError.Chderrnone, ChdFile.Open(outputPath, out var standalone));
        using (standalone)
        {
            Assert.False(standalone!.RequiresParent);
        }
    }
}
