using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Mgx.Engine.Http;
using Mgx.Engine.Models;

namespace Mgx.IntegrationTests;

/// <summary>
/// The two-hop content path. The security-critical assertions live here: hop 2 carries no
/// Authorization header, every redirect is re-validated against the allowlist, an
/// auto-redirecting transport is refused, and the -Debug trace never leaks a pre-auth URL's
/// query string. Hop 1 uses a MockHttpHandler-backed ResilientGraphClient; hop 2 is mocked
/// through GraphContentClient.DownloadClientForTests.
/// </summary>
[Collection("Pipeline")]
public class GraphContentClientTests : IDisposable
{
    private const string GraphContentUrl = "https://graph.microsoft.com/v1.0/me/drive/items/01ABC/content";
    private const string CdnUrl = "https://contoso-my.sharepoint.com/_layouts/15/download.aspx?UniqueId=abc&tempauth=SECRETTOKEN";

    private readonly MockHttpHandler _graphHandler = new();
    private readonly MockHttpHandler _cdnHandler = new();
    private readonly HttpClient _graphHttpClient;
    private readonly ResilientGraphClient _client;

    public GraphContentClientTests()
    {
        ResiliencePipelineFactory.Reset();
        _graphHttpClient = new HttpClient(_graphHandler);
        _client = new ResilientGraphClient(_graphHttpClient,
            new ResilientGraphClientOptions { NoRateLimit = true, MaxRetryAttempts = 1 });
        GraphContentClient.DownloadClientForTests = new HttpClient(_cdnHandler);
    }

    public void Dispose()
    {
        GraphContentClient.DownloadClientForTests?.Dispose();
        GraphContentClient.DownloadClientForTests = null;
        _client.Dispose();
        _graphHttpClient.Dispose();
    }

    private void QueueRedirectToCdn(string location = CdnUrl) =>
        _graphHandler.QueueResponse(HttpStatusCode.Found, null, new() { ["Location"] = location });

    private static async Task<string> ReadAllAsync(GraphContentResult result)
    {
        using var ms = new MemoryStream();
        await GraphContentClient.CopyWithIdleTimeoutAsync(
            result.Content, ms, null, TimeSpan.FromSeconds(5), CancellationToken.None);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // --- the token-leak regression the whole design exists to prevent ---

    [Fact]
    public async Task Hop2_request_carries_no_Authorization_header_and_the_range()
    {
        QueueRedirectToCdn();
        _cdnHandler.QueueResponse(HttpStatusCode.PartialContent, "12345");

        using var result = await _client.GetContentAsync(
            GraphContentUrl, new RangeHeaderValue(0, 4));

        Assert.Equal("12345", await ReadAllAsync(result));
        Assert.True(result.FromDownloadHost);

        var cdnRequest = _cdnHandler.Requests.Single();
        Assert.Null(cdnRequest.Headers.Authorization);
        Assert.False(cdnRequest.Headers.Contains("Authorization"),
            "the bearer must never reach the download host");
        Assert.Equal("bytes=0-4", cdnRequest.Headers.Range!.ToString());
    }

    [Fact]
    public void Debug_trace_never_contains_the_preauth_query_string()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Content = new StringContent("");
        response.Headers.TryAddWithoutValidation("Location", CdnUrl);

        var trace = GraphRequestTracer.FormatResponse(response, 12, null);

        Assert.DoesNotContain("SECRETTOKEN", trace);
        Assert.DoesNotContain("tempauth", trace);
        // The diagnostic value - host and path - survives.
        Assert.Contains("contoso-my.sharepoint.com/_layouts/15/download.aspx?<redacted>", trace);
    }

    // --- allowlist enforcement, both hops ---

    [Fact]
    public async Task Hop1_redirect_to_a_disallowed_host_is_refused()
    {
        QueueRedirectToCdn("https://mystore.blob.core.windows.net/exfil/file?sig=x");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _client.GetContentAsync(GraphContentUrl));

        Assert.Contains("not on the allowed list", ex.Message);
        Assert.Empty(_cdnHandler.Requests); // never fetched
    }

    [Fact]
    public async Task Hop2_redirect_to_a_disallowed_host_is_refused()
    {
        // An open redirect on an allowlisted host must not bypass the validator: every
        // Location is re-validated, not just the first.
        QueueRedirectToCdn();
        _cdnHandler.QueueResponse(HttpStatusCode.Found, null,
            new() { ["Location"] = "https://evil.example/steal" });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _client.GetContentAsync(GraphContentUrl));

        Assert.Contains("not on the allowed list", ex.Message);
    }

    [Fact]
    public async Task Hop2_validated_redirect_is_followed()
    {
        QueueRedirectToCdn();
        _cdnHandler.QueueResponse(HttpStatusCode.Found, null,
            new() { ["Location"] = "https://southindia1-mediap.svc.ms/transform?x=1" });
        _cdnHandler.QueueResponse(HttpStatusCode.OK, "bytes-from-cdn");

        using var result = await _client.GetContentAsync(GraphContentUrl);

        Assert.Equal("bytes-from-cdn", await ReadAllAsync(result));
        Assert.Equal(2, _cdnHandler.Requests.Count);
    }

    // --- transport precondition ---

    private sealed class AutoRedirectingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Simulates an SDK-style transport that already followed the 302: the response
            // is a 200 whose RequestMessage points at the download host.
            var followed = new HttpRequestMessage(HttpMethod.Get, CdnUrl);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("cdn bytes via auto-redirect"),
                RequestMessage = followed
            });
        }
    }

    [Fact]
    public async Task A_transport_that_auto_followed_the_redirect_is_refused()
    {
        using var autoClient = new HttpClient(new AutoRedirectingHandler());
        using var client = new ResilientGraphClient(autoClient,
            new ResilientGraphClientOptions { NoRateLimit = true, MaxRetryAttempts = 1 });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetContentAsync(GraphContentUrl));

        Assert.Contains("followed a redirect", ex.Message);
    }

    // --- content flows ---

    [Fact]
    public async Task Direct_200_from_graph_is_returned_without_hop2()
    {
        // Attachments and photos serve bytes straight from Graph - no redirect involved.
        _graphHandler.QueueResponse(HttpStatusCode.OK, "attachment-bytes");

        using var result = await _client.GetContentAsync(
            "https://graph.microsoft.com/v1.0/me/messages/1/attachments/2/$value");

        Assert.Equal("attachment-bytes", await ReadAllAsync(result));
        Assert.False(result.FromDownloadHost);
        Assert.Empty(_cdnHandler.Requests);
    }

    [Fact]
    public async Task Expired_preauth_url_gets_one_fresh_hop1_then_succeeds()
    {
        QueueRedirectToCdn();
        _cdnHandler.QueueResponse(HttpStatusCode.Forbidden);          // expired pre-auth URL
        QueueRedirectToCdn();                                         // fresh hop 1
        _cdnHandler.QueueResponse(HttpStatusCode.PartialContent, "x"); // fresh URL works

        using var result = await _client.GetContentAsync(
            GraphContentUrl, new RangeHeaderValue(0, 0));

        Assert.Equal("x", await ReadAllAsync(result));
        Assert.Equal(2, _graphHandler.RequestCount);
        Assert.Equal(2, _cdnHandler.Requests.Count);
    }

    [Fact]
    public async Task Persistent_403_after_refresh_throws()
    {
        QueueRedirectToCdn();
        _cdnHandler.QueueResponse(HttpStatusCode.Forbidden);
        QueueRedirectToCdn();
        _cdnHandler.QueueResponse(HttpStatusCode.Forbidden);

        var ex = await Assert.ThrowsAsync<GraphServiceException>(
            () => _client.GetContentAsync(GraphContentUrl));

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
    }

    [Fact]
    public async Task Hop2_retries_transient_503()
    {
        QueueRedirectToCdn();
        _cdnHandler.QueueResponse(HttpStatusCode.ServiceUnavailable);
        _cdnHandler.QueueResponse(HttpStatusCode.OK, "after-retry");

        using var result = await _client.GetContentAsync(GraphContentUrl);

        Assert.Equal("after-retry", await ReadAllAsync(result));
        Assert.Equal(2, _cdnHandler.Requests.Count);
    }

    [Fact]
    public async Task Graph_error_surfaces_as_GraphServiceException()
    {
        _graphHandler.QueueResponse(HttpStatusCode.NotFound,
            """{"error":{"code":"itemNotFound","message":"gone"}}""");

        var ex = await Assert.ThrowsAsync<GraphServiceException>(
            () => _client.GetContentAsync(GraphContentUrl));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }

    // --- the copy helper: truncation and idle timeout ---

    [Fact]
    public async Task Copy_truncates_at_maxBytes_when_the_server_ignored_range()
    {
        using var source = new MemoryStream(Encoding.UTF8.GetBytes("full body that the server sent anyway"));
        using var destination = new MemoryStream();

        var copied = await GraphContentClient.CopyWithIdleTimeoutAsync(
            source, destination, maxBytes: 9, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(9, copied);
        Assert.Equal("full body", Encoding.UTF8.GetString(destination.ToArray()));
    }

    private sealed class StalledStream : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Copy_aborts_a_stalled_stream_within_the_idle_timeout()
    {
        using var destination = new MemoryStream();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => GraphContentClient.CopyWithIdleTimeoutAsync(
                new StalledStream(), destination, null,
                TimeSpan.FromMilliseconds(200), CancellationToken.None));

        Assert.Contains("stalled", ex.Message);
    }

    [Fact]
    public async Task Content_bytes_reach_telemetry()
    {
        _graphHandler.QueueResponse(HttpStatusCode.OK, "12345678");
        MgxTelemetryCollector.Current.Reset();

        using var result = await _client.GetContentAsync(
            "https://graph.microsoft.com/v1.0/me/photo/$value");
        await ReadAllAsync(result);

        Assert.Equal(8, MgxTelemetryCollector.Current.GetSummary().ContentBytesDownloaded);
    }

    [Fact]
    public async Task Redirect_on_the_content_path_is_not_counted_as_a_failed_request()
    {
        // Graph answers /content with a 302 to a pre-authenticated download host. That is the
        // documented success path, not an error - but IsSuccessStatusCode is 2xx-only, so
        // classifying on it alone made every successful two-hop download register as a
        // failure. Observed live: five successful downloads reported as Succeeded=1 Failed=5.
        QueueRedirectToCdn();
        _cdnHandler.QueueResponse(HttpStatusCode.OK, "payload!");
        MgxTelemetryCollector.Current.Reset();

        using var result = await _client.GetContentAsync(GraphContentUrl);
        await ReadAllAsync(result);

        var summary = MgxTelemetryCollector.Current.GetSummary();
        Assert.Equal(0, summary.Failed);
        Assert.True(summary.Succeeded >= 1, $"expected a success, got {summary.Succeeded}");
    }
}