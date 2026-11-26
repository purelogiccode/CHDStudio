using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using CHDStudio.Models;
using Serilog;

namespace CHDStudio.Services;

/// <summary>Raised when a diagnostic sample upload cannot be completed.</summary>
internal sealed class SampleUploadException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="SampleUploadException" /> class.</summary>
    public SampleUploadException() { }

    /// <summary>Initializes a new instance of the <see cref="SampleUploadException" /> class.</summary>
    /// <param name="message">User-facing failure reason.</param>
    public SampleUploadException(string message)
        : base(message) { }

    /// <summary>Initializes a new instance of the <see cref="SampleUploadException" /> class.</summary>
    /// <param name="message">User-facing failure reason.</param>
    /// <param name="innerException">Underlying failure.</param>
    public SampleUploadException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>
///     Uploads a failing input to the website's diagnostic sample service. The website issues
///     short-lived multipart URLs; the file itself goes directly to private Cloudflare R2 storage
///     and never passes through the website or this repository.
/// </summary>
internal static class SampleUploadService
{
    /// <summary>Maximum number of part URLs requested at once.</summary>
    private const int PartsPerUrlRequest = 10;

    private static readonly HttpClient Client = new(new SocketsHttpHandler())
    {
        // Large files over a home connection can take much longer than the default 100 seconds.
        Timeout = Timeout.InfiniteTimeSpan
    };

    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web
    );

    /// <summary>
    ///     Uploads <paramref name="filePath" /> and its failure reason.
    /// </summary>
    /// <param name="filePath">File to upload.</param>
    /// <param name="reason">Conversion failure to record with the sample.</param>
    /// <param name="note">Optional user note.</param>
    /// <param name="progress">Optional byte-progress callback.</param>
    /// <param name="token">Cancellation token.</param>
    /// <param name="apiBaseUrl">Optional API base URL override (used by tests).</param>
    /// <returns>The file name and the server-side sample id.</returns>
    internal static async Task<(Guid Id, string FileName)> UploadAsync(
        string filePath,
        string reason,
        string? note,
        IProgress<SampleUploadProgress>? progress,
        CancellationToken token,
        string? apiBaseUrl = null
    )
    {
        var baseUrl = apiBaseUrl ?? AppConfig.SampleUploadApiUrl;
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists)
            throw new SampleUploadException($"The file no longer exists: {filePath}");

        if (fileInfo.Length <= 0)
            throw new SampleUploadException("The file is empty, so there is nothing to submit.");

        var fileName = Path.GetFileName(filePath);
        var createRequest = new
        {
            application = AppConfig.ApplicationName,
            version = typeof(AppConfig).Assembly.GetName().Version?.ToString()
                      ?? "unknown",
            fileName,
            sizeBytes = fileInfo.Length,
            reason,
            note,
            consent = true
        };

        SampleUploadTicket ticket;
        using var createMessage = CreateApiRequest(HttpMethod.Post, baseUrl, createRequest);
        using (var createResponse = await Client
                   .SendAsync(createMessage, token)
                   .ConfigureAwait(false))
        {
            if (!createResponse.IsSuccessStatusCode)
                throw new SampleUploadException(
                    await DescribeFailureAsync(createResponse, "start the upload", token)
                        .ConfigureAwait(false)
                );

            ticket =
                await createResponse
                    .Content.ReadFromJsonAsync<SampleUploadTicket>(JsonOptions, token)
                    .ConfigureAwait(false)
                ?? throw new SampleUploadException("The sample service returned an empty upload ticket.");
        }

        if (ticket.PartSizeBytes is < 5 * 1024 * 1024 or > int.MaxValue)
            throw new SampleUploadException("The sample service returned an unsupported part size.");

        var partSize = (int)ticket.PartSizeBytes;
        var partCount = ticket.PartCount;
        if (partCount <= 0 || partCount > 10_000)
            throw new SampleUploadException("The sample service returned an unsupported part count.");

        var parts = new List<PartETagDto>(partCount);
        var buffer = ArrayPool<byte>.Shared.Rent(partSize);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        try
        {
            using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.SequentialScan
            );

            long uploaded = 0;
            for (var firstPart = 1; firstPart <= partCount; firstPart += PartsPerUrlRequest)
            {
                token.ThrowIfCancellationRequested();

                var lastPart = Math.Min(firstPart + PartsPerUrlRequest - 1, partCount);
                var partNumbers = Enumerable.Range(firstPart, lastPart - firstPart + 1).ToArray();
                var urls = await RequestPartUrlsAsync(
                        baseUrl,
                        ticket.Id,
                        partNumbers,
                        token
                    )
                    .ConfigureAwait(false);

                foreach (var part in urls)
                {
                    token.ThrowIfCancellationRequested();

                    var remaining = fileInfo.Length - uploaded;
                    if (remaining <= 0)
                        throw new SampleUploadException(
                            "The file changed while it was being uploaded. Try again without modifying it."
                        );

                    var length = (int)Math.Min(partSize, remaining);
                    var read = await stream
                        .ReadAsync(buffer.AsMemory(0, length), token)
                        .ConfigureAwait(false);
                    if (read != length)
                        throw new SampleUploadException(
                            "The file changed while it was being uploaded. Try again without modifying it."
                        );

                    sha256.AppendData(buffer, 0, read);

                    using var partContent = new ByteArrayContent(buffer, 0, read);
                    partContent.Headers.ContentType =
                        new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

                    using var putResponse = await Client
                        .PutAsync(part.Url, partContent, token)
                        .ConfigureAwait(false);

                    if (!putResponse.IsSuccessStatusCode)
                        throw new SampleUploadException(
                            await DescribeFailureAsync(
                                    putResponse,
                                    $"upload part {part.PartNumber}",
                                    token
                                )
                                .ConfigureAwait(false)
                        );

                    var etag = putResponse.Headers.ETag?.Tag;
                    if (string.IsNullOrWhiteSpace(etag))
                        throw new SampleUploadException(
                            "The storage service did not return an ETag for an uploaded part."
                        );

                    parts.Add(new PartETagDto(part.PartNumber, etag));
                    uploaded += read;
                    progress?.Report(new SampleUploadProgress(uploaded, fileInfo.Length));
                }
            }

            var hash = Convert.ToHexString(sha256.GetHashAndReset());
            var completeRequest = new
            {
                parts = parts.Select(static part => new
                {
                    partNumber = part.PartNumber,
                    eTag = part.ETag
                }),
                sha256 = hash
            };
            using var completeMessage = CreateApiRequest(
                HttpMethod.Post,
                $"{baseUrl}/{ticket.Id}/complete",
                completeRequest
            );
            using var completeResponse = await Client
                .SendAsync(completeMessage, token)
                .ConfigureAwait(false);

            if (!completeResponse.IsSuccessStatusCode)
                throw new SampleUploadException(
                    await DescribeFailureAsync(completeResponse, "finish the upload", token)
                        .ConfigureAwait(false)
                );

            return (ticket.Id, fileName);
        }
        catch
        {
            await TryAbortAsync(baseUrl, ticket.Id).ConfigureAwait(false);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Requests presigned URLs for a batch of parts.</summary>
    private static async Task<IReadOnlyList<SamplePartUrl>> RequestPartUrlsAsync(
        string baseUrl,
        Guid id,
        IReadOnlyList<int> partNumbers,
        CancellationToken token
    )
    {
        using var request = CreateApiRequest(
            HttpMethod.Post,
            $"{baseUrl}/{id}/parts",
            new { partNumbers }
        );
        using var response = await Client
            .SendAsync(request, token)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new SampleUploadException(
                await DescribeFailureAsync(response, "prepare the upload", token)
                    .ConfigureAwait(false)
            );

        var payload = await response
            .Content.ReadFromJsonAsync<PartUrlResponse>(JsonOptions, token)
            .ConfigureAwait(false);

        return payload?.Parts
               ?? throw new SampleUploadException("The sample service returned no upload URLs.");
    }

    /// <summary>Best-effort cancellation of a pending upload.</summary>
    private static async Task TryAbortAsync(string baseUrl, Guid id)
    {
        try
        {
            using var request = CreateApiRequest(
                HttpMethod.Post,
                $"{baseUrl}/{id}/abort",
                body: null
            );
            using var response = await Client
                .SendAsync(request, CancellationToken.None)
                .ConfigureAwait(false);
            _ = response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The upload will expire on the server; the user already receives the real error.
            Log.Debug(ex, "Could not abort the diagnostic sample upload.");
        }
    }

    /// <summary>Builds a website API request carrying the shared application key.</summary>
    /// <param name="method">HTTP method.</param>
    /// <param name="url">Absolute endpoint URL.</param>
    /// <param name="body">Optional JSON body.</param>
    /// <returns>The prepared request. The caller owns and disposes it.</returns>
    private static HttpRequestMessage CreateApiRequest(
        HttpMethod method,
        string url,
        object? body
    )
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
        request.Headers.TryAddWithoutValidation("X-API-KEY", AppConfig.BugReportApiKey);
        return request;
    }

    /// <summary>Builds a user-facing message from an API or storage error response.</summary>
    private static async Task<string> DescribeFailureAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken token
    )
    {
        var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        string? detail = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (
                document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
            )
                detail = error.GetString();
        }
        catch (JsonException)
        {
            // A storage error is XML; fall through to the status line.
        }

        detail ??= body.Length switch
        {
            > 0 and <= 300 => body,
            _ => null
        };

        var status =
            $"{(int)response.StatusCode} {response.ReasonPhrase}".Trim();
        return string.IsNullOrWhiteSpace(detail)
            ? $"Could not {operation}: {status}."
            : $"Could not {operation}: {detail}";
    }

    /// <summary>Upload ticket returned by the website.</summary>
    private sealed record SampleUploadTicket(Guid Id, long PartSizeBytes, int PartCount);

    /// <summary>Part URL response returned by the website.</summary>
    private sealed record PartUrlResponse(IReadOnlyList<SamplePartUrl> Parts);

    /// <summary>One presigned part URL.</summary>
    private sealed record SamplePartUrl(int PartNumber, string Url);

    /// <summary>One completed part: its number and the ETag returned by storage.</summary>
    private sealed record PartETagDto(int PartNumber, string ETag);
}

