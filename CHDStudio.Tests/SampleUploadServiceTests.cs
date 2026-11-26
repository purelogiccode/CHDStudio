using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CHDStudio.Models;
using CHDStudio.Services;

namespace CHDStudio.Tests;

/// <summary>Tests for the diagnostic sample upload client.</summary>
public sealed class SampleUploadServiceTests : IDisposable
{
    private const string ApiBaseUrl = "https://example.com/api/samples";
    private readonly string _directory = Path.Join(
        Path.GetTempPath(),
        $"CHDStudio_SampleUpload_{Guid.NewGuid():N}"
    );

    /// <summary>Removes the temporary directory used by the test.</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup only.
        }
    }

    [Fact]
    public async Task UploadAsync_SendsCreatePartsPutAndComplete()
    {
        var bytes = Enumerable.Range(0, 1234).Select(static value => (byte)value).ToArray();
        var filePath = Path.Join(_directory, "broken cue.bin");
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(filePath, bytes);

        var id = Guid.NewGuid();
        var requests = new List<(string Method, string Path)>();
        string? createBody = null;
        string? partsBody = null;
        string? completeBody = null;
        var handler = FakeHttpMessageHandler.WithAsyncHandler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            requests.Add((request.Method.Method, path));

            if (
                string.Equals(request.Method.Method, "POST", StringComparison.Ordinal)
                && string.Equals(path, "/api/samples", StringComparison.Ordinal)
            )
            {
                createBody = await request.Content!.ReadAsStringAsync();
                return Json(
                    HttpStatusCode.OK,
                    $$"""{"id":"{{id}}","partSizeBytes":5242880,"partCount":1}"""
                );
            }

            if (
                string.Equals(request.Method.Method, "POST", StringComparison.Ordinal)
                && path.EndsWith("/parts", StringComparison.Ordinal)
            )
            {
                partsBody = await request.Content!.ReadAsStringAsync();
                return Json(
                    HttpStatusCode.OK,
                    """{"parts":[{"partNumber":1,"url":"https://storage.example/part1"}]}"""
                );
            }

            if (string.Equals(request.Method.Method, "PUT", StringComparison.Ordinal))
            {
                Assert.Equal(bytes, await request.Content!.ReadAsByteArrayAsync());
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.ETag = new EntityTagHeaderValue("\"etag-1\"");
                return response;
            }

            if (
                string.Equals(request.Method.Method, "POST", StringComparison.Ordinal)
                && path.EndsWith("/complete", StringComparison.Ordinal)
            )
            {
                completeBody = await request.Content!.ReadAsStringAsync();
                return Json(HttpStatusCode.OK, """{"success":true}""");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var client = new HttpClient(handler);
        var progress = new RecordingProgress();

        var result = await SampleUploadService.UploadAsync(
            filePath,
            "Invalid track",
            "A note",
            progress,
            CancellationToken.None,
            ApiBaseUrl,
            client
        );

        Assert.Equal(id, result.Id);
        Assert.Equal("broken cue.bin", result.FileName);
        Assert.Equal(
            [
                ("POST", "/api/samples"),
                ("POST", $"/api/samples/{id}/parts"),
                ("PUT", "/part1"),
                ("POST", $"/api/samples/{id}/complete")
            ],
            requests
        );
        Assert.Contains("\"application\":\"CHDStudio\"", createBody, StringComparison.Ordinal);
        Assert.Contains("\"reason\":\"Invalid track\"", createBody, StringComparison.Ordinal);
        Assert.Contains("\"note\":\"A note\"", createBody, StringComparison.Ordinal);
        Assert.Contains("\"consent\":true", createBody, StringComparison.Ordinal);
        Assert.DoesNotContain("sha256", createBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[1]", partsBody, StringComparison.Ordinal);
        using var completeDocument = JsonDocument.Parse(completeBody!);
        var completedPart = completeDocument.RootElement.GetProperty("parts")[0];
        Assert.Equal(1, completedPart.GetProperty("partNumber").GetInt32());
        Assert.Equal("\"etag-1\"", completedPart.GetProperty("eTag").GetString());
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(bytes)),
            completeDocument.RootElement.GetProperty("sha256").GetString()
        );
        var report = Assert.Single(progress.Values);
        Assert.Equal(bytes.Length, report.BytesUploaded);
        Assert.Equal(bytes.Length, report.TotalBytes);
    }

    [Fact]
    public async Task UploadAsync_ReportsTheServerErrorFromCreate()
    {
        var filePath = Path.Join(_directory, "sample.bin");
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(filePath, [1, 2, 3]);
        var handler = new FakeHttpMessageHandler(
            HttpStatusCode.TooManyRequests,
            """{"error":"Too many samples from this connection."}"""
        );
        using var client = new HttpClient(handler);

        var exception = await Assert.ThrowsAsync<SampleUploadException>(
            () =>
                SampleUploadService.UploadAsync(
                    filePath,
                    "Reason",
                    null,
                    null,
                    CancellationToken.None,
                    ApiBaseUrl,
                    client
                )
        );

        Assert.Contains("Too many samples", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadAsync_AbortsWhenAPartUploadFails()
    {
        var filePath = Path.Join(_directory, "sample.bin");
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(filePath, [1, 2, 3]);
        var id = Guid.NewGuid();
        var aborted = false;
        var handler = FakeHttpMessageHandler.WithAsyncHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (
                string.Equals(request.Method.Method, "POST", StringComparison.Ordinal)
                && string.Equals(path, "/api/samples", StringComparison.Ordinal)
            )
            {
                return Task.FromResult(
                    Json(
                        HttpStatusCode.OK,
                        $$"""{"id":"{{id}}","partSizeBytes":5242880,"partCount":1}"""
                    )
                );
            }

            if (path.EndsWith("/parts", StringComparison.Ordinal))
            {
                return Task.FromResult(
                    Json(
                        HttpStatusCode.OK,
                        """{"parts":[{"partNumber":1,"url":"https://storage.example/part1"}]}"""
                    )
                );
            }

            if (string.Equals(request.Method.Method, "PUT", StringComparison.Ordinal))
            {
                return Task.FromResult(
                    Json(HttpStatusCode.Forbidden, "<Error><Code>AccessDenied</Code></Error>")
                );
            }

            if (path.EndsWith("/abort", StringComparison.Ordinal))
            {
                aborted = true;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        using var client = new HttpClient(handler);

        var exception = await Assert.ThrowsAsync<SampleUploadException>(
            () =>
                SampleUploadService.UploadAsync(
                    filePath,
                    "Reason",
                    null,
                    null,
                    CancellationToken.None,
                    ApiBaseUrl,
                    client
                )
        );

        Assert.Contains("upload part 1", exception.Message, StringComparison.Ordinal);
        Assert.True(aborted, "The pending upload should be aborted");
    }

    [Fact]
    public async Task UploadAsync_RejectsUnsupportedServerPartSize()
    {
        var filePath = Path.Join(_directory, "sample.bin");
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(filePath, [1, 2, 3]);
        var handler = new FakeHttpMessageHandler(
            HttpStatusCode.OK,
            $$"""{"id":"{{Guid.NewGuid()}}","partSizeBytes":1048576,"partCount":1}"""
        );
        using var client = new HttpClient(handler);

        var exception = await Assert.ThrowsAsync<SampleUploadException>(
            () =>
                SampleUploadService.UploadAsync(
                    filePath,
                    "Reason",
                    null,
                    null,
                    CancellationToken.None,
                    ApiBaseUrl,
                    client
                )
        );

        Assert.Contains("unsupported part size", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadAsync_RejectsAnEmptyFile()
    {
        var filePath = Path.Join(_directory, "empty.bin");
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(filePath, []);

        var exception = await Assert.ThrowsAsync<SampleUploadException>(
            () =>
                SampleUploadService.UploadAsync(
                    filePath,
                    "Reason",
                    null,
                    null,
                    CancellationToken.None,
                    ApiBaseUrl
                )
        );

        Assert.Contains("empty", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Builds a JSON response with the supplied status code.</summary>
    /// <param name="status">HTTP status code.</param>
    /// <param name="json">Response body.</param>
    /// <returns>The response message.</returns>
    private static HttpResponseMessage Json(HttpStatusCode status, string json)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    /// <summary>Records progress synchronously so assertions do not race the callback queue.</summary>
    private sealed class RecordingProgress : IProgress<SampleUploadProgress>
    {
        /// <summary>Gets the reported progress values.</summary>
        public List<SampleUploadProgress> Values { get; } = [];

        /// <inheritdoc />
        public void Report(SampleUploadProgress value)
        {
            Values.Add(value);
        }
    }
}
