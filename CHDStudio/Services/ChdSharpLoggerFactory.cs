using Microsoft.Extensions.Logging;
using Serilog.Events;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using ILoggerFactory = Microsoft.Extensions.Logging.ILoggerFactory;
using ILoggerProvider = Microsoft.Extensions.Logging.ILoggerProvider;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace CHDStudio.Services;

/// <summary>
///     Bridges CHDSharp's <see cref="ILoggerFactory" /> to the application's Serilog pipeline.
///     Without this the library logs into a null logger, so a verification failure arrives as the
///     bare code "Decompression failed" and the hunk-level reason (index, codec, file offset,
///     compressed length, codec message) is discarded before it can reach the log or a bug report.
///     Warning-and-above events are also kept in <see cref="TakeLastWarning" /> so the caller that
///     reports the failure can attach the concrete cause to its own message instead of filing a
///     second, near-duplicate report.
/// </summary>
internal sealed class ChdSharpLoggerFactory : ILoggerFactory
{
    private static readonly Lock Gate = new();
    private static string? _lastWarning;

    /// <summary>Creates a logger for the given CHDSharp category.</summary>
    /// <param name="categoryName">Category name supplied by CHDSharp.</param>
    /// <returns>A logger that forwards to Serilog.</returns>
    public ILogger CreateLogger(string categoryName)
    {
        return new ChdSharpLogger(categoryName);
    }

    /// <summary>Ignored: Serilog is the only sink.</summary>
    /// <param name="provider">Provider supplied by the logging infrastructure.</param>
    public void AddProvider(ILoggerProvider provider)
    {
        // Serilog is configured directly; external providers are not used.
    }

    /// <summary>Releases the factory. The Serilog logger is owned by the application.</summary>
    public void Dispose()
    {
        // Nothing to release.
    }

    /// <summary>
    ///     Returns the most recent warning-or-above message CHDSharp logged (or null) and clears
    ///     it, so one failure's detail cannot be attached to a later, unrelated report.
    /// </summary>
    /// <returns>The last captured diagnostic, or null when none was recorded.</returns>
    internal static string? TakeLastWarning()
    {
        lock (Gate)
        {
            var value = _lastWarning;
            _lastWarning = null;
            return value;
        }
    }

    /// <summary>Stores a warning-or-above message for the caller to attach to its own report.</summary>
    /// <param name="message">The rendered message.</param>
    private static void Capture(string message)
    {
        lock (Gate)
        {
            _lastWarning = message;
        }
    }

    /// <summary>Forwards CHDSharp log events to Serilog.</summary>
    private sealed class ChdSharpLogger : ILogger
    {
        private readonly string _category;

        /// <summary>Initializes a logger for one category.</summary>
        /// <param name="category">Category name supplied by CHDSharp.</param>
        internal ChdSharpLogger(string category)
        {
            _category = category;
        }

        /// <summary>Scopes are not modelled by Serilog here.</summary>
        /// <typeparam name="TState">Scope state type.</typeparam>
        /// <param name="state">Scope state.</param>
        /// <returns>Always null.</returns>
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        /// <summary>Reports whether the level maps to an enabled Serilog level.</summary>
        /// <param name="logLevel">Level to test.</param>
        /// <returns>True when Serilog would write the event.</returns>
        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel != LogLevel.None;
        }

        /// <summary>
        ///     Writes one CHDSharp event to Serilog and captures warning-or-above text for the
        ///     caller to attach to its own, already-classified error.
        /// </summary>
        /// <typeparam name="TState">Log state type.</typeparam>
        /// <param name="logLevel">Event level.</param>
        /// <param name="eventId">Event identifier.</param>
        /// <param name="state">Event state.</param>
        /// <param name="exception">Associated exception, if any.</param>
        /// <param name="formatter">Formatter that renders the message.</param>
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (logLevel == LogLevel.None || formatter is null)
                return;

            var message = formatter(state, exception);

            Serilog.Log.Write(
                Map(logLevel),
                exception,
                "{ChdSharpCategory}: {Message}",
                _category,
                message
            );

            // Capture from the CHDSharp level, not the Serilog one: a Warning is the level the
            // library uses for both real hunk failures and user-data notices, and the caller
            // decides whether its own failure is reportable.
            if (logLevel >= LogLevel.Warning)
                Capture($"{_category}: {message}");
        }

        /// <summary>
        ///     Maps a <see cref="LogLevel" /> to the matching Serilog level. CHDSharp's
        ///     <see cref="LogLevel.Warning" /> is deliberately written at
        ///     <see cref="LogEventLevel.Information" />: the library also uses it for user-data
        ///     notices (for example "track has subcode data, it will be omitted"), and every
        ///     Warning-and-above Serilog event is forwarded to the bug-report API. Genuine
        ///     failures are reported by the application's own classified error message, which
        ///     carries the captured detail.
        /// </summary>
        /// <param name="logLevel">Level to map.</param>
        /// <returns>The equivalent Serilog level.</returns>
        private static LogEventLevel Map(LogLevel logLevel)
        {
            return logLevel switch
            {
                LogLevel.Trace => LogEventLevel.Verbose,
                LogLevel.Debug => LogEventLevel.Debug,
                LogLevel.Information => LogEventLevel.Information,
                LogLevel.Warning => LogEventLevel.Information,
                LogLevel.Error => LogEventLevel.Error,
                LogLevel.Critical => LogEventLevel.Fatal,
                _ => LogEventLevel.Debug
            };
        }
    }
}
