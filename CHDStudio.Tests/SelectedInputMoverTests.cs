using CHDStudio.Utilities;

namespace CHDStudio.Tests;

/// <summary>Tests for moving selected conversion inputs and their referenced files.</summary>
public sealed class SelectedInputMoverTests : IDisposable
{
    private readonly string _destination;
    private readonly string _source;
    private readonly string _tempDir;

    public SelectedInputMoverTests()
    {
        _tempDir = Path.Join(Path.GetTempPath(), $"SelectedInputMoverTests_{Guid.NewGuid():N}");
        _source = Path.Join(_tempDir, "source");
        _destination = Path.Join(_tempDir, "destination");
        Directory.CreateDirectory(_source);
        Directory.CreateDirectory(_destination);
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
    }

    [Fact]
    public async Task MoveAsync_MovesCueAndItsBinIntoASubfolder()
    {
        var cuePath = Path.Join(_source, "Frog Feast.cue");
        var binPath = Path.Join(_source, "Frog Feast.bin");
        await File.WriteAllBytesAsync(binPath, new byte[2352]);
        await File.WriteAllTextAsync(
            cuePath,
            "FILE \"Frog Feast.bin\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n"
        );

        var messages = new List<string>();
        var result = await SelectedInputMover.MoveAsync(
            _destination,
            [cuePath],
            messages.Add,
            messages.Add,
            CancellationToken.None
        );

        Assert.Single(result.MovedInputs);
        Assert.Empty(result.FailedInputs);
        Assert.True(File.Exists(Path.Join(_destination, "Frog Feast", "Frog Feast.cue")));
        Assert.True(File.Exists(Path.Join(_destination, "Frog Feast", "Frog Feast.bin")));
        Assert.False(File.Exists(cuePath));
        Assert.False(File.Exists(binPath));
        Assert.Contains(messages, static message => message.Contains("Moved 2 file(s)"));
    }

    [Fact]
    public async Task GetUploadSetAsync_ReturnsCueAndItsBin()
    {
        var cuePath = Path.Join(_source, "Game.cue");
        var binPath = Path.Join(_source, "Game.bin");
        await File.WriteAllBytesAsync(binPath, new byte[2352]);
        await File.WriteAllTextAsync(
            cuePath,
            "FILE \"Game.bin\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n"
        );

        var set = await SelectedInputMover.GetUploadSetAsync(cuePath, CancellationToken.None);

        Assert.Equal(2, set.Count);
        Assert.Contains(cuePath, set, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(binPath, set, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetUploadSetAsync_ReturnsSingleImage()
    {
        var isoPath = Path.Join(_source, "Game.iso");
        await File.WriteAllBytesAsync(isoPath, new byte[2048]);

        var set = await SelectedInputMover.GetUploadSetAsync(isoPath, CancellationToken.None);

        Assert.Equal([isoPath], set);
    }

    [Fact]
    public async Task MoveAsync_SingleImageMovesDirectlyIntoTheDestination()
    {
        var isoPath = Path.Join(_source, "Game.iso");
        await File.WriteAllBytesAsync(isoPath, new byte[2048]);

        var result = await SelectedInputMover.MoveAsync(
            _destination,
            [isoPath],
            static _ => { },
            static _ => { },
            CancellationToken.None
        );

        Assert.Equal([isoPath], result.MovedInputs);
        Assert.Empty(result.FailedInputs);
        Assert.True(File.Exists(Path.Join(_destination, "Game.iso")));
    }

    [Fact]
    public async Task MoveAsync_NameCollisionGetsASuffixInsteadOfOverwriting()
    {
        var isoPath = Path.Join(_source, "Game.iso");
        await File.WriteAllBytesAsync(isoPath, new byte[2048]);
        var existing = Path.Join(_destination, "Game.iso");
        await File.WriteAllTextAsync(existing, "existing");

        var result = await SelectedInputMover.MoveAsync(
            _destination,
            [isoPath],
            static _ => { },
            static _ => { },
            CancellationToken.None
        );

        Assert.Single(result.MovedInputs);
        Assert.Equal("existing", await File.ReadAllTextAsync(existing));
        Assert.True(File.Exists(Path.Join(_destination, "Game (2).iso")));
    }

    [Fact]
    public async Task MoveAsync_DescriptorSetCollisionGetsItsOwnSubfolder()
    {
        var cuePath = Path.Join(_source, "Game.cue");
        var binPath = Path.Join(_source, "Game.bin");
        await File.WriteAllBytesAsync(binPath, new byte[2352]);
        await File.WriteAllTextAsync(
            cuePath,
            "FILE \"Game.bin\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n"
        );

        var occupied = Path.Join(_destination, "Game");
        Directory.CreateDirectory(occupied);
        await File.WriteAllTextAsync(Path.Join(occupied, "other.txt"), "other");

        var result = await SelectedInputMover.MoveAsync(
            _destination,
            [cuePath],
            static _ => { },
            static _ => { },
            CancellationToken.None
        );

        Assert.Single(result.MovedInputs);
        Assert.True(File.Exists(Path.Join(_destination, "Game (2)", "Game.cue")));
        Assert.True(File.Exists(Path.Join(_destination, "Game (2)", "Game.bin")));
        Assert.Equal("other", await File.ReadAllTextAsync(Path.Join(occupied, "other.txt")));
    }
}
