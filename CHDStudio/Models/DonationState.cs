namespace CHDStudio.Models;

/// <summary>
///     Locally persisted donation recognition state. The email is only sent to the website when
///     the user asks to check a donation or when a previously saved email is refreshed at startup.
/// </summary>
internal sealed class DonationState
{
    /// <summary>Gets or sets the email address used on purelogiccode.com.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether a completed donation was recognized.</summary>
    public bool HasDonated { get; set; }

    /// <summary>Gets or sets the UTC time of the last verification request.</summary>
    public DateTime? LastCheckedUtc { get; set; }
}
