using System.Buffers.Binary;
using System.Text;
using CHDStudio.Utilities;

namespace CHDStudio.Tests;

/// <summary>
///     Tests for <see cref="WavHeaderSanitizer" />, which repairs WAVE files whose header declares
///     more audio than the file contains (the Policenauts Saturn patch ships such files, and both
///     encoders fail on them).
/// </summary>
public class WavHeaderSanitizerTests : IDisposable
{
    private const int FrameBytes = 2352;
    private readonly string _tempDir;

    public WavHeaderSanitizerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"WavHeaderSanitizerTests_{Guid.NewGuid():N}");
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

    [Fact]
    public void TryGetDataChunkReportsOffsetAndDeclaredSize()
    {
        var path = WriteWav("intact.wav", 3);

        Assert.True(
            WavHeaderSanitizer.TryGetDataChunk(
                path,
                out var dataOffset,
                out var declaredLength,
                out var fileLength
            )
        );
        Assert.Equal(44, dataOffset);
        Assert.Equal(3u * FrameBytes, declaredLength);
        Assert.Equal(44 + 3 * FrameBytes, fileLength);
    }

    [Fact]
    public void TryGetDataChunkSkipsUnknownChunksBeforeData()
    {
        // A LIST chunk between fmt and data, as written by tools that embed metadata.
        var listChunk = BuildListChunk();
        var path = WriteWav("with-list.wav", 2, 0, listChunk);

        Assert.True(
            WavHeaderSanitizer.TryGetDataChunk(path, out var dataOffset, out var declaredLength, out _)
        );
        Assert.Equal(12 + 24 + listChunk.Length + 8, dataOffset);
        Assert.Equal(2u * FrameBytes, declaredLength);
    }

    [Fact]
    public void TryGetDataChunkReturnsFalseForNonWaveData()
    {
        var path = Path.Combine(_tempDir, "not-a-wav.bin");
        File.WriteAllText(path, "this is not a wave file at all", Encoding.ASCII);

        Assert.False(WavHeaderSanitizer.TryGetDataChunk(path, out _, out _, out _));
    }

    [Fact]
    public void IsOverDeclaredIsTrueWhenHeaderClaimsMoreAudioThanFileHolds()
    {
        var path = WriteWav("truncated.wav", 3, declaredExtraFrames: 1);

        Assert.True(WavHeaderSanitizer.IsOverDeclared(path));
    }

    [Fact]
    public void IsOverDeclaredIsFalseForIntactFile()
    {
        var path = WriteWav("intact.wav", 3);

        Assert.False(WavHeaderSanitizer.IsOverDeclared(path));
    }

    [Fact]
    public void ClampDeclaredSizesTrimsDataAndRiffSizesToActualBytes()
    {
        var path = WriteWav("truncated.wav", 3, declaredExtraFrames: 1);

        var overDeclaredBytes = WavHeaderSanitizer.ClampDeclaredSizes(path);

        Assert.Equal(FrameBytes, overDeclaredBytes);
        Assert.False(WavHeaderSanitizer.IsOverDeclared(path));

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(3 * FrameBytes, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(40, 4)));
        Assert.Equal(bytes.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4, 4)));
    }

    [Fact]
    public void ClampDeclaredSizesLeavesIntactFileByteIdentical()
    {
        var path = WriteWav("intact.wav", 3);
        var before = File.ReadAllBytes(path);

        var overDeclaredBytes = WavHeaderSanitizer.ClampDeclaredSizes(path);

        Assert.Equal(0, overDeclaredBytes);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    /// <summary>Builds an even-sized LIST chunk so the data chunk after it stays four-byte aligned.</summary>
    private static byte[] BuildListChunk()
    {
        var payload = "INFO"u8.ToArray();
        var chunk = new byte[8 + payload.Length];
        "LIST"u8.ToArray().CopyTo(chunk, 0);
        BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(4, 4), payload.Length);
        payload.CopyTo(chunk, 8);
        return chunk;
    }

    /// <summary>
    ///     Writes a 44100 Hz stereo 16-bit PCM WAVE. When <paramref name="declaredExtraFrames" /> is
    ///     positive, both the data and RIFF size fields over-declare by that many CD frames while the
    ///     payload keeps <paramref name="frames" /> frames, producing the truncated-header shape.
    /// </summary>
    /// <param name="name">File name inside the test directory.</param>
    /// <param name="frames">Number of 2352-byte audio frames actually written.</param>
    /// <param name="declaredExtraFrames">Extra frames the header claims but the file does not hold.</param>
    /// <param name="chunkBeforeData">Optional raw chunk (header plus payload) inserted before "data".</param>
    /// <returns>The full path of the written file.</returns>
    private string WriteWav(
        string name,
        int frames,
        int declaredExtraFrames = 0,
        byte[]? chunkBeforeData = null
    )
    {
        var path = Path.Combine(_tempDir, name);
        var dataBytes = frames * FrameBytes;
        var declaredBytes = dataBytes + declaredExtraFrames * FrameBytes;
        var extraChunk = chunkBeforeData ?? [];

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + extraChunk.Length + declaredBytes);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((ushort)1);
        writer.Write((ushort)2);
        writer.Write(44100);
        writer.Write(176400);
        writer.Write((ushort)4);
        writer.Write((ushort)16);
        writer.Write(extraChunk);
        writer.Write("data"u8.ToArray());
        writer.Write(declaredBytes);
        for (var i = 0; i < dataBytes; i++) writer.Write((byte)(i % 251));
        return path;
    }
}
