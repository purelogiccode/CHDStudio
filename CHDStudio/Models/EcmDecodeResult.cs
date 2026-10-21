using System.Diagnostics.CodeAnalysis;

namespace CHDStudio.Models;

/// <summary>
///     Outcome of decoding an ECM file.
/// </summary>
/// <param name="Success">True when the image was written completely.</param>
/// <param name="BytesWritten">Size of the restored image.</param>
/// <param name="FailureReason">User-facing explanation, or null on success.</param>
[SuppressMessage(
    "ReSharper",
    "NotAccessedPositionalProperty.Global",
    Justification = "Part of the decode result returned to callers and tests."
)]
internal sealed record EcmDecodeResult(
    bool Success,
    long BytesWritten,
    string? FailureReason
)
{
    /// <summary>Creates a successful result for the restored image.</summary>
    /// <param name="bytesWritten">Size of the restored image.</param>
    /// <returns>A successful <see cref="EcmDecodeResult" />.</returns>
    internal static EcmDecodeResult Succeeded(long bytesWritten)
    {
        return new EcmDecodeResult(true, bytesWritten, null);
    }

    /// <summary>Creates a failed result carrying a user-facing explanation.</summary>
    /// <param name="reason">User-facing explanation of the failure.</param>
    /// <returns>A failed <see cref="EcmDecodeResult" />.</returns>
    internal static EcmDecodeResult Failed(string reason)
    {
        return new EcmDecodeResult(false, 0, reason);
    }
}