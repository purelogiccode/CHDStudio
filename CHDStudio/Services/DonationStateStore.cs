using System.Text.Json;
using CHDStudio.Models;
using Serilog;

namespace CHDStudio.Services;

/// <summary>
///     Reads and writes the local donation recognition state. The file lives in the application's
///     AppData folder so it survives upgrades and works when the install folder is read-only.
/// </summary>
internal static class DonationStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    /// <summary>Gets the default path of the donation state file.</summary>
    /// <returns>The full path under the local application data folder.</returns>
    internal static string GetDefaultPath()
    {
        return Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppConfig.ApplicationName,
            "donation.json"
        );
    }

    /// <summary>Loads the saved donation state, or a new empty state when none can be read.</summary>
    /// <param name="path">Optional state file path override (used by tests).</param>
    /// <returns>The loaded state. Unreadable files are treated as missing.</returns>
    internal static DonationState Load(string? path = null)
    {
        var statePath = path ?? GetDefaultPath();
        try
        {
            if (!File.Exists(statePath))
            {
                return new DonationState();
            }

            var json = File.ReadAllText(statePath);
            return JsonSerializer.Deserialize<DonationState>(json, JsonOptions)
                   ?? new DonationState();
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warning(ex, "Could not read the saved donation state.");
            return new DonationState();
        }
    }

    /// <summary>Writes the donation state, ignoring failures so recognition never blocks the UI.</summary>
    /// <param name="state">State to persist.</param>
    /// <param name="path">Optional state file path override (used by tests).</param>
    internal static void Save(DonationState state, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        var statePath = path ?? GetDefaultPath();
        try
        {
            var directory = Path.GetDirectoryName(statePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(statePath, JsonSerializer.Serialize(state, JsonOptions));
        }
        catch (Exception ex)
            when (ex
                  is IOException
                  or UnauthorizedAccessException
                  or JsonException
                  or NotSupportedException
            )
        {
            Log.Warning(ex, "Could not save the donation state.");
        }
    }
}
