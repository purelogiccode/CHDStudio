using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(CHDStudio.Tests.UiTestApplication))]

namespace CHDStudio.Tests;

/// <summary>
///     Builds the real CHD Studio application on Avalonia's headless platform so UI tests load the
///     same styles, resources and compiled XAML as the shipped app.
/// </summary>
public static class UiTestApplication
{
    /// <summary>Creates the headless application builder used by every Avalonia UI test.</summary>
    /// <returns>The configured application builder.</returns>
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }
}

/// <summary>Runs test code on the single Avalonia headless dispatcher thread.</summary>
internal static class UiTestSession
{
    private static readonly Lazy<HeadlessUnitTestSession> Session = new(static () =>
        HeadlessUnitTestSession.StartNew(
            typeof(UiTestApplication),
            AvaloniaTestIsolationLevel.PerAssembly
        )
    );

    /// <summary>Runs an action on the headless UI thread.</summary>
    /// <param name="action">Action to run.</param>
    internal static void Run(Action action)
    {
        Session.Value.Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>Runs an asynchronous action on the headless UI thread and waits for it.</summary>
    /// <param name="action">Asynchronous action to run.</param>
    internal static void RunAsync(Func<Task> action)
    {
        Session.Value
            .Dispatch(
                async () =>
                {
                    await action().ConfigureAwait(true);
                    return true;
                },
                CancellationToken.None
            )
            .GetAwaiter()
            .GetResult();
    }
}
