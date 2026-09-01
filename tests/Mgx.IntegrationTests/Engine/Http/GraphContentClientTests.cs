using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Mgx.Cmdlets.Base;
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
        // Only scheme and host survive. The path goes too, deliberately: not every download
        // host puts the capability in the query - some carry it in a path segment - and the
        // tracer cannot tell those apart. The host answers the question -Debug is actually
        // asked here ("where did this redirect go?"); the path adds little and can leak.
        Assert.Contains("https://contoso-my.sharepoint.com/<redacted>", trace);
        Assert.DoesNotContain("_layouts", trace);
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

    // --- the same precondition, one layer up: Get-MgxContent before it fetches anything ---

    private const string TestTenantId = "test-tenant-00000000-0000-0000-0000-000000000000";

    /// <summary>
    /// Points MgxCmdletBase's transport at this class's Graph mock and declares whether mgx owns
    /// it (false = the Graph SDK's client, which ships a RedirectHandler). The mock client is
    /// this class's own, so the scope borrows it rather than disposing it at the end of a test.
    /// </summary>
    private MgxTransportScope InjectCmdletTransport(bool owned, HttpClient? client = null) =>
        MgxTransportScope.Inject(client ?? _graphHttpClient, owned);

    private static PowerShell CreateContentShell()
    {
        var ps = PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Content.GetMgxContent).Assembly);
        ps.Invoke();
        ps.Commands.Clear();
        // Stands in for a Graph connection: GetClient() only needs a context with a TenantId.
        ps.AddScript($"function Get-MgContext {{ [PSCustomObject]@{{ TenantId = '{TestTenantId}' }} }}");
        ps.Invoke();
        ps.Commands.Clear();
        return ps;
    }

    private static IEnumerable<string> ErrorIds(PowerShell ps, Exception? thrown)
    {
        foreach (var record in ps.Streams.Error)
            yield return record.FullyQualifiedErrorId;
        if (thrown is IContainsErrorRecord containsRecord)
            yield return containsRecord.ErrorRecord.FullyQualifiedErrorId;
    }

    [Fact]
    public void GetMgxContent_refuses_to_fetch_over_a_transport_mgx_does_not_own()
    {
        const string contentUri = "/me/drive/items/01ABC/content";
        // Bait, served to anyone who asks. Getting these bytes back means an authenticated
        // request went out over a transport that can auto-follow a 302 to a host mgx never
        // validated - the exact leak the two-hop design exists to prevent.
        _graphHandler.SetDefaultResponse(HttpStatusCode.OK, "bytes-that-must-not-be-fetched");

        // 1. Borrowed SDK transport: refuse, and refuse before the request is sent.
        using (InjectCmdletTransport(owned: false))
        using (var ps = CreateContentShell())
        {
            ps.AddCommand("Get-MgxContent").AddParameter("Uri", contentUri);
            var thrown = Record.Exception(() => ps.Invoke());

            Assert.Contains(ErrorIds(ps, thrown),
                id => id.Contains("ContentRequiresOwnedTransport", StringComparison.Ordinal));
            Assert.Equal(0, _graphHandler.RequestCount);
        }

        // 2. The identical call on the mgx-owned transport has to go through, or step 1
        //    proves nothing: a cmdlet that always refuses would pass it too.
        using (InjectCmdletTransport(owned: true))
        using (var ps = CreateContentShell())
        {
            ps.AddCommand("Get-MgxContent").AddParameter("Uri", contentUri);
            var results = ps.Invoke();

            Assert.Empty(ps.Streams.Error);
            var bytes = Assert.IsType<byte[]>(Assert.Single(results).BaseObject);
            Assert.Equal("bytes-that-must-not-be-fetched", Encoding.UTF8.GetString(bytes));
            Assert.Equal(1, _graphHandler.RequestCount);
        }
    }

    // --- pipeline output guard ---

    /// <summary>
    /// A body of <paramref name="actualBytes"/> bytes with an optionally declared
    /// Content-Length. Declared and actual are independent on purpose: Graph declares the
    /// length on a whole-file GET, so an oversized body can be refused before a byte moves,
    /// while a chunked response declares nothing and the guard has to hold during the copy.
    /// </summary>
    private sealed class SizedBodyHandler(long? declaredLength, long actualBytes) : HttpMessageHandler
    {
        private GeneratedBodyStream? _body;

        /// <summary>Bytes the cmdlet actually pulled off the body stream.</summary>
        public long BytesServed => _body?.BytesRead ?? 0;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _body = new GeneratedBodyStream(actualBytes);
            var content = new StreamContent(_body);
            // Non-seekable, so StreamContent computes no length: leaving this unset is the
            // chunked case (ContentLength null), setting it is what Graph does.
            if (declaredLength != null)
                content.Headers.ContentLength = declaredLength;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
                RequestMessage = request
            });
        }
    }

    /// <summary>N zero bytes, generated on read - a large body without a large fixture.</summary>
    private sealed class GeneratedBodyStream(long length) : Stream
    {
        public long BytesRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var remaining = length - BytesRead;
            if (remaining <= 0) return 0;
            var n = (int)Math.Min(buffer.Length, remaining);
            buffer[..n].Clear();
            BytesRead += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
            => ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromResult(Read(buffer.AsSpan(offset, count)));

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Runs Get-MgxContent -Uri over a transport serving one sized body.</summary>
    private (Collection<PSObject> Output, IReadOnlyList<string> ErrorIds, long BytesServed) InvokeContentOverSizedBody(
        long? declaredLength, long actualBytes)
    {
        var handler = new SizedBodyHandler(declaredLength, actualBytes);
        using var http = new HttpClient(handler);
        using (InjectCmdletTransport(owned: true, client: http))
        {
            using var ps = CreateContentShell();
            ps.AddCommand("Get-MgxContent").AddParameter("Uri", "/me/drive/items/01ABC/content");
            Collection<PSObject> output = [];
            var thrown = Record.Exception(() => output = ps.Invoke());
            return (output, [.. ErrorIds(ps, thrown)], handler.BytesServed);
        }
    }

    [Fact]
    public void GetMgxContent_refuses_a_body_bigger_than_the_pipeline_guard()
    {
        const long MiB = 1024 * 1024;

        // 1. Graph declares 128 MB. Over the 100 MB guard, so the cmdlet must refuse without
        //    reading the body at all - the whole point is that these bytes never materialise
        //    as a byte[] in the host's memory.
        var declared = InvokeContentOverSizedBody(declaredLength: 128 * MiB, actualBytes: 128 * MiB);
        Assert.Contains(declared.ErrorIds,
            id => id.Contains("ContentTooLargeForPipeline", StringComparison.Ordinal));
        Assert.Empty(declared.Output);
        Assert.Equal(0, declared.BytesServed);

        // 2. Chunked: no Content-Length to check up front, so the guard has to trip during the
        //    copy. One byte over is the interesting case - it must not be admitted, and the
        //    cmdlet must stop reading rather than buffer the rest of a multi-gigabyte body.
        var chunked = InvokeContentOverSizedBody(declaredLength: null, actualBytes: 100 * MiB + 1);
        Assert.Contains(chunked.ErrorIds,
            id => id.Contains("ContentTooLargeForPipeline", StringComparison.Ordinal));
        Assert.Empty(chunked.Output);
        Assert.InRange(chunked.BytesServed, 100 * MiB + 1, 101 * MiB);

        // 3. Brackets the guard from below, so a cmdlet that simply refuses everything cannot
        //    pass: a body declared just under 100 MB still reaches the pipeline as bytes.
        var admitted = InvokeContentOverSizedBody(declaredLength: 96 * MiB, actualBytes: 4096);
        Assert.Empty(admitted.ErrorIds);
        var bytes = Assert.IsType<byte[]>(Assert.Single(admitted.Output).BaseObject);
        Assert.Equal(4096, bytes.Length);
    }

    // --- byte ranges: what -First / -Offset -Length put on the wire ---

    /// <summary>
    /// An origin that honours Range the way SharePoint and a well-behaved CDN do: it serves
    /// exactly the bytes asked for and answers 206, so nothing downstream trims the body. That
    /// makes the number of bytes the caller receives a direct readout of the range mgx asked
    /// for - the local truncation path only runs on a 200, and would mask the difference.
    /// </summary>
    private sealed class RangeHonouringHandler(byte[] body) : HttpMessageHandler
    {
        /// <summary>The Range header as it went out, or null when none was sent.</summary>
        public string? RangeHeader { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RangeHeader = request.Headers.Range?.ToString();
            var span = request.Headers.Range?.Ranges.Single();

            HttpResponseMessage response;
            if (span?.From is not { } from)
            {
                response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(body)
                };
            }
            else
            {
                // Inclusive on both ends, per RFC 9110: bytes=0-4 is five bytes.
                var to = (int)Math.Min(span.To ?? body.Length - 1, body.Length - 1);
                response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(body[(int)from..(to + 1)])
                };
                response.Content.Headers.TryAddWithoutValidation(
                    "Content-Range", $"bytes {from}-{to}/{body.Length}");
            }

            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    /// <summary>Runs Get-MgxContent -Uri (plus the given range parameters) against that origin.</summary>
    private (byte[] Bytes, string? RangeHeader) InvokeContentOverRangeHonouringOrigin(
        byte[] body, Dictionary<string, object> rangeParameters)
    {
        var handler = new RangeHonouringHandler(body);
        using var http = new HttpClient(handler);
        using (InjectCmdletTransport(owned: true, client: http))
        {
            using var ps = CreateContentShell();
            ps.AddCommand("Get-MgxContent").AddParameter("Uri", "/me/drive/items/01ABC/content");
            foreach (var (name, value) in rangeParameters)
                ps.AddParameter(name, value);

            var output = ps.Invoke();

            Assert.Empty(ps.Streams.Error);
            return (Assert.IsType<byte[]>(Assert.Single(output).BaseObject), handler.RangeHeader);
        }
    }

    [Fact]
    public void GetMgxContent_First_N_fetches_exactly_N_bytes()
    {
        var body = Encoding.UTF8.GetBytes("ABCDEFGHIJKLMNOPQRSTUVWXYZ");

        // -First 5 has to mean five bytes. Byte ranges are inclusive at both ends, so the last
        // byte requested is 4: asking for 0-5 pulls a sixth byte off the wire that the caller
        // never asked for and never sees flagged, because the server honored the range and no
        // local truncation runs. On the 256 KB-header scans this parameter exists for, that is
        // an extra byte per file and a header boundary landing one byte late.
        var (first, firstRange) = InvokeContentOverRangeHonouringOrigin(body, new() { ["First"] = 5L });
        Assert.Equal("ABCDE", Encoding.UTF8.GetString(first));
        Assert.Equal("bytes=0-4", firstRange);

        // The -Offset/-Length spelling of the same idea: 4 bytes from offset 5, end inclusive.
        var (window, windowRange) = InvokeContentOverRangeHonouringOrigin(
            body, new() { ["Offset"] = 5L, ["Length"] = 4L });
        Assert.Equal("FGHI", Encoding.UTF8.GetString(window));
        Assert.Equal("bytes=5-8", windowRange);

        // Unranged, the very same origin hands over the whole file - so the byte counts above
        // are the range mgx asked for, not the limit of the fixture.
        var (whole, wholeRange) = InvokeContentOverRangeHonouringOrigin(body, new());
        Assert.Equal(body, whole);
        Assert.Null(wholeRange);
    }

    /// <summary>
    /// An origin that ignores Range and answers 200 with the whole body - what the profile
    /// photo endpoint and a few CDN edges actually do. The requested slice then has to be cut
    /// out locally, or -Offset means nothing.
    /// </summary>
    private sealed class RangeIgnoringHandler(byte[] body) : HttpMessageHandler
    {
        /// <summary>The Range header the cmdlet sent, or null when none was sent.</summary>
        public string? RangeHeader { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RangeHeader = request.Headers.Range?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
                RequestMessage = request
            });
        }
    }

    /// <summary>Runs Get-MgxContent -Uri (plus range parameters) against a range-ignoring origin.</summary>
    private (byte[] Bytes, string? RangeHeader) InvokeContentOverRangeIgnoringOrigin(
        byte[] body, Dictionary<string, object> rangeParameters)
    {
        var handler = new RangeIgnoringHandler(body);
        using var http = new HttpClient(handler);
        using (InjectCmdletTransport(owned: true, client: http))
        {
            using var ps = CreateContentShell();
            ps.AddCommand("Get-MgxContent").AddParameter("Uri", "/me/drive/items/01ABC/content");
            foreach (var (name, value) in rangeParameters)
                ps.AddParameter(name, value);

            var output = ps.Invoke();

            Assert.Empty(ps.Streams.Error);
            return (Assert.IsType<byte[]>(Assert.Single(output).BaseObject), handler.RangeHeader);
        }
    }

    [Fact]
    public void GetMgxContent_honours_Offset_locally_when_the_server_ignores_the_range()
    {
        var body = Encoding.UTF8.GetBytes("ABCDEFGHIJKLMNOPQRSTUVWXYZ");

        // The server was asked for bytes 5-8 and sent all 26 anyway (HTTP 200). The caller
        // still asked for a window starting at 5, so the head has to be discarded here: the
        // failure mode this guards is silent, not loud - handing back "ABCD" and reporting
        // success looks exactly like a correct read to everything downstream.
        var (window, rangeHeader) = InvokeContentOverRangeIgnoringOrigin(
            body, new() { ["Offset"] = 5L, ["Length"] = 4L });
        Assert.Equal("bytes=5-8", rangeHeader);
        Assert.Equal("FGHI", Encoding.UTF8.GetString(window));

        // -First 5 is the offset-zero spelling: nothing to discard, so the same path must take
        // the bytes from the head. This is what stops "skip a fixed amount" from passing.
        var (first, firstRange) = InvokeContentOverRangeIgnoringOrigin(body, new() { ["First"] = 5L });
        Assert.Equal("bytes=0-4", firstRange);
        Assert.Equal("ABCDE", Encoding.UTF8.GetString(first));

        // -OutFile takes the same truncated copy, and it is the path where wrong bytes get
        // written to disk and outlive the session.
        var outFile = Path.Combine(Path.GetTempPath(), $"mgx-offset-{Guid.NewGuid():N}.bin");
        var handler = new RangeIgnoringHandler(body);
        using var http = new HttpClient(handler);
        using var transport = InjectCmdletTransport(owned: true, client: http);
        try
        {
            using var ps = CreateContentShell();
            ps.AddCommand("Get-MgxContent")
              .AddParameter("Uri", "/me/drive/items/01ABC/content")
              .AddParameter("Offset", 20L)
              .AddParameter("Length", 6L)
              .AddParameter("OutFile", outFile);
            ps.Invoke();

            Assert.Empty(ps.Streams.Error);
            Assert.Equal("UVWXYZ", Encoding.UTF8.GetString(File.ReadAllBytes(outFile)));
        }
        finally
        {
            if (File.Exists(outFile)) File.Delete(outFile);
        }
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

    [Fact]
    public async Task Copy_honours_the_offset_when_the_server_ignored_range()
    {
        // The offset case of the test above. A server that answers a ranged request with 200
        // and the whole body ignored the START of the range as well as its length, so the head
        // must be discarded locally. Before this was handled, -Offset 5 -Length 4 returned
        // "full" instead of "body" and reported success - silently wrong bytes.
        using var source = new MemoryStream(Encoding.UTF8.GetBytes("full body that the server sent anyway"));
        using var destination = new MemoryStream();

        var copied = await GraphContentClient.CopyWithIdleTimeoutAsync(
            source, destination, maxBytes: 4, TimeSpan.FromSeconds(5), CancellationToken.None,
            skipBytes: 5);

        Assert.Equal(4, copied);
        Assert.Equal("body", Encoding.UTF8.GetString(destination.ToArray()));
    }

    [Fact]
    public async Task Copy_throws_when_the_body_ends_before_the_offset()
    {
        // Returning 0 here was wrong, and this test previously asserted it. A count of zero is
        // indistinguishable from a legitimately empty resource, and WriteToFile does not inspect
        // the count - it moved the empty temp over the destination with overwrite: true,
        // destroying an existing file and exiting successfully. Throwing is the only signal the
        // caller can act on.
        using var source = new MemoryStream(Encoding.UTF8.GetBytes("short"));
        using var destination = new MemoryStream();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => GraphContentClient.CopyWithIdleTimeoutAsync(
                source, destination, maxBytes: 10, TimeSpan.FromSeconds(5), CancellationToken.None,
                skipBytes: 500));

        Assert.Contains("before the requested offset", ex.Message);
        Assert.Empty(destination.ToArray());
    }

    [Fact]
    public async Task An_empty_resource_still_writes_an_empty_result()
    {
        // The counterpart: a genuinely empty body with no offset requested must NOT throw. The
        // fix above must distinguish "the slice does not exist" from "the resource is empty",
        // which a bare copied == 0 check would not.
        using var source = new MemoryStream([]);
        using var destination = new MemoryStream();

        var copied = await GraphContentClient.CopyWithIdleTimeoutAsync(
            source, destination, maxBytes: null, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(0, copied);
        Assert.Empty(destination.ToArray());
    }

    [Fact]
    public async Task A_redirect_counts_as_success_only_for_the_caller_that_expects_one()
    {
        // Content path: the 302 is the documented success path, so it must not book a failure.
        QueueRedirectToCdn();
        _cdnHandler.QueueResponse(HttpStatusCode.OK, "payload!");
        MgxTelemetryCollector.Current.Reset();

        using var result = await _client.GetContentAsync(GraphContentUrl);
        await ReadAllAsync(result);

        Assert.Equal(0, MgxTelemetryCollector.Current.GetSummary().Failed);

        // Ordinary path: AllowAutoRedirect is off and every non-content caller throws on a 3xx,
        // so booking it as succeeded would report success for a request the user saw fail.
        _graphHandler.QueueResponse(HttpStatusCode.Found, null,
            new() { ["Location"] = "https://contoso-my.sharepoint.com/download.aspx" });
        MgxTelemetryCollector.Current.Reset();

        using var plain = await _client.SendAsync(
            HttpMethod.Get, "https://graph.microsoft.com/v1.0/reports/x");

        Assert.Equal(HttpStatusCode.Found, plain.StatusCode);
        var summary = MgxTelemetryCollector.Current.GetSummary();
        Assert.Equal(1, summary.Failed);
        Assert.Equal(0, summary.Succeeded);
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