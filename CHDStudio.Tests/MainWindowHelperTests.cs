using System.Text;
using CHDSharp;
using CHDStudio.Services;

namespace CHDStudio.Tests;

/// <summary>
///     Tests for MainWindow helper methods used by the conversion pipeline.
/// </summary>
public class MainWindowHelperTests : IDisposable
{
    private readonly string _tempDir;

    public MainWindowHelperTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"MainWindowHelperTests_{Guid.NewGuid():N}");
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

    [Theory]
    [InlineData(59, false)]
    [InlineData(61, true)]
    public void IsStaleTempDirectory_RespectsMinimumAge(int ageMinutes, bool expected)
    {
        var directory = Path.Combine(_tempDir, $"temp_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        var utcNow = DateTime.UtcNow;
        Directory.SetLastWriteTimeUtc(directory, utcNow.AddMinutes(-ageMinutes));

        Assert.Equal(
            expected,
            MainWindow.IsStaleTempDirectory(directory, utcNow, TimeSpan.FromHours(1))
        );
    }

    [Fact]
    public async Task AwaitFileScanAsync_CancelledScanIsIgnored()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await MainWindow.AwaitFileScanAsync(Task.Run(static () => { }, cts.Token));
    }

    [Fact]
    public async Task AwaitFileScanAsync_RealFailurePropagates()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () =>
                MainWindow.AwaitFileScanAsync(
                    Task.FromException(new InvalidOperationException("scan failed"))
                )
        );
    }

    [Fact]
    public async Task StripUtf8BomIfPresentAsync_RemovesBom()
    {
        var path = Path.Combine(_tempDir, "bom.cue");
        await File.WriteAllTextAsync(path, "FILE \"track1.bin\" BINARY", Encoding.UTF8);

        Assert.Equal(0xEF, (await File.ReadAllBytesAsync(path))[0]);

        await MainWindow.StripUtf8BomIfPresentAsync(path, CancellationToken.None);

        var bytes = await File.ReadAllBytesAsync(path);
        Assert.False(bytes is [0xEF, 0xBB, 0xBF, ..], "BOM must be removed");
        Assert.StartsWith(
            "FILE \"track1.bin\" BINARY",
            Encoding.UTF8.GetString(bytes),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task StripUtf8BomIfPresentAsync_LeavesBomFreeFileUntouched()
    {
        var path = Path.Combine(_tempDir, "plain.cue");
        const string content = "FILE \"track1.bin\" BINARY";
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false));

        await MainWindow.StripUtf8BomIfPresentAsync(path, CancellationToken.None);

        Assert.Equal(content, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task StripUtf8BomIfPresentAsync_MissingFileIsIgnored()
    {
        // Best-effort helper: must not throw for a missing file.
        await MainWindow.StripUtf8BomIfPresentAsync(
            Path.Combine(_tempDir, "nope.cue"),
            CancellationToken.None
        );
    }

    [Fact]
    public void SelectChdmanErrorLine_SkipsProgressLinesAndReturnsLastRealError()
    {
        const string errorText =
            "Compressing, 0.0% complete... (ratio=100.0%)\r\n"
            + "Output bytes: 1234\r\n"
            + "ERROR: couldn't find bin file [track1.bin]";

        var line = MainWindow.SelectChdmanErrorLine(errorText);

        Assert.Equal("ERROR: couldn't find bin file [track1.bin]", line);
    }

    [Fact]
    public void SelectChdmanErrorLine_ProgressOnly_ReturnsLastLine()
    {
        const string errorText =
            "Compressing, 10.0% complete... (ratio=95.0%)\nConverting, 20.0% complete...";

        var line = MainWindow.SelectChdmanErrorLine(errorText);

        Assert.Equal("Converting, 20.0% complete...", line);
    }

    [Fact]
    public void SelectChdmanErrorLine_SingleErrorLineIsReturned()
    {
        var line = MainWindow.SelectChdmanErrorLine(
            "Unit size must be specified if no output parent CHD is supplied"
        );

        Assert.Equal("Unit size must be specified if no output parent CHD is supplied", line);
    }

    [Fact]
    public void SelectChdmanErrorLine_EmptyInputReturnsEmpty()
    {
        Assert.Equal(string.Empty, MainWindow.SelectChdmanErrorLine(string.Empty));
        Assert.Equal(string.Empty, MainWindow.SelectChdmanErrorLine(" \r\n \n "));
    }

    [Fact]
    public void SelectChdmanErrorLine_SkipsFatalErrorSummaryAndReturnsRealCause()
    {
        // chdman prints the actual cause before its "Fatal error occurred: N" exit summary.
        const string errorText =
            "Compressing, 0.0% complete... (ratio=100.0%)\r\n"
            + "ERROR: Input file is not a valid CD image\r\n"
            + "Fatal error occurred: 1";

        var line = MainWindow.SelectChdmanErrorLine(errorText);

        Assert.Equal("ERROR: Input file is not a valid CD image", line);
    }

    [Fact]
    public void SelectChdmanErrorLine_FatalSummaryOnly_ReturnsDescriptiveMessage()
    {
        const string errorText =
            "Compressing, 0.0% complete... (ratio=100.0%)\nFatal error occurred: 1";

        var line = MainWindow.SelectChdmanErrorLine(errorText);

        Assert.Equal(
            "chdman encountered an error. The file may be corrupted, in an unsupported format, or a required codec may be missing.",
            line
        );
    }

    [Fact]
    public void SelectChdmanErrorLine_DoesNotSkipUnhandledExceptionLine()
    {
        // chdman C++ runtime crash lines are real causes and must be kept.
        const string errorText =
            "Unhandled exception: cannot create std::vector larger than max_size()";

        var line = MainWindow.SelectChdmanErrorLine(errorText);

        Assert.Equal("Unhandled exception: cannot create std::vector larger than max_size()", line);
    }

    [Theory]
    [InlineData("libc++abi: Pure virtual function called!", true)]
    [InlineData("terminate called after throwing an instance of 'std::bad_alloc'", true)]
    [InlineData("terminate called without an active exception", true)]
    [InlineData("LIBC++ABI: PURE VIRTUAL FUNCTION CALLED!", true)]
    [InlineData("ERROR: couldn't find bin file [track1.bin]", false)]
    [InlineData("Fatal error occurred: 1", false)]
    [InlineData("", false)]
    public void IsChdmanRuntimeCrashText_DetectsCppRuntimeAborts(string errorText, bool expected)
    {
        // A C++ runtime abort exits with a positive code on Windows, so the text is the only
        // signal that chdman crashed rather than reporting a conversion error.
        Assert.Equal(expected, MainWindow.IsChdmanRuntimeCrashText(errorText));
    }

    [Fact]
    public void GetChdExtractionErrorMessage_DecompressionFailure_AddsGuidance()
    {
        var message = MainWindow.GetChdExtractionErrorMessage(
            "Failed to read hunk 0: Chderrdecompressionerror"
        );

        Assert.Contains("A/V (laserdisc)", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Retrying with chdman", message, StringComparison.Ordinal);
        Assert.StartsWith(
            "Failed to read hunk 0: Chderrdecompressionerror",
            message,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void GetChdExtractionErrorMessage_OtherFailures_AreUnchanged()
    {
        const string message = "No files extracted.";

        Assert.Equal(message, MainWindow.GetChdExtractionErrorMessage(message));
    }

    [Theory]
    [InlineData("CHD error occurred (main): A device which does not exist was specified.")]
    [InlineData("The device is not ready")]
    [InlineData("The system cannot find the drive specified")]
    public void IsMissingDeviceError_DeviceFailures_ReturnsTrue(string errorOutput)
    {
        Assert.True(MainWindow.IsMissingDeviceError(errorOutput));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Input/output error")]
    [InlineData("Access denied")]
    [InlineData("couldn't find bin file")]
    public void IsMissingDeviceError_OtherFailures_ReturnsFalse(string? errorOutput)
    {
        Assert.False(MainWindow.IsMissingDeviceError(errorOutput));
    }

    [Fact]
    public void BuildChdmanExtractArgs_ExtractCd_PinsBinAndForces()
    {
        var args = MainWindow.BuildChdmanExtractArgs(
            "extractcd",
            @"D:\roms\game.chd",
            @"D:\out\game.cue"
        );

        Assert.Contains("extractcd -i \"D:\\roms\\game.chd\"", args, StringComparison.Ordinal);
        Assert.Contains("-o \"D:\\out\\game.cue\"", args, StringComparison.Ordinal);
        Assert.Contains("-ob \"D:\\out\\game.bin\"", args, StringComparison.Ordinal);
        Assert.EndsWith("-f", args, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("extractdvd", ".iso")]
    [InlineData("extracthd", ".img")]
    [InlineData("extractld", ".avi")]
    [InlineData("extractraw", ".raw")]
    public void BuildChdmanExtractArgs_OtherCommands_InputOutputForce(string command, string ext)
    {
        var output = $@"D:\out\game{ext}";
        var args = MainWindow.BuildChdmanExtractArgs(command, @"D:\roms\game.chd", output);

        Assert.Contains($"{command} -i \"D:\\roms\\game.chd\"", args, StringComparison.Ordinal);
        Assert.Contains($"-o \"{output}\"", args, StringComparison.Ordinal);
        Assert.DoesNotContain("-ob", args, StringComparison.Ordinal);
        Assert.EndsWith("-f", args, StringComparison.Ordinal);
    }

    [Fact]
    public void GetChdExtractionErrorMessage_WriteFailure_AddsDriveGuidance()
    {
        var message = MainWindow.GetChdExtractionErrorMessage(
            "Extraction failed; track 3: Chderrwriteerror [CHD v5 compression=Cdzstd]"
        );

        Assert.Contains("full, disconnected, read-only", message, StringComparison.Ordinal);
        Assert.StartsWith("Extraction failed", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Extraction failed; track 3: Chderrwriteerror", true)]
    [InlineData("extraction failed; track 1: chderrwriteerror", true)]
    [InlineData("Extraction failed; track 1: Chderrdecompressionerror", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsChdWriteFailure_ClassifiesWriteErrors(string? message, bool expected)
    {
        Assert.Equal(expected, MainWindow.IsChdWriteFailure(message));
    }

    [Fact]
    public void IsDamagedPbpBlock_ShortDeflateStream_ReturnsTrue()
    {
        // Exact shape of the reports: a deflate stream that ends before the full 16-sector block.
        const string error =
            "Failed to extract disc 1 of 1: DecompressionError (code 8); blocks=19267, isoSize=725048688, "
            + "discId=SCUS94358, psarOffset=0x10000; block 2380 of 19267 (disc 1, id SCUS94358): "
            + "fileOffset=0x3481D91, length=28556, read=28556, uncompressed=False, isoSize=725048688, "
            + "first bytes ECBC0B3894EDDA3F7CDDB333C6301BD2; the PSAR block did not inflate as raw "
            + "deflate (the deflate stream produced only 28,130 of 37,632 bytes.) or as a zlib stream "
            + "(Header checksum illegal).";

        Assert.True(MainWindow.IsDamagedPbpBlock(error));
    }

    [Theory]
    [InlineData("the PSAR block did not inflate as raw deflate (broken uncompressed block)", true)]
    [InlineData("Invalid ISO block length in PSAR index.", true)]
    [InlineData("Failed to open PBP file: CorruptFile (code 4)", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsDamagedPbpBlock_OtherMessages(string? message, bool expected)
    {
        Assert.Equal(expected, MainWindow.IsDamagedPbpBlock(message));
    }

    [Fact]
    public void ValidateStagedChd_DeltaOutput_VerifiesAgainstParent()
    {
        var parentBytes = new byte[4096 * 64];
        for (var i = 0; i < parentBytes.Length; i++)
            parentBytes[i] = (byte)(i * 17);
        var parentSource = Path.Combine(_tempDir, "delta-parent.bin");
        File.WriteAllBytes(parentSource, parentBytes);
        var parentChd = Path.Combine(_tempDir, "delta-parent.chd");
        ChdSharpEncoderService.Encode(
            "createraw",
            parentSource,
            parentChd,
            false,
            2,
            CancellationToken.None
        );

        var childBytes = (byte[])parentBytes.Clone();
        for (var i = 0; i < 2048; i++)
            childBytes[i] = (byte)(255 - childBytes[i]);
        var childSource = Path.Combine(_tempDir, "delta-child.bin");
        File.WriteAllBytes(childSource, childBytes);
        var childChd = Path.Combine(_tempDir, "delta-child.chd");
        ChdSharpEncoderService.Encode(
            "createraw",
            childSource,
            childChd,
            false,
            2,
            CancellationToken.None,
            parentPath: parentChd
        );

        var result = MainWindow.ValidateStagedChd(
            childChd,
            parentChd,
            CancellationToken.None
        );

        Assert.True(result.IsSuccess, result.Error.GetMessage());
    }
}