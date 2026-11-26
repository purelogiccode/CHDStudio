using System.Net.Mail;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CHDStudio.Services;
using Serilog;

namespace CHDStudio.Dialogs;

/// <summary>
///     Collects the optional email used to recognize a donation and returns it to the caller.
///     The caller performs the network check after the dialog closes.
/// </summary>
internal partial class DonationEmailWindow : Window
{
    /// <summary>Initializes a new instance of the <see cref="DonationEmailWindow" /> class.</summary>
    internal DonationEmailWindow()
        : this(null) { }

    /// <summary>Initializes a new instance of the <see cref="DonationEmailWindow" /> class.</summary>
    /// <param name="email">Saved donation email, or <see langword="null" />.</param>
    internal DonationEmailWindow(string? email)
    {
        InitializeComponent();

        EmailBox.Text = email ?? string.Empty;
        EmailBox.TextChanged += EmailBox_TextChanged;
        Opened += (_, _) =>
        {
            EmailBox.Focus();
            EmailBox.SelectAll();
        };
        KeyDown += DonationEmailWindow_KeyDown;
    }

    /// <summary>
    ///     Gets the confirmed email, or <see langword="null" /> when the dialog was cancelled.
    /// </summary>
    internal string? Email { get; private set; }

    /// <summary>Shows the dialog and returns the confirmed email.</summary>
    /// <param name="owner">Owner window.</param>
    /// <param name="currentEmail">Saved donation email, or <see langword="null" />.</param>
    /// <returns>The confirmed email, or <see langword="null" /> when cancelled.</returns>
    internal static async Task<string?> ShowAsync(Window owner, string? currentEmail)
    {
        ArgumentNullException.ThrowIfNull(owner);

        try
        {
            var dialog = new DonationEmailWindow(currentEmail);
            await dialog.ShowDialog(owner).ConfigureAwait(true);
            return dialog.Email;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error(ex, "The donation email dialog failed.");
            throw;
        }
    }

    /// <summary>Handles Enter, Escape and the F8 screenshot hotkey.</summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The key event arguments.</param>
    private void DonationEmailWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F8)
        {
            ScreenshotService.CaptureAndLog(this);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            CheckDonation();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    /// <summary>Confirms the email and closes the dialog.</summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The click event arguments.</param>
    private void CheckButton_Click(object? sender, RoutedEventArgs e)
    {
        CheckDonation();
    }

    /// <summary>Closes the dialog without confirming an email.</summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The click event arguments.</param>
    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>Closes the dialog from the title bar.</summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The click event arguments.</param>
    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>Starts a window move drag when the title bar is pressed with the left button.</summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The pointer event arguments.</param>
    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    /// <summary>Hides the validation message as soon as the user edits the email.</summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The text-changed event arguments.</param>
    private void EmailBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        ValidationText.IsVisible = false;
    }

    /// <summary>Validates the email and closes the dialog when it is acceptable.</summary>
    private void CheckDonation()
    {
        var email = EmailBox.Text?.Trim() ?? string.Empty;
        if (!IsValidEmail(email))
        {
            ValidationText.IsVisible = true;
            EmailBox.Focus();
            return;
        }

        Email = email;
        Close();
    }

    /// <summary>Checks that the text is a plain, complete email address.</summary>
    /// <param name="email">Text to validate.</param>
    /// <returns>True when the text is a valid email address.</returns>
    private static bool IsValidEmail(string email)
    {
        if (email.Length == 0 || email.Length > 254)
        {
            return false;
        }

        return MailAddress.TryCreate(email, out var address)
               && string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase);
    }
}
