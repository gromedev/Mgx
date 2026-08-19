using System.Net;
using System.Net.Http.Headers;
using Polly;
using Polly.Retry;

namespace Mgx.Engine.Http;

/// <summary>
/// Result of a content fetch. Owns the response: dispose it to release the connection.
/// </summary>
public sealed class GraphContentResult : IDisposable
{
    public required Stream Content { get; init; }
    public HttpStatusCode StatusCode { get; init; }
    public long? ContentLength { get; init; }
    public string? ContentRange { get; init; }
    public string? ContentType { get; init; }
    public string? ETag { get; init; }

    /// <summary>True when the bytes came from the pre-authenticated download host (hop 2)
    /// rather than directly from Graph.</summary>
    public bool FromDownloadHost { get; init; }

    internal HttpResponseMessage? OwnedResponse { get; init; }

    public void Dispose()
    {
        Content.Dispose();
        OwnedResponse?.Dispose();
    }
}

/// <summary>
/// The two-hop content path behind Get-MgxContent.
///
/// Hop 1 (Graph, authenticated) goes through ResilientGraphClient.SendAsync - full pipeline,
/// pacer, bucket lease. A 2xx with a body is content served directly by Graph (attachments,
/// photos). A redirect is the drive-item case: the Location is a pre-authenticated URL on a
/// download host, validated against DownloadUrlValidator before hop 2 touches it.
///
/// Hop 2 (download host, token-free) uses a static singleton HttpClient with NO auth handler:
/// the bearer must never reach the download host. AllowAutoRedirect is off and redirects are
/// followed manually (max 3), re-validating every Location - an open redirect on an
/// allowlisted host must not silently bypass the validator. Its own small retry pipeline
/// handles 429/5xx with Retry-After; deliberately NO circuit breaker (a CDN outage must not
/// poison the Graph circuit) and NO bucket charge (the request budget is Graph-side).
/// On 401/403 (expired pre-auth URL) the caller re-runs hop 1 once for a fresh URL.
///
/// Transport precondition (fail closed): the 302 is only observable because the owned clean
/// HttpClient sets AllowAutoRedirect=false. On a transport that auto-follows (the SDK
/// fallback client ships Kiota's RedirectHandler; injected transports are unknown), the
/// bytes would arrive as a 2xx from a host mgx never validated - so any 2xx whose request
/// URI left the Graph host is rejected here, and the cmdlet layer refuses non-owned
/// transports outright before calling in.
/// </summary>
public static class GraphContentClient
{
    private const int MaxManualRedirects = 3;

    /// <summary>Statuses hop 1 treats as "go fetch from the download host".</summary>
    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.Moved or HttpStatusCode.Found
            or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect
            or (HttpStatusCode)308;

    /// <summary>Test seam: replaces the token-free hop-2 client so download-host behavior
    /// (redirects, 429/5xx, auth-expiry) can be mocked. Never set from production code.</summary>
    internal static HttpClient? DownloadClientForTests;

    // Token-free singleton for hop 2. No auth handler by construction; decompression stays
    // off so ranged reads and Content-Length are byte-exact.
    private static readonly HttpClient s_downloadClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TransportDefaults.PooledConnectionLifetime,
        MaxConnectionsPerServer = TransportDefaults.MaxConnectionsPerServer,
        ConnectTimeout = TransportDefaults.ConnectTimeout
    })
    {
        // Covers until response headers (ResponseHeadersRead); the body copy is bounded by
        // the caller's idle timeout, not this.
        Timeout = TimeSpan.FromSeconds(100)
    };

    // Small reactive pipeline for hop 2: retry 3 with exponential backoff + jitter on
    // 429/5xx/transport errors, honoring Retry-After clamped to two minutes.
    private static readonly ResiliencePipeline<HttpResponseMessage> s_downloadPipeline =
        new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromSeconds(1),
                MaxDelay = TimeSpan.FromSeconds(120),
                ShouldHandle = args =>
                {
                    if (args.Outcome.Result?.StatusCode is (HttpStatusCode)429
                        or HttpStatusCode.InternalServerError
                        or HttpStatusCode.BadGateway
                        or HttpStatusCode.ServiceUnavailable
                        or HttpStatusCode.GatewayTimeout)
                        return ValueTask.FromResult(true);
                    return ValueTask.FromResult(args.Outcome.Exception is HttpRequestException);
                },
                DelayGenerator = args =>
                {
                    // Retry-After has two legal forms: delta-seconds and an HTTP-date. Honouring
                    // only Delta silently fell back to plain exponential backoff whenever a
                    // download host chose the date form - and the main pipeline already handles
                    // both, so this path was the odd one out.
                    var retryAfter = args.Outcome.Result?.Headers.RetryAfter;
                    var cap = TimeSpan.FromSeconds(120);
                    if (retryAfter?.Delta is { } delta)
                        return ValueTask.FromResult<TimeSpan?>(delta > cap ? cap : delta);
                    if (retryAfter?.Date is { } date)
                    {
                        var delay = date - DateTimeOffset.UtcNow;
                        if (delay > TimeSpan.Zero)
                            return ValueTask.FromResult<TimeSpan?>(delay > cap ? cap : delay);
                    }
                    return ValueTask.FromResult<TimeSpan?>(null);
                },
                OnRetry = args =>
                {
                    args.Outcome.Result?.Dispose();
                    return default;
                }
            })
            .Build();

    /// <summary>
    /// Fetch content for a Graph $value / /content URI, optionally a byte range.
    /// <paramref name="requestUri"/> must be absolute (the cmdlet layer builds it).
    /// </summary>
    public static async Task<GraphContentResult> GetContentAsync(
        ResilientGraphClient graphClient,
        string requestUri,
        RangeHeaderValue? range,
        Dictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        var graphHost = new Uri(requestUri).Host;
        var requestHeaders = headers != null
            ? new Dictionary<string, string>(headers)
            : new Dictionary<string, string>();
        if (range != null)
            requestHeaders["Range"] = range.ToString();

        // Two passes at most: a hop-2 401/403 means the pre-authenticated URL expired
        // (they are short-lived), so re-run hop 1 once for a fresh one.
        for (var authAttempt = 0; ; authAttempt++)
        {
            // redirectIsSuccess: this is the one caller that expects a 302 and follows it, so
            // a redirect here is the documented success path, not a failed request.
            var hop1 = await graphClient.SendAsync(
                HttpMethod.Get, requestUri, headers: requestHeaders,
                cancellationToken: cancellationToken, traceResponseBody: false,
                redirectIsSuccess: true);

            if (hop1.IsSuccessStatusCode)
            {
                // Fail closed: a 2xx that did not come from the Graph host means the
                // transport auto-followed the redirect - the host was never validated and
                // bearer handling depended on transport internals. Refuse the bytes.
                var finalHost = hop1.RequestMessage?.RequestUri?.Host;
                if (finalHost != null && !string.Equals(finalHost, graphHost, StringComparison.OrdinalIgnoreCase))
                {
                    hop1.Dispose();
                    throw new InvalidOperationException(
                        $"The transport followed a redirect to '{finalHost}' before mgx could validate it. "
                        + "Content downloads require the mgx-owned HTTP client (AllowAutoRedirect off).");
                }
                return await WrapAsync(hop1, fromDownloadHost: false, graphClient.BodyReadTimeout, cancellationToken);
            }

            if (IsRedirect(hop1.StatusCode))
            {
                var location = hop1.Headers.Location;
                hop1.Dispose();
                if (location == null)
                    throw new HttpRequestException("Graph returned a redirect without a Location header.");

                var absolute = location.IsAbsoluteUri
                    ? location.ToString()
                    : new Uri(new Uri(requestUri), location).ToString();
                var validated = DownloadUrlValidator.Validate(absolute)
                    ?? throw new InvalidOperationException(
                        $"Download host '{TryGetHost(absolute)}' is not on the allowed list "
                        + "(SharePoint/OneDrive download hosts only). Refusing to fetch content from it.");

                var hop2 = await FetchFromDownloadHostAsync(validated, range, cancellationToken);

                if (hop2.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    && authAttempt == 0)
                {
                    // Expired pre-auth URL: one fresh hop-1, one more try.
                    hop2.Dispose();
                    continue;
                }

                if (hop2.IsSuccessStatusCode)
                    return await WrapAsync(hop2, fromDownloadHost: true, graphClient.BodyReadTimeout, cancellationToken);

                var hop2Body = await ReadErrorBodyAsync(hop2, graphClient.BodyReadTimeout, cancellationToken);
                var hop2Status = hop2.StatusCode;
                hop2.Dispose();
                throw new Models.GraphServiceException(hop2Status, hop2Body);
            }

            // Plain Graph error (404, 401, 416, ...): surface it like every other cmdlet path.
            var body = await ReadErrorBodyAsync(hop1, graphClient.BodyReadTimeout, cancellationToken);
            var status = hop1.StatusCode;
            hop1.Dispose();
            throw new Models.GraphServiceException(status, body);
        }
    }

    /// <summary>
    /// Fetch directly from a pre-authenticated download URL (a piped driveItem's
    /// @microsoft.graph.downloadUrl), skipping hop 1. The caller MUST have validated the URL
    /// through DownloadUrlValidator first; this method validates again and throws otherwise.
    /// No auth refresh is possible on this path - the URL is short-lived, and a 401/403 means
    /// the item must be re-fetched for a fresh one.
    /// </summary>
    public static async Task<GraphContentResult> GetFromDownloadUrlAsync(
        string downloadUrl,
        RangeHeaderValue? range,
        TimeSpan bodyReadTimeout,
        CancellationToken cancellationToken)
    {
        var validated = DownloadUrlValidator.Validate(downloadUrl)
            ?? throw new InvalidOperationException(
                $"Download host '{TryGetHost(downloadUrl)}' is not on the allowed list "
                + "(SharePoint/OneDrive download hosts only). Refusing to fetch content from it.");

        var response = await FetchFromDownloadHostAsync(validated, range, cancellationToken);
        if (response.IsSuccessStatusCode)
            return await WrapAsync(response, fromDownloadHost: true, bodyReadTimeout, cancellationToken);

        var body = await ReadErrorBodyAsync(response, bodyReadTimeout, cancellationToken);
        var status = response.StatusCode;
        response.Dispose();
        throw new Models.GraphServiceException(status, body);
    }

    /// <summary>
    /// Token-free fetch with manual, re-validated redirects. Every hop must pass the
    /// allowlist: an open redirect on an allowlisted host is not a pass.
    /// </summary>
    private static async Task<HttpResponseMessage> FetchFromDownloadHostAsync(
        string url, RangeHeaderValue? range, CancellationToken cancellationToken)
    {
        var current = url;
        for (var redirects = 0; ; redirects++)
        {
            var context = ResilienceContextPool.Shared.Get(cancellationToken);
            HttpResponseMessage response;
            try
            {
                var target = current;
                response = await s_downloadPipeline.ExecuteAsync(
                    async ctx =>
                    {
                        var request = new HttpRequestMessage(HttpMethod.Get, target);
                        if (range != null)
                            request.Headers.Range = range;
                        return await (DownloadClientForTests ?? s_downloadClient).SendAsync(
                            request, HttpCompletionOption.ResponseHeadersRead, ctx.CancellationToken);
                    },
                    context);
            }
            finally
            {
                ResilienceContextPool.Shared.Return(context);
            }

            if (!IsRedirect(response.StatusCode))
                return response;

            var location = response.Headers.Location;
            response.Dispose();
            if (location == null)
                throw new HttpRequestException("Download host returned a redirect without a Location header.");
            if (redirects >= MaxManualRedirects)
                throw new HttpRequestException(
                    $"Download exceeded {MaxManualRedirects} redirects. Refusing to continue.");

            var absolute = location.IsAbsoluteUri
                ? location.ToString()
                : new Uri(new Uri(current), location).ToString();
            current = DownloadUrlValidator.Validate(absolute)
                ?? throw new InvalidOperationException(
                    $"Download host '{TryGetHost(absolute)}' is not on the allowed list "
                    + "(redirected mid-download). Refusing to follow.");
        }
    }

    private static async Task<GraphContentResult> WrapAsync(
        HttpResponseMessage response, bool fromDownloadHost, TimeSpan bodyReadTimeout,
        CancellationToken cancellationToken)
    {
        // ResponseHeadersRead: opening the stream is cheap; the caller copies with an idle
        // timeout (CopyWithIdleTimeoutAsync) so a stalled body cannot hang forever.
        using var bodyCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bodyCts.CancelAfter(bodyReadTimeout);
        Stream stream;
        try
        {
            stream = await response.Content.ReadAsStreamAsync(bodyCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            response.Dispose();
            throw new HttpRequestException("Content stream open timed out.");
        }

        return new GraphContentResult
        {
            Content = stream,
            StatusCode = response.StatusCode,
            ContentLength = response.Content.Headers.ContentLength,
            ContentRange = response.Content.Headers.ContentRange?.ToString(),
            ContentType = response.Content.Headers.ContentType?.ToString(),
            ETag = response.Headers.ETag?.ToString(),
            FromDownloadHost = fromDownloadHost,
            OwnedResponse = response
        };
    }

    private static async Task<string> ReadErrorBodyAsync(
        HttpResponseMessage response, TimeSpan bodyReadTimeout, CancellationToken cancellationToken)
    {
        try
        {
            using var bodyCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bodyCts.CancelAfter(bodyReadTimeout);
            return await response.Content.ReadAsStringAsync(bodyCts.Token);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string TryGetHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    /// <summary>
    /// Copy up to <paramref name="maxBytes"/> (null = unbounded) with a PER-READ idle
    /// timeout: the clock resets on progress, so a slow-but-moving download of any size
    /// survives while a stalled stream aborts within <paramref name="idleTimeout"/>.
    /// Returns the byte count copied and records it in telemetry.
    /// The maxBytes cut is the truncation path: when a server ignores Range and answers 200
    /// with the full body, the caller passes the requested length and disposes the result,
    /// aborting the rest of the transfer.
    /// </summary>
    public static async Task<long> CopyWithIdleTimeoutAsync(
        Stream source, Stream destination, long? maxBytes, TimeSpan idleTimeout,
        CancellationToken cancellationToken, long skipBytes = 0)
    {
        var buffer = new byte[81920];

        // Discard the bytes before the requested offset. Only reachable when a ranged request
        // was answered with 200 and the whole body: the server ignored the offset, so it has to
        // be honoured here instead. Without this, -Offset 1MB -Length 64KB returned bytes
        // 0..65535 and reported success - the wrong bytes, silently, which is worse than an
        // error because nothing downstream can tell.
        long skipped = 0;
        while (skipped < skipBytes)
        {
            var toSkip = (int)Math.Min(buffer.Length, skipBytes - skipped);
            var r = await ReadWithIdleTimeoutAsync(source, buffer, toSkip, idleTimeout, cancellationToken);
            if (r == 0)
            {
                // The body ended before the offset was reached, so the requested slice does not
                // exist. Returning 0 was indistinguishable from a legitimately empty resource,
                // and the -OutFile path does not inspect the count: it moved the empty temp over
                // the destination, destroying an existing file and reporting success. Throwing
                // is the only signal the caller can act on - its catch deletes the temp and
                // rethrows, so the destination is left untouched, which is what the surrounding
                // code already promises.
                throw new InvalidOperationException(
                    $"The content ended after {skipped:N0} bytes, before the requested offset of "
                    + $"{skipBytes:N0}. The requested range does not exist in this resource.");
            }
            skipped += r;
        }

        long total = 0;
        while (maxBytes == null || total < maxBytes.Value)
        {
            var toRead = maxBytes == null
                ? buffer.Length
                : (int)Math.Min(buffer.Length, maxBytes.Value - total);

            var read = await ReadWithIdleTimeoutAsync(source, buffer, toRead, idleTimeout, cancellationToken);
            if (read == 0) break;
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            total += read;
        }

        if (total > 0)
            MgxTelemetryCollector.Current.RecordContentBytes(total);
        return total;
    }

    /// <summary>
    /// One read, bounded by the idle timeout. A stalled body must not hang forever, and the
    /// timeout applies per read rather than to the transfer as a whole so a slow-but-progressing
    /// download is not killed.
    /// </summary>
    private static async Task<int> ReadWithIdleTimeoutAsync(
        Stream source, byte[] buffer, int count, TimeSpan idleTimeout,
        CancellationToken cancellationToken)
    {
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readCts.CancelAfter(idleTimeout);
        try
        {
            return await source.ReadAsync(buffer.AsMemory(0, count), readCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException(
                $"Content download stalled: no data for {idleTimeout.TotalSeconds:F0}s.");
        }
    }
}
