using System.Net;
using System.Text;
using CHDStudio.Models;
using CHDStudio.Services;

namespace CHDStudio.Tests;

public class DonationServiceTests
{
    private const string TestApiUrl = "https://example.com/api/donations/verify";
    private const string TestApiKey = "test-api-key";

    [Fact]
    public void CreateDonationPageUriIncludesApplicationAndEmail()
    {
        var uri = DonationService.CreateDonationPageUri("user+tag@example.com");

        Assert.Equal("https", uri.Scheme);
        Assert.Contains("app=chd-studio", uri.Query, StringComparison.Ordinal);
        Assert.Contains(
            "email=user%2Btag%40example.com",
            uri.Query,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void CreateDonationPageUriOmitsMissingEmail()
    {
        var uri = DonationService.CreateDonationPageUri(null);

        Assert.Contains("app=chd-studio", uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("email=", uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsyncSendsApiKeyAndApplicationSlug()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var handler = FakeHttpMessageHandler.WithAsyncHandler(
            async request =>
            {
                capturedRequest = request;
                capturedBody = await request.Content!.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {"donated":true,"applicationSlug":"chd-studio","totalAmount":25.50,"lastDonationAtUtc":"2026-11-20T12:00:00Z"}
                        """,
                        Encoding.UTF8,
                        "application/json"
                    )
                };
            }
        );
        using var client = new HttpClient(handler);

        var result = await DonationService.VerifyAsync(
            "user@example.com",
            TestApiUrl,
            TestApiKey,
            client,
            CancellationToken.None
        );

        Assert.Equal(DonationVerificationStatus.Donated, result.Status);
        Assert.Equal(25.50m, result.TotalAmount);
        Assert.Equal(
            new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc),
            result.LastDonationAtUtc
        );
        Assert.NotNull(capturedRequest);
        Assert.Equal(
            TestApiKey,
            Assert.Single(capturedRequest.Headers.GetValues("X-API-KEY"))
        );
        Assert.Contains(
            "\"applicationSlug\":\"chd-studio\"",
            capturedBody,
            StringComparison.Ordinal
        );
        Assert.Contains("\"email\":\"user@example.com\"", capturedBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsyncReturnsNotDonatedForSuccessfulEmptyResult()
    {
        var handler = new FakeHttpMessageHandler(
            HttpStatusCode.OK,
            """{"donated":false,"applicationSlug":"chd-studio","totalAmount":0}"""
        );
        using var client = new HttpClient(handler);

        var result = await DonationService.VerifyAsync(
            "user@example.com",
            TestApiUrl,
            TestApiKey,
            client,
            CancellationToken.None
        );

        Assert.Equal(DonationVerificationStatus.NotDonated, result.Status);
        Assert.Equal(0m, result.TotalAmount);
    }

    [Fact]
    public async Task VerifyAsyncReturnsUnavailableForUnauthorized()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.Unauthorized, "");
        using var client = new HttpClient(handler);

        var result = await DonationService.VerifyAsync(
            "user@example.com",
            TestApiUrl,
            TestApiKey,
            client,
            CancellationToken.None
        );

        Assert.Equal(DonationVerificationStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task VerifyAsyncReturnsUnavailableWhenTheNetworkFails()
    {
        var handler = FakeHttpMessageHandler.WithAsyncHandler(
            _ => throw new HttpRequestException("offline")
        );
        using var client = new HttpClient(handler);

        var result = await DonationService.VerifyAsync(
            "user@example.com",
            TestApiUrl,
            TestApiKey,
            client,
            CancellationToken.None
        );

        Assert.Equal(DonationVerificationStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task VerifyAsyncSkipsTheRequestForAnEmptyEmail()
    {
        var requestCount = 0;
        var handler = FakeHttpMessageHandler.WithAsyncHandler(
            _ =>
            {
                requestCount++;
                throw new InvalidOperationException("The request should not have been sent.");
            }
        );
        using var client = new HttpClient(handler);

        var result = await DonationService.VerifyAsync(
            "   ",
            TestApiUrl,
            TestApiKey,
            client,
            CancellationToken.None
        );

        Assert.Equal(DonationVerificationStatus.NotDonated, result.Status);
        Assert.Equal(0, requestCount);
    }
}
