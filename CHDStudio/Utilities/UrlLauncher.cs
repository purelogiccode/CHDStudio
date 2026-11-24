using System.ComponentModel;
using System.Diagnostics;

namespace CHDStudio.Utilities;

/// <summary>
///     Opens an HTTP(S) URL with the user's default browser. Windows refuses the shell request
///     with "Class not registered" when no default browser is associated with the protocol, so a
///     direct platform opener is tried as a fallback. Failures are returned as a message instead
///     of being reported as application bugs: the browser association is an installation detail.
/// </summary>
internal static class UrlLauncher
{
    /// <summary>
    ///     Tries to open <paramref name="url" />, first through the shell association and then
    ///     through the platform's standard opener.
    /// </summary>
    /// <param name="url">The URL to open.</param>
    /// <param name="error">A description of the failure when the URL could not be opened.</param>
    /// <returns>True when a process was started for the URL.</returns>
    internal static bool TryOpen(string url, out string? error)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            error = "the URL is empty";
            return false;
        }

        if (TryStartShell(url, out error))
            return true;

        var (fileName, arguments) = OperatingSystem.IsWindows()
            ? ("explorer.exe", new[] { url })
            : OperatingSystem.IsMacOS()
                ? ("open", new[] { url })
                : ("xdg-open", new[] { url });

        if (TryStartProcess(fileName, arguments, out var fallbackError))
        {
            error = null;
            return true;
        }

        error = $"{error}; {fallbackError}";
        return false;
    }

    /// <summary>Starts the URL through the operating system's shell association.</summary>
    /// <param name="url">The URL to open.</param>
    /// <param name="error">The shell failure message, or null on success.</param>
    /// <returns>True when a process was started.</returns>
    private static bool TryStartShell(string url, out string? error)
    {
        try
        {
            using var process = Process.Start(
                new ProcessStartInfo { FileName = url, UseShellExecute = true }
            );
            error = process is null ? "the shell returned no process" : null;
            return process is not null;
        }
        catch (
            Exception ex
        ) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Starts a platform opener executable with the URL as its argument.</summary>
    /// <param name="fileName">Executable to start.</param>
    /// <param name="arguments">Arguments to pass.</param>
    /// <param name="error">The failure message, or null on success.</param>
    /// <returns>True when a process was started.</returns>
    private static bool TryStartProcess(string fileName, string[] arguments, out string? error)
    {
        try
        {
            var startInfo = new ProcessStartInfo(fileName) { UseShellExecute = false };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo);
            error = process is null ? $"'{fileName}' returned no process" : null;
            return process is not null;
        }
        catch (
            Exception ex
        ) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            error = ex.Message;
            return false;
        }
    }
}
