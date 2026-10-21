using System.Text;
using Avalonia;
using CHDStudio.Services;

namespace CHDStudio;

/// <summary>
///     Entry point for the Avalonia (cross-platform) front end of CHD Studio.
/// </summary>
internal static class Program
{
    /// <summary>
    ///     Application entry point. Starts the classic desktop lifetime and turns a missing
    ///     native rendering library into an actionable message instead of an unhandled crash.
    /// </summary>
    /// <param name="args">Command-line arguments (an optional input folder path).</param>
    /// <returns>0 when the application exits normally; 1 when a native library is missing.</returns>
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception ex) when (BugReportService.IsMissingNativeLibrary(ex))
        {
            WriteStartupFailure(ex);
            return 1;
        }
    }

    /// <summary>
    ///     Creates and configures the <see cref="AppBuilder" /> used by the application and by the
    ///     Avalonia designer tooling.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder
            .Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }

    /// <summary>
    ///     Writes a startup failure caused by missing native libraries to stderr and to a log file
    ///     under the user's local application data folder. The release zip contains all of these
    ///     files; extracting the complete archive beside the executable fixes the problem.
    /// </summary>
    /// <param name="exception">The native-library exception that stopped startup.</param>
    private static void WriteStartupFailure(Exception exception)
    {
        var message = new StringBuilder()
            .AppendLine("CHD Studio could not start because a required native library is missing.")
            .AppendLine()
            .AppendLine(GetNativeLibraryHint())
            .AppendLine()
            .AppendLine($"Executable folder: {AppContext.BaseDirectory}")
            .AppendLine()
            .AppendLine(exception.ToString())
            .ToString();

        try
        {
            Console.Error.WriteLine(message);
        }
        catch
        {
            // A WinExe has no console attached; the log file below is the fallback.
        }

        TryWriteStartupLog(message);
    }

    /// <summary>
    ///     Returns the platform-specific list of native files that must sit beside the executable.
    /// </summary>
    /// <returns>A sentence naming the files the release zip provides.</returns>
    private static string GetNativeLibraryHint()
    {
        if (OperatingSystem.IsWindows())
        {
            return "Extract every file from the release zip into the same folder as CHDStudio.exe "
                   + "(including libHarfBuzzSharp.dll, libSkiaSharp.dll and av_libglesv2.dll), then start the app again.";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "Extract every file from the release zip into the same folder as CHDStudio "
                   + "(including libHarfBuzzSharp.dylib, libSkiaSharp.dylib and libAvaloniaNative.dylib), then start the app again.";
        }

        return "Extract every file from the release zip into the same folder as CHDStudio "
               + "(including libHarfBuzzSharp.so and libSkiaSharp.so), then start the app again.";
    }

    /// <summary>
    ///     Writes the startup message to <c>startup-error.log</c>, first under the user's local
    ///     application data folder and then beside the executable when that folder is unavailable.
    /// </summary>
    /// <param name="message">The complete diagnostic message.</param>
    private static void TryWriteStartupLog(string message)
    {
        try
        {
            var logFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CHDStudio"
            );
            Directory.CreateDirectory(logFolder);
            File.WriteAllText(Path.Combine(logFolder, "startup-error.log"), message);
            return;
        }
        catch
        {
            // Fall through to the executable folder.
        }

        try
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup-error.log"), message);
        }
        catch
        {
            // Nothing else can be done without the native rendering libraries.
        }
    }
}
