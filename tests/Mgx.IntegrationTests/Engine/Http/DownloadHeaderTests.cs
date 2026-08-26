using System.Net;
using Mgx.Engine.Http;

namespace Mgx.IntegrationTests;

/// <summary>
/// Conditional headers cross to the download host; auth and correlation headers never do.
/// Before this, hop 2 carried only Range, so a caller's If-None-Match silently vanished
/// and a conditional download re-fetched the full content every time.
/// </summary>
[Collection("Pipeline")]
public class DownloadHeaderTests : IDisposable
{
    private const string CdnUrl = "https://contoso-my.sharepoint.com/_layouts/15/download.aspx?UniqueId=abc&tempauth=TOKEN";

    private readonly MockHttpHandler _cdnHandler = new();

    public DownloadHeaderTests()
    {
        GraphContentClient.DownloadClientForTests = new HttpClient(_cdnHandler);
    }

    public void Dispose()
    {
        GraphContentClient.DownloadClientForTests?.Dispose();
        GraphContentClient.DownloadClientForTests = null;
    }

    [Fact]
    public async Task Conditional_headers_reach_the_download_host()
    {
        _cdnHandler.QueueBytes(HttpStatusCode.NotModified, [], "application/octet-stream");

        var headers = new Dictionary<string, string>
        {
            ["If-None-Match"] = "\"etag-1\"",
            ["If-Modified-Since"] = "Mon, 24 Aug 2026 10:00:00 GMT",
            ["If-Match"] = "\"graph-etag\"",       // write validator: stays on hop 1
            ["Authorization"] = "Bearer should-never-cross",
            ["client-request-id"] = "should-never-cross",
            ["ConsistencyLevel"] = "eventual",
        };

        // 304 is a non-success status; the client surfaces it as an error after reading the
        // body - what matters here is what went on the wire.
        try
        {
            using var result = await GraphContentClient.GetFromDownloadUrlAsync(
                CdnUrl, range: null, TimeSpan.FromSeconds(5), CancellationToken.None, headers);
        }
        catch (Mgx.Engine.Models.GraphServiceException) { /* 304 surfaces as an error; fine */ }

        var sent = Assert.Single(_cdnHandler.CapturedRequests);
        Assert.Equal("\"etag-1\"", Assert.Single(Assert.Contains("If-None-Match", sent.Headers)));
        Assert.Contains("If-Modified-Since", sent.Headers);
        Assert.DoesNotContain("Authorization", sent.Headers);
        Assert.DoesNotContain("client-request-id", sent.Headers);
        Assert.DoesNotContain("ConsistencyLevel", sent.Headers);
        Assert.DoesNotContain("If-Match", sent.Headers);
    }

    [Fact]
    public async Task Lowercase_conditional_headers_forward_on_the_two_hop_path()
    {
        // The two-hop path copies the caller dictionary; the copy must keep case
        // insensitivity or hop 2 silently drops non-canonical casings.
        ResiliencePipelineFactory.Reset();
        var graphWire = new MockHttpHandler();
        graphWire.QueueResponse(HttpStatusCode.Found, headers: new Dictionary<string, string>
        {
            ["Location"] = CdnUrl,
        });
        _cdnHandler.QueueBytes(HttpStatusCode.OK, [1, 2, 3], "application/octet-stream");

        using var client = new ResilientGraphClient(new HttpClient(graphWire),
            new ResilientGraphClientOptions { NoRateLimit = true, MaxRetryAttempts = 1 });
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["if-none-match"] = "\"lower-etag\"",
            ["client-request-id"] = "caller-id",
        };
        using var result = await client.GetContentAsync(
            "https://graph.microsoft.com/v1.0/me/drive/items/x/content",
            range: null, headers, CancellationToken.None);

        var hop2 = Assert.Single(_cdnHandler.CapturedRequests);
        Assert.Equal("\"lower-etag\"", Assert.Single(Assert.Contains("If-None-Match",
            new Dictionary<string, string[]>(hop2.Headers, StringComparer.OrdinalIgnoreCase))));

        // And hop 1 must carry the caller's correlation id exactly once.
        var hop1 = Assert.Single(graphWire.CapturedRequests);
        var ids = Assert.Contains("client-request-id",
            new Dictionary<string, string[]>(hop1.Headers, StringComparer.OrdinalIgnoreCase));
        Assert.Equal("caller-id", Assert.Single(ids));
        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public async Task A_download_without_caller_headers_sends_none()
    {
        _cdnHandler.QueueBytes(HttpStatusCode.OK, [1, 2, 3], "application/octet-stream");

        using var result = await GraphContentClient.GetFromDownloadUrlAsync(
            CdnUrl, range: null, TimeSpan.FromSeconds(5), CancellationToken.None);

        var sent = Assert.Single(_cdnHandler.CapturedRequests);
        Assert.DoesNotContain("If-None-Match", sent.Headers);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
    }
}
