using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Threading.RateLimiting;
using Polly;

namespace Mgx.Engine.Http;

/// <summary>
/// DelegatingHandler that applies the shared Polly resilience pipeline to HTTP requests.
/// Used by Enable-MgxResilience to inject retry/circuit breaker/rate limiting
/// into the Microsoft.Graph SDK's HttpClient handler chain.
/// </summary>
public sealed class ResilientDelegatingHandler : DelegatingHandler
{
    private readonly ResiliencePipeline<HttpResponseMessage> _pipeline;
    private readonly TokenBucketRateLimiter? _rateLimiter;
    private readonly ConcurrentQueue<string> _pendingVerbose = new();

    /// <summary>
    /// Optional callback for verbose messages from the resilience pipeline.
    /// Messages are buffered during pipeline execution and drained after
    /// ExecuteAsync returns on the calling thread.
    /// </summary>
    public Action<string>? VerboseWriter { get; set; }

    /// <summary>
    /// Options stamped onto every request this handler sends, in addition to the caller's own.
    /// The SDK bridge uses it to disarm the retry handler sitting inside the wrapped chain: that
    /// handler answers 429 and 503 before this pipeline ever sees them, so the pacer never learns
    /// from a throttle and telemetry books a throttled session as zero. Kiota reads its retry
    /// option per request, so setting it here is what reaches it.
    ///
    /// Keyed by string and typed as object so the engine needs no reference to the SDK; the
    /// caller supplies whatever the inner handler expects.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? AdditionalRequestOptions { get; init; }

    /// <summary>
    /// Resolved on the first request rather than when the handler is built. The type it needs
    /// belongs to the SDK and is not loaded until the SDK has sent something through its own
    /// chain, so building the options eagerly found nothing and silently left the inner handler
    /// armed. Called once; the result, including null, is kept.
    /// </summary>
    public Func<IReadOnlyDictionary<string, object?>?>? AdditionalRequestOptionsFactory { get; init; }

    private IReadOnlyDictionary<string, object?>? _resolvedOptions;
    private bool _optionsResolved;

    private IReadOnlyDictionary<string, object?>? ResolveAdditionalOptions()
    {
        if (AdditionalRequestOptions != null) return AdditionalRequestOptions;
        if (AdditionalRequestOptionsFactory == null) return null;
        if (_optionsResolved) return _resolvedOptions;

        try
        {
            _resolvedOptions = AdditionalRequestOptionsFactory();
        }
        catch (Exception ex)
        {
            // The factory runs on a request thread, long after the cmdlet that supplied it
            // finished its pipeline - so anything it touches that expects to be on that
            // pipeline throws here. Failing the request over an option that only annotates it
            // would be worse than not setting the option: leaving it null just means the inner
            // handler keeps the retry behavior it had before the override existed.
            _pendingVerbose.Enqueue($"Could not configure the SDK's retry option: {ex.Message}");
            _resolvedOptions = null;
        }

        _optionsResolved = true;
        return _resolvedOptions;
    }

    public ResilientDelegatingHandler(
        ResiliencePipeline<HttpResponseMessage> pipeline,
        TokenBucketRateLimiter? rateLimiter)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _rateLimiter = rateLimiter;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Buffer content upfront so retries can reconstruct a fresh request body.
        // Also snapshot all content headers (not just ContentType) to preserve
        // Content-Encoding, Content-Disposition, etc. on retry.
        // What the pipeline cannot replay, it does not manage. A body whose declared
        // length exceeds the replay-buffer cap (a drive-content upload runs to 250MB)
        // passes to the SDK chain untouched: no clone, no retry-option stamp, no
        // per-attempt timeout, no circuit counting - exactly what the SDK does without
        // the wrap, which is the only promise the wrap can keep for a stream it cannot
        // rewind. A body with no declared length is buffered whatever its size - the
        // read is unavoidable to know, and once buffered it replays like any other.
        if (request.Content?.Headers.ContentLength > ResilientGraphClient.MaxRequestBodyBytes)
        {
            var passSw = System.Diagnostics.Stopwatch.StartNew();
            var passSucceeded = false;
            try
            {
                var passthrough = await base.SendAsync(request, cancellationToken);
                passSucceeded = passthrough.IsSuccessStatusCode;
                return passthrough;
            }
            finally
            {
                MgxTelemetryCollector.Current.RecordRequest(passSucceeded, passSw.ElapsedMilliseconds);
            }
        }

        byte[]? contentBytes = null;
        List<KeyValuePair<string, IEnumerable<string>>>? contentHeaders = null;
        if (request.Content != null)
        {
            contentBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            contentHeaders = request.Content.Headers.ToList();
        }
        var clientRequestId = Guid.NewGuid().ToString();

        RateLimitLease? lease = null;
        var context = ResilienceContextPool.Shared.Get(cancellationToken);
        context.Properties.Set(ResiliencePipelineFactory.IsIdempotentKey, request.Method != HttpMethod.Post);
        context.Properties.Set(ResiliencePipelineFactory.VerboseWriterKey,
            (Action<string>)(msg => _pendingVerbose.Enqueue(msg)));
        var bucket = AdaptivePacing.Classify(request.RequestUri?.ToString());
        // Telemetry parity with ResilientGraphClient.SendAsync. Without it an
        // Enable-MgxResilience session reported TotalRequests=0 alongside a non-zero retry
        // count - self-contradictory, and a divide-by-zero for the throttle-rate calculation
        // the Get-MgxTelemetry help tells users to compute.
        var totalSw = System.Diagnostics.Stopwatch.StartNew();
        var succeeded = false;
        var recorded = false;
        try
        {
            // Same proactive gate as ResilientGraphClient.SendAsync. This is the
            // Enable-MgxResilience path: SDK cmdlet traffic bypasses ResilientGraphClient
            // entirely, so pacing hooked only there would skip SDK-wrapped workloads.
            var pacedMs = await AdaptiveRequestPacer.WaitAsync(bucket, cancellationToken);
            if (pacedMs > 0)
            {
                MgxTelemetryCollector.Current.RecordPacingWait(pacedMs);
                _pendingVerbose.Enqueue($"Adaptive pacing: waited {pacedMs}ms before sending ({bucket} workload)");
            }

            if (_rateLimiter != null)
            {
                var limiterSw = Stopwatch.StartNew();
                lease = await _rateLimiter.AcquireAsync(1, cancellationToken);
                MgxTelemetryCollector.Current.RecordRateLimiterWait(limiterSw.ElapsedMilliseconds);
                if (!lease.IsAcquired)
                    throw new InvalidOperationException("Rate limit exceeded. Too many concurrent requests. Reduce -Concurrency on fan-out cmdlets, increase the queue with Set-MgxOption -RateLimitQueueLimit, or disable with Set-MgxOption -NoRateLimit.");
            }

            var result = await _pipeline.ExecuteAsync(
                async ctx =>
                {
                    // Clone on every attempt, including the first. On the SDK bridge path
                    // (Enable-MgxResilience), the outer HttpClient sets _sendStatus = AlreadySent
                    // before this handler runs. Passing the original to SdkClientBridgeHandler
                    // throws "already sent". Cloning resets the flag.
                    var clone = new HttpRequestMessage(request.Method, request.RequestUri)
                    {
                        Version = request.Version,
                        VersionPolicy = request.VersionPolicy
                    };

                    foreach (var header in request.Headers)
                        clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    clone.Headers.TryAddWithoutValidation("SdkVersion", MgxSdkVersion.Value);
                    // Parity with the owned client: one correlation id per logical request,
                    // shared across attempts - unless the SDK already stamped its own.
                    if (!clone.Headers.Contains("client-request-id"))
                        clone.Headers.TryAddWithoutValidation("client-request-id", clientRequestId);

                    // Copy request options (used by SDK handlers for per-request metadata)
#pragma warning disable CS8714 // nullability mismatch in IDictionary generic
                    foreach (var option in request.Options)
                        ((IDictionary<string, object?>)clone.Options)[option.Key] = option.Value;

                    // After the caller's own, so a handler-level stamp wins: the point is to
                    // override what the inner chain would otherwise do on its own.
                    var extra = ResolveAdditionalOptions();
                    if (extra != null)
                        foreach (var option in extra)
                            ((IDictionary<string, object?>)clone.Options)[option.Key] = option.Value;
#pragma warning restore CS8714

                    if (contentBytes != null)
                    {
                        var freshContent = new ByteArrayContent(contentBytes);
                        foreach (var header in contentHeaders!)
                            freshContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
                        clone.Content = freshContent;
                    }

                    // Per-attempt network time, measured INSIDE the pipeline so retry delays are
                    // excluded - the same placement the owned client uses. Without this,
                    // Get-MgxTelemetry reported "HTTP Time (ms): 0" for every Enable-MgxResilience
                    // session, which is indistinguishable from no network time at all.
                    // NOTE: deliberately not AdaptiveRequestPacer.RecordLatency - see the comment
                    // after ExecuteAsync for why the pacer baseline stays off this path.
                    // Caveat: on this path the SDK's own retry handler sleeps INSIDE
                    // base.SendAsync, so its Retry-After waits land in HttpMs while
                    // RetryDelayMs stays 0 (that counts Polly's waits, and Polly is outside).
                    // Separating them would mean reaching into the SDK's handler chain.
                    // Bounded by AttemptTimeoutSeconds, and the alternative was reporting 0.
                    var httpSw = Stopwatch.StartNew();
                    var attempt = await base.SendAsync(clone, ctx.CancellationToken);
                    MgxTelemetryCollector.Current.RecordHttpTime(httpSw.ElapsedMilliseconds);
                    return attempt;
                },
                context);

            // Feed the pacer's signal state (proximity percentage, final 429). Latency is
            // not recorded on this path: timing here would include retry delays inside
            // ExecuteAsync, which would pollute the network-time baseline.
            AdaptiveRequestPacer.RecordResponse(bucket, result);

            // A 3xx is an error on this path: the SDK bridge does not follow redirects itself
            // and every caller surfaces them, so only 2xx counts as success here.
            succeeded = result.IsSuccessStatusCode;
            if (result.Headers.TryGetValues("x-ms-resource-unit", out var ruValues)
                && long.TryParse(System.Linq.Enumerable.FirstOrDefault(ruValues), out var ru)
                && ru > 0)
            {
                MgxTelemetryCollector.Current.RecordResourceUnit(ru);
            }
            return result;
        }
        finally
        {
            if (!recorded)
            {
                recorded = true;
                totalSw.Stop();
                MgxTelemetryCollector.Current.RecordRequest(succeeded, totalSw.ElapsedMilliseconds);
            }
            // Drain buffered verbose messages on the calling thread
            if (VerboseWriter != null)
            {
                while (_pendingVerbose.TryDequeue(out var msg))
                    VerboseWriter(msg);
            }
            else
            {
                while (_pendingVerbose.TryDequeue(out _)) { }
            }

            ResilienceContextPool.Shared.Return(context);
            lease?.Dispose();
        }
    }
}
