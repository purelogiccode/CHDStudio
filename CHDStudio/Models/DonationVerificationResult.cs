namespace CHDStudio.Models;

/// <summary>The outcome of a donation verification request.</summary>
internal enum DonationVerificationStatus
{
    /// <summary>A completed donation was found for the supplied email.</summary>
    Donated,

    /// <summary>The service answered successfully but found no completed donation.</summary>
    NotDonated,

    /// <summary>The service could not be reached or returned an unexpected response.</summary>
    Unavailable
}

/// <summary>Describes the result of a donation verification request.</summary>
/// <param name="Status">Whether a donation was found, not found, or could not be checked.</param>
/// <param name="TotalAmount">Total donated amount reported by the service.</param>
/// <param name="LastDonationAtUtc">UTC time of the most recent completed donation.</param>
internal sealed record DonationVerificationResult(
    DonationVerificationStatus Status,
    decimal TotalAmount = 0m,
    DateTime? LastDonationAtUtc = null
);
