using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using CHDStudio.Models;

namespace CHDStudio.Utilities;

/// <summary>
///     Logs archive extraction progress at ten-percent steps, so a long ZIP/RAR/7z extraction
///     visibly keeps running instead of looking frozen until the whole archive is written.
/// </summary>
[SuppressMessage("ReSharper", "UnusedType.Global")]
internal sealed class ArchiveProgressLogger : IProgress<ArchiveProgress>
{
    private const int ProgressStepPercent = 10;

    private readonly string _activity;
    private readonly Action<string> _log;
    private readonly Lock _lock = new();
    private int _lastPercent;
    private int _nextPercent = ProgressStepPercent;

    /// <summary>Initializes a progress logger.</summary>
    /// <param name="log">Callback that receives the progress messages.</param>
    /// <param name="activity">Activity name used in the messages (e.g. <c>"Extracting"</c>).</param>
    internal ArchiveProgressLogger(Action<string> log, string activity)
    {
        _log = log;
        _activity = activity;
    }

    /// <summary>Reports one archive progress sample.</summary>
    /// <param name="progress">The current extraction progress.</param>
    public void Report(ArchiveProgress progress)
    {
        string message;
        lock (_lock)
        {
            var percent = (int)progress.Percent;
            if (percent < _lastPercent) _nextPercent = ProgressStepPercent;
            _lastPercent = percent;

            if (percent < _nextPercent) return;

            _nextPercent = percent - percent % ProgressStepPercent + ProgressStepPercent;
            message = progress.TotalBytes > 0
                ? $" {_activity} archive, {percent.ToString(CultureInfo.InvariantCulture)}% complete... ({progress.ProcessedBytes.ToString("N0", CultureInfo.InvariantCulture)} / {progress.TotalBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes)"
                : $" {_activity} archive, {percent.ToString(CultureInfo.InvariantCulture)}% complete... ({progress.CompletedEntries.ToString("N0", CultureInfo.InvariantCulture)} / {progress.TotalEntries.ToString("N0", CultureInfo.InvariantCulture)} entries)";
        }

        _log(message);
    }
}
