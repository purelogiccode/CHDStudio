using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CHDStudio.Dialogs;
using CHDStudio.Models;
using CHDStudio.Services;
using CHDStudio.Utilities;

namespace CHDStudio;

/// <summary>
///     About window displaying application version and information.
/// </summary>
internal partial class AboutWindow : Window
{
    internal AboutWindow()
    {
        InitializeComponent();

        AppVersionTextBlock.Text = $"Version: {GetApplicationVersion()}";
        DescriptionTextBlock.Text =
            "A cross-platform utility for batch converting disc images (cue, iso, img, ccd, mds, pbp, cso, isz, ecm, split sets and archives) to CHD, and for verifying, exploring and extracting existing CHD files. Runs on Windows, Linux and macOS, with x64 and ARM64 builds.";

        KeyDown += AboutWindow_KeyDown;
    }

    /// <summary>
    ///     Handles the F8 hotkey by saving a screenshot of this window.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The key event arguments.</param>
    private void AboutWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.F8) return;

        ScreenshotService.CaptureAndLog(this);
        e.Handled = true;
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OpenUrl_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url } || string.IsNullOrWhiteSpace(url)) return;

        if (UrlLauncher.TryOpen(url, out var error)) return;

        _ = MessageBox.ShowAsync(
            this,
            $"Unable to open link: {error}",
            "Error",
            MessageBoxButton.Ok,
            MessageBoxImage.Error
        );
    }

    private static string GetApplicationVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version?.ToString() ?? "Unknown";
    }
}