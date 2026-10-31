using CHDStudio.Services;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace CHDStudio.Tests;

/// <summary>
///     Verifies that CHDSharp's diagnostics reach the application log and can be attached to the
///     failure message, so a rejected CHD reports the failing hunk instead of a bare error code.
/// </summary>
public class ChdSharpLoggerFactoryTests
{
    /// <summary>A warning is captured so the caller can attach it to its own report.</summary>
    [Fact]
    public void WarningIsCapturedAndCleared()
    {
        ChdSharpLoggerFactory.TakeLastWarning();

        ILogger logger = new ChdSharpLoggerFactory().CreateLogger("CHDSharp.Chd");
        logger.Log(
            LogLevel.Warning,
            new EventId(0),
            "hunk 8123/11437 (cdlz) decompression failed: Chderrinvaliddata",
            null,
            static (state, _) => state
        );

        var captured = ChdSharpLoggerFactory.TakeLastWarning();
        Assert.NotNull(captured);
        Assert.Contains("hunk 8123/11437", captured);
        Assert.Contains("cdlz", captured);

        // The detail is single-use: a later, unrelated failure must not inherit it.
        Assert.Null(ChdSharpLoggerFactory.TakeLastWarning());
    }

    /// <summary>Lower levels are logged but never captured as a failure reason.</summary>
    [Fact]
    public void InformationIsNotCaptured()
    {
        ChdSharpLoggerFactory.TakeLastWarning();

        ILogger logger = new ChdSharpLoggerFactory().CreateLogger("CHDSharp.Chd");
        logger.Log(
            LogLevel.Information,
            new EventId(0),
            "Total Blocks 11437",
            null,
            static (state, _) => state
        );

        Assert.Null(ChdSharpLoggerFactory.TakeLastWarning());
    }

    /// <summary>The most recent warning wins, matching first-failure reporting.</summary>
    [Fact]
    public void LatestWarningReplacesEarlierOne()
    {
        ChdSharpLoggerFactory.TakeLastWarning();

        ILogger logger = new ChdSharpLoggerFactory().CreateLogger("CHDSharp.Chd");
        logger.Log(LogLevel.Warning, new EventId(0), "first", null, static (s, _) => s);
        logger.Log(LogLevel.Error, new EventId(0), "second", null, static (s, _) => s);

        var captured = ChdSharpLoggerFactory.TakeLastWarning();
        Assert.NotNull(captured);
        Assert.Contains("second", captured);
    }
}
