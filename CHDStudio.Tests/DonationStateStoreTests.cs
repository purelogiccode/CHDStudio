using CHDStudio.Models;
using CHDStudio.Services;

namespace CHDStudio.Tests;

public class DonationStateStoreTests : IDisposable
{
    private readonly string _directory = Path.Join(
        Path.GetTempPath(),
        $"CHDStudio_DonationState_{Guid.NewGuid():N}"
    );

    private string StatePath => Path.Join(_directory, "donation.json");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup only.
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void SaveAndLoadRoundTrip()
    {
        var original = new DonationState
        {
            Email = "user@example.com",
            HasDonated = true,
            LastCheckedUtc = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc)
        };

        DonationStateStore.Save(original, StatePath);
        var loaded = DonationStateStore.Load(StatePath);

        Assert.Equal(original.Email, loaded.Email);
        Assert.True(loaded.HasDonated);
        Assert.Equal(original.LastCheckedUtc, loaded.LastCheckedUtc);
    }

    [Fact]
    public void SaveCreatesTheParentDirectory()
    {
        DonationStateStore.Save(new DonationState { Email = "user@example.com" }, StatePath);

        Assert.True(File.Exists(StatePath));
    }

    [Fact]
    public void LoadReturnsAnEmptyStateWhenTheFileIsMissing()
    {
        var loaded = DonationStateStore.Load(StatePath);

        Assert.Equal(string.Empty, loaded.Email);
        Assert.False(loaded.HasDonated);
        Assert.Null(loaded.LastCheckedUtc);
    }

    [Fact]
    public void LoadReturnsAnEmptyStateWhenTheFileIsInvalid()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(StatePath, "{not json");

        var loaded = DonationStateStore.Load(StatePath);

        Assert.Equal(string.Empty, loaded.Email);
        Assert.False(loaded.HasDonated);
    }
}
