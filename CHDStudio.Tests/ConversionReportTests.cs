using CHDStudio.Utilities;

namespace CHDStudio.Tests;

/// <summary>
///     Tests for the conversion batch size and failure reports.
/// </summary>
public class ConversionReportTests : IDisposable
{
    private readonly string _tempDir;

    public ConversionReportTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ConversionReportTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or ArgumentException
                or NotSupportedException
            )
        {
            /* ignore */
        }

        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData(1000, 500, "saved 500 B (50%)")]
    [InlineData(1000, 1500, "500 B larger (50%)")]
    [InlineData(1000, 1000, "no change")]
    [InlineData(0, 1000, "")]
    public void FormatSavings_DescribesTheDifference(
        long sourceBytes,
        long outputBytes,
        string expected
    )
    {
        Assert.Equal(expected, ConversionReport.FormatSavings(sourceBytes, outputBytes));
    }

    [Fact]
    public async Task GetSourceBytesAsync_CueCountsItsReferencedBin()
    {
        var binPath = Path.Combine(_tempDir, "game.bin");
        await File.WriteAllBytesAsync(binPath, new byte[2048]);
        var cuePath = Path.Combine(_tempDir, "game.cue");
        await File.WriteAllTextAsync(
            cuePath,
            "FILE \"game.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n"
        );

        var bytes = await ConversionReport.GetSourceBytesAsync(
            [cuePath],
            CancellationToken.None
        );

        Assert.Equal(new FileInfo(cuePath).Length + 2048, bytes);
    }

    [Fact]
    public async Task GetSourceBytesByInputAsync_ReturnsEachInputsOwnSize()
    {
        var firstBin = Path.Combine(_tempDir, "first.bin");
        await File.WriteAllBytesAsync(firstBin, new byte[1024]);
        var firstCue = Path.Combine(_tempDir, "first.cue");
        await File.WriteAllTextAsync(
            firstCue,
            "FILE \"first.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n"
        );

        var secondIso = Path.Combine(_tempDir, "second.iso");
        await File.WriteAllBytesAsync(secondIso, new byte[2048]);

        var bytes = await ConversionReport.GetSourceBytesByInputAsync(
            [firstCue, secondIso],
            CancellationToken.None
        );

        Assert.Equal(new FileInfo(firstCue).Length + 1024, bytes[firstCue]);
        Assert.Equal(2048, bytes[secondIso]);
    }

    [Fact]
    public async Task GetSourceBytesAsync_UnreadableDescriptorStillCountsItself()
    {
        var cuePath = Path.Combine(_tempDir, "missing.cue");
        await File.WriteAllTextAsync(
            cuePath,
            "FILE \"does-not-exist.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n"
        );

        var bytes = await ConversionReport.GetSourceBytesAsync(
            [cuePath],
            CancellationToken.None
        );

        Assert.Equal(new FileInfo(cuePath).Length, bytes);
    }

    [Fact]
    public async Task WriteFailureReportAsync_WritesEveryFailedInput()
    {
        var first = Path.Combine(_tempDir, "first.cue");
        var second = Path.Combine(_tempDir, "second.iso");

        var reportPath = await ConversionReport.WriteFailureReportAsync(
            _tempDir,
            _tempDir,
            [second, first],
            CancellationToken.None
        );

        Assert.NotNull(reportPath);
        Assert.Equal(Path.Combine(_tempDir, ConversionReport.FailureReportFileName), reportPath);
        var lines = await File.ReadAllLinesAsync(reportPath);
        Assert.Contains("Failed: 2", lines, StringComparer.Ordinal);
        Assert.Contains(first, lines, StringComparer.Ordinal);
        Assert.Contains(second, lines, StringComparer.Ordinal);
    }

    [Fact]
    public void DeleteFailureReport_RemovesAnEarlierReport()
    {
        var reportPath = Path.Combine(_tempDir, ConversionReport.FailureReportFileName);
        File.WriteAllText(reportPath, "old");

        ConversionReport.DeleteFailureReport(_tempDir);

        Assert.False(File.Exists(reportPath));
    }

    [Fact]
    public async Task WriteFailureReportAsync_NoFailuresWritesNothing()
    {
        var reportPath = await ConversionReport.WriteFailureReportAsync(
            _tempDir,
            _tempDir,
            [],
            CancellationToken.None
        );

        Assert.Null(reportPath);
        Assert.False(
            File.Exists(Path.Combine(_tempDir, ConversionReport.FailureReportFileName))
        );
    }
}
