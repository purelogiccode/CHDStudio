using System.Net;
using System.Text.Json;
using CHDStudio.Models;
using Serilog;

namespace CHDStudio.Services;

/// <summary>
///     Builds the CHD Studio donation page link and checks completed donations through the
///     website's existing donation verification API.
/// </summary>
/// <remarks>
///     The request is only sent when the user asks to check a donation or when a previously saved
///     email is refreshed at startup. The email is never written to the application log.
/// </remarks>
internal static class DonationService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Builds the donation page URL with the application slug and optional email.</summary>
    /// <param name="email">Saved donation email, or <see langword="null" /> to omit it.</param>
    /// <returns>The donation page URI.</returns>
    internal static Uri CreateDonationPageUri(string? email)
    {
        var url =
            $"{AppConfig.DonationUrl}?app={Uri.EscapeDataString(AppConfig.DonationApplicationSlug)}";
        if (!string.IsNullOrWhiteSpace(email))
        {
            url += $"&email={Uri.EscapeDataString(email.Trim())}";
        }

        return new Uri(url, UriKind.Absolute);
    }

    /// <summary>Verifies whether the supplied email has a completed donation for CHD Studio.</summary>
    /// <param name="email">Email address used on the donation page.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The verification result. Network failures are reported as unavailable.</returns>
    internal static Task<DonationVerificationResult> VerifyAsync(
        string email,
        CancellationToken cancellationToken = default
    )
    {
        return VerifyAsync(
            email,
            AppConfig.DonationApiUrl,
            AppConfig.BugReportApiKey,
            AppHttpClient.Client,
            cancellationToken
        );
    }

    /// <summary>Sends one verification request. Exposed to tests with injected dependencies.</summary>
    /// <param name="email">Email address used on the donation page.</param>
    /// <param name="apiUrl">Verification endpoint.</param>
    /// <param name="apiKey">Shared API key sent in the X-API-KEY header.</param>
    /// <param name="httpClient">HTTP client to use.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The verification result. Network failures are reported as unavailable.</returns>
    internal static async Task<DonationVerificationResult> VerifyAsync(
        string email,
        string apiUrl,
        string apiKey,
        HttpClient httpClient,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        if (string.IsNullOrWhiteSpace(email)
            || string.IsNullOrWhiteSpace(apiUrl)
            || string.IsNullOrWhiteSpace(apiKey))
        {
            return new DonationVerificationResult(DonationVerificationStatus.NotDonated);
        }

        try
        {
            var payload = new
            {
                email = email.Trim(),
                applicationSlug = AppConfig.DonationApplicationSlug
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, apiUrl);
            request.Content = JsonContent.Create(
                payload,
                options: JsonOptions
            );
            request.Headers.TryAddWithoutValidation("X-API-KEY", apiKey);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken
            );
            timeoutCts.CancelAfter(RequestTimeout);
            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LogFailure(response.StatusCode);
                return new DonationVerificationResult(DonationVerificationStatus.Unavailable);
            }

            var result = await response.Content
                .ReadFromJsonAsync<DonationVerificationResponse>(JsonOptions, timeoutCts.Token)
                .ConfigureAwait(false);
            if (result is null)
            {
                Log.Warning("The donation verification API returned an empty response.");
                return new DonationVerificationResult(DonationVerificationStatus.Unavailable);
            }

            return result.Donated
                ? new DonationVerificationResult(
                    DonationVerificationStatus.Donated,
                    result.TotalAmount,
                    result.LastDonationAtUtc
                )
                : new DonationVerificationResult(DonationVerificationStatus.NotDonated);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Information(ex, "The donation verification request timed out.");
            return new DonationVerificationResult(DonationVerificationStatus.Unavailable);
        }
        catch (HttpRequestException ex)
        {
            Log.Information(ex, "Could not reach the donation verification service.");
            return new DonationVerificationResult(DonationVerificationStatus.Unavailable);
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "The donation verification API returned invalid JSON.");
            return new DonationVerificationResult(DonationVerificationStatus.Unavailable);
        }
        catch (NotSupportedException ex)
        {
            Log.Warning(
                ex,
                "The donation verification API response format is not supported."
            );
            return new DonationVerificationResult(DonationVerificationStatus.Unavailable);
        }
        catch (InvalidOperationException ex)
        {
            Log.Warning(ex, "The donation verification request could not be created.");
            return new DonationVerificationResult(DonationVerificationStatus.Unavailable);
        }
    }

    /// <summary>Logs a non-success status without treating temporary outages as application bugs.</summary>
    /// <param name="statusCode">Status code returned by the verification service.</param>
    private static void LogFailure(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        if (statusCode == HttpStatusCode.Unauthorized)
        {
            Log.Warning("The donation verification API rejected the configured API key.");
            return;
        }

        if (statusCode == HttpStatusCode.NotFound
            || statusCode == HttpStatusCode.TooManyRequests
            || code >= 500)
        {
            Log.Information(
                "Donation verification is temporarily unavailable. Status: {StatusCode}.",
                code
            );
            return;
        }

        Log.Warning("Donation verification failed with status {StatusCode}.", code);
    }

    /// <summary>Response body returned by the donation verification endpoint.</summary>
    /// <param name="Donated">True when a completed donation exists.</param>
    /// <param name="TotalAmount">Total donated amount.</param>
    /// <param name="LastDonationAtUtc">UTC time of the most recent completed donation.</param>
    private sealed record DonationVerificationResponse(
        bool Donated,
        decimal TotalAmount,
        DateTime? LastDonationAtUtc
    );
}
