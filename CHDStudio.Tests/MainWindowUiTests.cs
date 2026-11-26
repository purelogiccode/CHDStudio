using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using CHDStudio.Dialogs;
using CHDStudio.Models;
using System.Collections.ObjectModel;
using System.Reflection;

namespace CHDStudio.Tests;

/// <summary>Headless UI tests for the donation banner and the failed-file actions.</summary>
[Collection("Avalonia UI")]
public class MainWindowUiTests
{
    private static readonly string[] FailureButtonNames =
    [
        "SelectFailedConversionButton",
        "MoveFailedConversionButton",
        "CopyFailureDetailsButton",
        "SubmitSampleButton"
    ];

    [Fact]
    public void MainWindow_ShowsDonationBannerForANewInstallation()
    {
        UiTestSession.Run(() =>
        {
            using var window = new MainWindow(new DonationState());

            var banner = window.FindControl<Border>("DonationBanner");
            var text = window.FindControl<TextBlock>("DonationBannerText");
            var panel = window.FindControl<WrapPanel>("FailedActionPanel");

            Assert.NotNull(banner);
            Assert.True(banner.IsVisible);
            Assert.NotNull(text);
            Assert.Contains("donationware", text.Text, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(window.FindControl<Button>("DonationDonateButton"));
            Assert.NotNull(window.FindControl<Button>("DonationStatusButton"));
            Assert.NotNull(window.FindControl<Button>("DismissDonationBannerButton"));
            Assert.NotNull(panel);
        });
    }

    [Fact]
    public void MainWindow_DismissingTheDonationBannerHidesItForTheSession()
    {
        UiTestSession.Run(() =>
        {
            using var window = new MainWindow(new DonationState());
            var banner = window.FindControl<Border>("DonationBanner");
            var dismiss = window.FindControl<Button>("DismissDonationBannerButton");

            Assert.NotNull(banner);
            Assert.NotNull(dismiss);
            Assert.True(banner.IsVisible);

            dismiss.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.False(banner.IsVisible);
        });
    }

    [Fact]
    public void MainWindow_HidesTheDonationBannerForRecognizedDonors()
    {
        UiTestSession.Run(() =>
        {
            using var window = new MainWindow(new DonationState { HasDonated = true });

            var banner = window.FindControl<Border>("DonationBanner");

            Assert.NotNull(banner);
            Assert.False(banner.IsVisible);
        });
    }

    [Fact]
    public void FailedActionPanel_WrapsAllFourFailureButtons()
    {
        UiTestSession.Run(() =>
        {
            using var window = new MainWindow(new DonationState());
            var panel = window.FindControl<WrapPanel>("FailedActionPanel");

            Assert.NotNull(panel);
            Assert.Equal(6, panel.Children.OfType<Button>().Count());
            Assert.Equal(
                4,
                panel.Children
                    .OfType<Button>()
                    .Count(button =>
                        button.Name
                            is "SelectFailedConversionButton"
                            or "MoveFailedConversionButton"
                            or "CopyFailureDetailsButton"
                            or "SubmitSampleButton"
                    )
            );
            Assert.Contains(panel.Children, child => child is Button { Name: "SelectFailedConversionButton" });
            Assert.Contains(panel.Children, child => child is Button { Name: "MoveFailedConversionButton" });
            Assert.Contains(panel.Children, child => child is Button { Name: "CopyFailureDetailsButton" });
            Assert.Contains(panel.Children, child => child is Button { Name: "SubmitSampleButton" });
            Assert.Equal(10, panel.ItemSpacing);
            Assert.Equal(8, panel.LineSpacing);
            Assert.Equal(Avalonia.Layout.HorizontalAlignment.Right, panel.HorizontalAlignment);
        });
    }

    [Fact]
    public void FailedActionButtons_AreDisabledWithoutFailuresAndEnabledWithOne()
    {
        UiTestSession.Run(() =>
        {
            using var window = new MainWindow(new DonationState());
            SetControlsState(window, enabled: true);

            foreach (var name in FailureButtonNames)
            {
                Assert.False(window.FindControl<Button>(name)!.IsEnabled, $"{name} starts disabled");
            }

            GetConversionFiles(window)
                .Add(
                    new FileItem
                    {
                        FileName = "broken.cue",
                        FullPath = @"D:\roms\broken.cue",
                        Status = "Failed",
                        FailureReason = "Invalid track"
                    }
                );
            SetControlsState(window, enabled: true);

            foreach (var name in FailureButtonNames)
            {
                Assert.True(window.FindControl<Button>(name)!.IsEnabled, $"{name} enables with failures");
            }
        });
    }

    [Fact]
    public void SelectFailedConversion_SelectsOnlyFailedRows()
    {
        UiTestSession.Run(() =>
        {
            using var window = new MainWindow(new DonationState());
            var files = GetConversionFiles(window);
            files.Add(
                new FileItem
                {
                    FileName = "failed.cue",
                    FullPath = @"D:\roms\failed.cue",
                    Status = "Failed",
                    IsSelected = false
                }
            );
            files.Add(
                new FileItem
                {
                    FileName = "good.iso",
                    FullPath = @"D:\roms\good.iso",
                    Status = "OK",
                    IsSelected = true
                }
            );
            files.Add(
                new FileItem
                {
                    FileName = "pending.gdi",
                    FullPath = @"D:\roms\pending.gdi",
                    Status = "Pending",
                    IsSelected = true
                }
            );
            SetControlsState(window, enabled: true);

            var button = window.FindControl<Button>("SelectFailedConversionButton");
            Assert.NotNull(button);
            Assert.True(button.IsEnabled);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.True(files[0].IsSelected);
            Assert.False(files[1].IsSelected);
            Assert.False(files[2].IsSelected);
        });
    }

    [Fact]
    public void MoveFailedConversion_MovesTheWholeDescriptorSetIntoTheFailedFolder()
    {
        UiTestSession.RunAsync(async () =>
        {
            var root = Path.Join(
                Path.GetTempPath(),
                $"CHDStudio_UiMoveFailed_{Guid.NewGuid():N}"
            );
            var source = Path.Join(root, "source");
            Directory.CreateDirectory(source);
            try
            {
                var cue = Path.Join(source, "broken.cue");
                var bin = Path.Join(source, "broken.bin");
                await File.WriteAllTextAsync(
                    cue,
                    "FILE \"broken.bin\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n"
                );
                await File.WriteAllBytesAsync(bin, new byte[2352]);

                using var window = new MainWindow(new DonationState());
                var inputBox = window.FindControl<TextBox>("ConversionInputFolderTextBox");
                Assert.NotNull(inputBox);
                inputBox.Text = source;

                var files = GetConversionFiles(window);
                files.Add(
                    new FileItem
                    {
                        FileName = "broken.cue",
                        FullPath = cue,
                        Status = "Failed",
                        FailureReason = "Invalid track"
                    }
                );
                SetControlsState(window, enabled: true);

                var button = window.FindControl<Button>("MoveFailedConversionButton");
                Assert.NotNull(button);
                Assert.True(button.IsEnabled);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                var expectedCue = Path.Join(source, "Failed", "broken", "broken.cue");
                var expectedBin = Path.Join(source, "Failed", "broken", "broken.bin");
                for (var attempt = 0;
                     attempt < 100 && !File.Exists(expectedCue);
                     attempt++)
                {
                    await Task.Delay(50);
                }

                Assert.True(File.Exists(expectedCue), "The failed cue should be moved");
                Assert.True(File.Exists(expectedBin), "The referenced bin should move with it");
                Assert.False(File.Exists(cue));
                Assert.False(File.Exists(bin));
                Assert.Empty(files);
                Assert.False(button.IsEnabled);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                }
                catch (IOException)
                {
                    // Best-effort cleanup only.
                }
            }
        });
    }

    [Fact]
    public void DonationBanner_IsABorderContainingTheDonateButton()
    {
        UiTestSession.Run(() =>
        {
            using var window = new MainWindow(new DonationState());
            var banner = window.FindControl<Border>("DonationBanner");
            var donate = window.FindControl<Button>("DonationDonateButton");

            Assert.NotNull(banner);
            Assert.NotNull(donate);
            Assert.IsType<Border>(banner);
            Assert.Contains(donate, banner.Child!.GetVisualDescendants().OfType<Button>());
            Assert.Contains("Donate", donate.Content as string, StringComparison.Ordinal);
            Assert.Equal("Open the CHD Studio donation page on purelogiccode.com", ToolTip.GetTip(donate));
        });
    }

    [Fact]
    public void DonationBanner_TooltipsExplainTheOptionalSupport()
    {
        UiTestSession.Run(() =>
        {
            using var window = new MainWindow(new DonationState());
            var banner = window.FindControl<Border>("DonationBanner")!;

            Assert.Contains(
                "optional",
                ToolTip.GetTip(banner) as string,
                StringComparison.OrdinalIgnoreCase
            );
            Assert.Contains(
                "email",
                ToolTip.GetTip(window.FindControl<Button>("DonationStatusButton")!) as string,
                StringComparison.OrdinalIgnoreCase
            );
            Assert.Contains(
                "next launch",
                ToolTip.GetTip(window.FindControl<Button>("DismissDonationBannerButton")!) as string,
                StringComparison.OrdinalIgnoreCase
            );
        });
    }

    [Theory]
    [InlineData("user@example.com", true)]
    [InlineData("user.name+tag@example.co.uk", true)]
    [InlineData(" user@example.com ", true)]
    [InlineData("", false)]
    [InlineData("user", false)]
    [InlineData("user@", false)]
    [InlineData("@example.com", false)]
    [InlineData("user@example.com extra", false)]
    [InlineData("user@example..com", false)]
    public void DonationEmailDialog_ValidatesAddresses(string email, bool expected)
    {
        UiTestSession.Run(() =>
        {
            var dialog = new DonationEmailWindow();
            dialog.Show();
            var box = dialog.FindControl<TextBox>("EmailBox");
            var validation = dialog.FindControl<TextBlock>("ValidationText");

            Assert.NotNull(box);
            Assert.NotNull(validation);
            box.Text = email;
            ClickButton(dialog, "Check Donation");

            Assert.Equal(expected, dialog.Email is not null);
            Assert.Equal(!expected, validation.IsVisible);
            if (!expected) dialog.Close();
        });
    }

    [Fact]
    public void FailureButtonTooltips_ExplainTheirSelectionRules()
    {
        UiTestSession.Run(() =>
        {
            using var window = new MainWindow(new DonationState());

            AssertTooltipContains(window, "SelectFailedConversionButton", "clear every other selection");
            AssertTooltipContains(window, "MoveFailedConversionButton", "not just the checked rows");
            AssertTooltipContains(window, "CopyFailureDetailsButton", "all rows marked Failed");
            AssertTooltipContains(window, "SubmitSampleButton", "exactly one checked row");
        });
    }

    [Fact]
    public void FailureButtonTooltips_WrapAtAReadableWidth()
    {
        UiTestSession.Run(() =>
        {
            using var window = new MainWindow(new DonationState());

            foreach (var name in new[]
                     {
                         "SelectFailedConversionButton",
                         "MoveFailedConversionButton",
                         "CopyFailureDetailsButton",
                         "SubmitSampleButton"
                     })
            {
                var tip = GetTooltipTextBlock(window, name);
                Assert.True(tip.MaxWidth <= 420, $"{name} tooltip max width");
                Assert.Equal(TextWrapping.Wrap, tip.TextWrapping);
            }
        });
    }

    [Fact]
    public void DonationEmailDialog_PrefillsTheSavedEmail()
    {
        UiTestSession.Run(() =>
        {
            var dialog = new DonationEmailWindow("donor@example.com");

            var box = dialog.FindControl<TextBox>("EmailBox");

            Assert.NotNull(box);
            Assert.Equal("donor@example.com", box.Text);
        });
    }

    [Fact]
    public void DonationEmailDialog_RejectsAnInvalidAddress()
    {
        UiTestSession.Run(() =>
        {
            var dialog = new DonationEmailWindow();
            dialog.Show();
            var box = dialog.FindControl<TextBox>("EmailBox");
            var validation = dialog.FindControl<TextBlock>("ValidationText");

            Assert.NotNull(box);
            Assert.NotNull(validation);
            box.Text = "not an email address";
            ClickButton(dialog, "Check Donation");

            Assert.True(validation.IsVisible);
            Assert.Null(dialog.Email);
            dialog.Close();
        });
    }

    [Fact]
    public void DonationEmailDialog_AcceptsAValidAddress()
    {
        UiTestSession.Run(() =>
        {
            var dialog = new DonationEmailWindow();
            dialog.Show();
            var box = dialog.FindControl<TextBox>("EmailBox");

            Assert.NotNull(box);
            box.Text = " donor@example.com ";
            ClickButton(dialog, "Check Donation");

            Assert.Equal("donor@example.com", dialog.Email);
        });
    }

    [Fact]
    public void DonationEmailDialog_CancelLeavesTheEmailUnset()
    {
        UiTestSession.Run(() =>
        {
            var dialog = new DonationEmailWindow("donor@example.com");
            dialog.Show();

            ClickButton(dialog, "Cancel");

            Assert.Null(dialog.Email);
        });
    }

    [Fact]
    public void DonationEmailDialog_EscapeLeavesTheEmailUnset()
    {
        UiTestSession.Run(() =>
        {
            var dialog = new DonationEmailWindow("donor@example.com");
            dialog.Show();

            dialog.RaiseEvent(
                new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.Escape
                }
            );

            Assert.Null(dialog.Email);
        });
    }

    [Fact]
    public void DonationEmailDialog_EnterConfirmsAValidAddress()
    {
        UiTestSession.Run(() =>
        {
            var dialog = new DonationEmailWindow();
            dialog.Show();
            var box = dialog.FindControl<TextBox>("EmailBox");

            Assert.NotNull(box);
            box.Text = "donor@example.com";
            dialog.RaiseEvent(
                new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.Enter
                }
            );

            Assert.Equal("donor@example.com", dialog.Email);
        });
    }

    [Fact]
    public void DonationEmailDialog_HidesValidationWhenTheUserEditsTheAddress()
    {
        UiTestSession.Run(() =>
        {
            var dialog = new DonationEmailWindow();
            dialog.Show();
            var box = dialog.FindControl<TextBox>("EmailBox");
            var validation = dialog.FindControl<TextBlock>("ValidationText");

            Assert.NotNull(box);
            Assert.NotNull(validation);
            box.Text = "bad";
            ClickButton(dialog, "Check Donation");
            Assert.True(validation.IsVisible);

            box.Text = string.Empty;
            dialog.KeyTextInput("donor@example.com");

            Assert.False(validation.IsVisible);
            dialog.Close();
        });
    }

    [Fact]
    public void AboutWindow_DonationLinkUsesTheChdStudioApplication()
    {
        UiTestSession.Run(() =>
        {
            var about = new AboutWindow();
            about.Show();

            var link = about
                .GetVisualDescendants()
                .OfType<Button>()
                .Single(button =>
                    string.Equals(
                        button.Tag as string,
                        "https://www.purelogiccode.com/donate?app=chd-studio",
                        StringComparison.Ordinal
                    )
                );

            Assert.Contains(
                "donation",
                ToolTip.GetTip(link) as string,
                StringComparison.OrdinalIgnoreCase
            );
            about.Close();
        });
    }

    /// <summary>Gets the main window's conversion file collection for test setup.</summary>
    /// <param name="window">Main window under test.</param>
    /// <returns>The live conversion file collection.</returns>
    private static ObservableCollection<FileItem> GetConversionFiles(MainWindow window)
    {
        var field = typeof(MainWindow).GetField(
            "_conversionFiles",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        return Assert.IsType<ObservableCollection<FileItem>>(field?.GetValue(window));
    }

    /// <summary>Invokes the production control-state update so action buttons reflect the list.</summary>
    /// <param name="window">Main window under test.</param>
    /// <param name="enabled">True for the idle state.</param>
    private static void SetControlsState(MainWindow window, bool enabled)
    {
        var method = typeof(MainWindow).GetMethod(
            "SetControlsState",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(method);
        method.Invoke(window, [enabled]);
    }

    /// <summary>Finds a button by its content and raises its click event.</summary>
    /// <param name="window">Window containing the button.</param>
    /// <param name="content">Exact button content.</param>
    private static void ClickButton(Window window, string content)
    {
        var button = window
            .GetVisualDescendants()
            .OfType<Button>()
            .Single(button => string.Equals(button.Content as string, content, StringComparison.Ordinal));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>Asserts that a button's tooltip contains the expected explanation.</summary>
    /// <param name="window">Window containing the button.</param>
    /// <param name="buttonName">Name of the button.</param>
    /// <param name="expected">Text expected inside the tooltip.</param>
    private static void AssertTooltipContains(Window window, string buttonName, string expected)
    {
        var tip = GetTooltipTextBlock(window, buttonName);
        Assert.Contains(expected, tip.Text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Gets the wrapped text block used as a button tooltip.</summary>
    /// <param name="window">Window containing the button.</param>
    /// <param name="buttonName">Name of the button.</param>
    /// <returns>The tooltip text block.</returns>
    private static TextBlock GetTooltipTextBlock(Window window, string buttonName)
    {
        var button = window.FindControl<Button>(buttonName);
        Assert.NotNull(button);
        var tip = ToolTip.GetTip(button);
        return Assert.IsType<TextBlock>(tip);
    }
}

/// <summary>Keeps the Avalonia UI tests serialized on the shared headless dispatcher.</summary>
[CollectionDefinition("Avalonia UI", DisableParallelization = true)]
public class AvaloniaUiCollection { }
