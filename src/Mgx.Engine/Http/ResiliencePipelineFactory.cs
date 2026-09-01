using Mgx.Engine.Errors;
using System.Net;
using System.Net.Http.Headers;
using System.Threading.RateLimiting;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;



namespace Mgx.Engine.Http;

/// <summary>
/// Manages shared Polly resilience pipeline and rate limiter instances.
/// Circuit breaker and rate limiter MUST be shared across cmdlet invocations
/// to function correctly: circuit breaker needs failure history across calls,
/// rate limiter needs cumulative token consumption.
/// </summary>
public static class ResiliencePipelineFactory
{
    private static readonly object s_lock = new();
    private static ResiliencePipeline<HttpResponseMessage>? s_pipeline;
    private static TokenBucketRateLimiter? s_rateLimiter;
    private static ResilientGraphClientOptions? s_cachedOptions;

    /// <summary>
    /// Property key for passing idempotency info into the Polly retry predicate.
    /// POST is the only non-idempotent Graph method; it only retries on 429.
    /// </summary>
    internal static readonly ResiliencePropertyKey<bool> IsIdempotentKey = new("IsIdempotent");

    /// <summary>
    /// Property key for passing a per-invocation verbose writer into OnRetry.
    /// Set on the ResilienceContext before each pipeline execution so the shared
    /// pipeline can log without capturing per-invocation state in closures.
    /// </summary>
    internal static readonly ResiliencePropertyKey<Action<string>?> VerboseWriterKey = new("VerboseWriter");

    /// <summary>
    /// Property key for a caller's stop signal. Once canceled, no further attempt is decided
    /// on for this request. It deliberately does not cancel the request: the attempt in flight
    /// finishes and its answer is returned, because that answer is the only record of what the
    /// server did with it. What stops is the decision to send another one.
    /// </summary>
    internal static readonly ResiliencePropertyKey<CancellationToken> StopRetriesKey = new("StopRetries");

    /// <summary>
    /// Get or create a shared resilience pipeline and rate limiter.
    /// Rebuilds when options change (detected by reference equality, since
    /// Set-MgxOption creates a new ResilientGraphClientOptions each time).
    /// Old rate limiters are disposed after a delay to avoid racing with in-flight clients.
    /// </summary>
    public static (ResiliencePipeline<HttpResponseMessage> Pipeline, TokenBucketRateLimiter? RateLimiter)
        GetOrCreate(ResilientGraphClientOptions options)
    {
        lock (s_lock)
        {
            if (s_pipeline != null && ReferenceEquals(s_cachedOptions, options))
                return (s_pipeline, s_rateLimiter);

            // Schedule delayed disposal of the old rate limiter. It may still be
            // referenced by in-flight ResilientGraphClient instances, so we wait
            // TotalTimeoutSeconds to ensure all in-flight requests have completed.
            // Not disposed. The limiter is captured as a readonly field by every
            // ResilientGraphClient already built from it, and by the handler
            // Enable-MgxResilience injects into the SDK - and that handler is not rebuilt when
            // options change. Disposing on a timer therefore broke live sessions: any
            // Set-MgxOption call left every SDK cmdlet throwing "Cannot access a disposed
            // object. Object name: 'TokenBucketRateLimiter'" once the delay elapsed, recoverable
            // only by Disable-MgxResilience; Enable-MgxResilience.
            //
            // The delay was TotalTimeoutSeconds - a per-REQUEST timeout used as a resource
            // lifetime. No such bound is correct: a client may hold its limiter for a multi-hour
            // export. Dropping the reference is enough; the limiter owns no unmanaged handle and
            // its replenishment timer is rooted only by the limiter itself.

            TokenBucketRateLimiter? rateLimiter = null;
            if (!options.NoRateLimit)
            {
                rateLimiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
                {
                    TokenLimit = options.RateLimitBurst,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                    TokensPerPeriod = options.RateLimitPerSecond,
                    QueueLimit = options.RateLimitQueueLimit,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    AutoReplenishment = true
                });
            }

            s_pipeline = BuildPipeline(options);
            s_rateLimiter = rateLimiter;
            s_cachedOptions = options;

            // The chokepoint every client build passes through: apply pacing configuration
            // here so Set-MgxOption changes reach the pacer on the next invocation.
            AdaptiveRequestPacer.Configure(options);

            return (s_pipeline, rateLimiter);
        }
    }

    /// <summary>
    /// Force rebuild on next access. Call when tenant changes to reset
    /// circuit breaker state (failure history from old tenant is irrelevant).
    /// </summary>
    public static void Reset()
    {
        lock (s_lock)
        {
            s_pipeline = null;
            // Dispose after delay: in-flight clients may still reference the old limiter.
            // Default 300s covers the maximum total timeout window.
            // Not disposed, for the reason documented in GetOrCreate.
            s_rateLimiter = null;
            s_cachedOptions = null;
            // Learned pacing state describes the old tenant; clear it with the CB history.
            AdaptiveRequestPacer.Reset();
            // GraphBatchClient keeps its own AIMD state in separate statics, so clearing the
            // request pacer alone left the batch pacer describing the previous tenant: a fresh
            // credential started at the throttled item rate learned elsewhere, and its first
            // batch was additionally delayed by the old tenant's completion timestamp.
            GraphBatchClient.ResetPacingState();
        }
    }

    /// <summary>
    /// Disposes a rate limiter after a delay. TokenBucketRateLimiter holds an internal
    /// Timer (via AutoReplenishment) that acts as a GC root. Immediate disposal would
    /// cause ObjectDisposedException in in-flight clients, so we wait for the total
    /// timeout window to expire before disposing.
    /// </summary>

    private static ResiliencePipeline<HttpResponseMessage> BuildPipeline(ResilientGraphClientOptions options)
    {
        var maxRetryAfterCap = options.MaxRetryAfterSeconds;

        return new ResiliencePipelineBuilder<HttpResponseMessage>()
            // Total timeout
            .AddTimeout(TimeSpan.FromSeconds(options.TotalTimeoutSeconds))
            // Retry
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = options.MaxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromSeconds(1),
                MaxDelay = TimeSpan.FromSeconds(maxRetryAfterCap),
                ShouldHandle = args =>
                {
                    // The caller has stopped sending. Asked before the classification because
                    // it is not about this outcome: whatever the server said, the run this
                    // request belongs to is not sending anything else.
                    if (args.Context.Properties.GetValue(StopRetriesKey, default).IsCancellationRequested)
                        return ValueTask.FromResult(false);

                    // Classification and the retry decision live in Errors/, shared with the
                    // batch client and the download pipeline; ErrorPolicyParityTests holds
                    // them to the decisions this predicate used to make inline. Per-attempt
                    // timeouts (TimeoutRejectedException) retry so the Enable-MgxResilience
                    // path survives the SDK honoring a Retry-After longer than
                    // AttemptTimeoutSeconds; the outer TotalTimeout still bounds the whole.
                    var isIdempotent = args.Context.Properties.GetValue(IsIdempotentKey, true);
                    var info = args.Outcome.Result is { } response
                        ? MgxErrorClassifier.Classify(response)
                        : args.Outcome.Exception is { } ex
                            ? MgxErrorClassifier.Classify(ex, args.Context.CancellationToken.IsCancellationRequested)
                            : new MgxErrorInfo(MgxErrorClass.Permanent, 0);
                    return ValueTask.FromResult(MgxErrorPolicy.ShouldRetry(info.Class, isIdempotent));
                },
                DelayGenerator = args =>
                    // Respect Retry-After from the service; null falls back to the
                    // exponential backoff configured above.
                    ValueTask.FromResult(RetryAfterPolicy.Resolve(
                        args.Outcome.Result?.Headers.RetryAfter,
                        TimeSpan.FromSeconds(maxRetryAfterCap))),
                OnRetry = args =>
                {
                    var response = args.Outcome.Result;
                    var status = response?.StatusCode;
                    var retryAfter = response?.Headers.RetryAfter;
                    var attempt = args.AttemptNumber;
                    var retryDelay = args.RetryDelay;

                    // Feed the pacer BEFORE the response is disposed below - the request URI
                    // and Retry-After are unreadable afterwards. Retried 429s never reach the
                    // SendAsync success path, so this is the only place they are observed.
                    if (status == (HttpStatusCode)429)
                    {
                        TimeSpan? serverDelay = retryAfter?.Delta
                            ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
                        AdaptiveRequestPacer.RecordThrottle(
                            AdaptivePacing.Classify(response?.RequestMessage?.RequestUri?.ToString()),
                            serverDelay);
                    }

                    // Dispose the previous response to drain the connection back to the pool.
                    // With HttpCompletionOption.ResponseHeadersRead, the body stream stays open
                    // until explicitly read or the response is disposed. Without this, retried
                    // responses leak connections until GC finalizes them.
                    response?.Dispose();

                    MgxTelemetryCollector.Current.RecordRetry(
                        isThrottle: status == (HttpStatusCode)429,
                        delayMs: (long)retryDelay.TotalMilliseconds);

                    if (!args.Context.Properties.TryGetValue(VerboseWriterKey, out var writer) || writer == null)
                        return default;

                    // Timeout retries have no HTTP response - log a distinct message
                    if (args.Outcome.Exception is TimeoutRejectedException tre)
                    {
                        writer($"Retry attempt {attempt}: per-attempt timeout ({tre.Timeout.TotalSeconds:F0}s) exceeded, waiting {retryDelay.TotalSeconds:F1}s");
                        return default;
                    }

                    if (retryAfter is RetryConditionHeaderValue ra)
                    {
                        TimeSpan? serverRequested = ra.Delta
                            ?? (ra.Date.HasValue ? ra.Date.Value - DateTimeOffset.UtcNow : null);
                        if (serverRequested.HasValue &&
                            serverRequested.Value > TimeSpan.FromSeconds(maxRetryAfterCap))
                        {
                            writer($"Retry attempt {attempt}: server requested {serverRequested.Value.TotalSeconds:F0}s delay (clamped to {maxRetryAfterCap}s). Status: {(int?)status}");
                            return default;
                        }
                    }

                    writer($"Retry attempt {attempt}: waiting {retryDelay.TotalSeconds:F1}s. Status: {(int?)status}");
                    return default;
                }
            })
            // Circuit breaker. What counts as a circuit failure - and why 429 and 408
            // deliberately do not - lives in MgxErrorPolicy.CountsAsCircuitFailure,
            // beside the retry policy it diverges from.
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
            {
                FailureRatio = options.CircuitBreakerFailureRatio,
                SamplingDuration = TimeSpan.FromSeconds(options.CircuitBreakerSamplingDurationSeconds),
                MinimumThroughput = options.CircuitBreakerMinThroughput,
                BreakDuration = TimeSpan.FromSeconds(options.CircuitBreakerDurationSeconds),
                ShouldHandle = args =>
                {
                    var info = args.Outcome.Result is { } response
                        ? MgxErrorClassifier.Classify(response)
                        : args.Outcome.Exception is { } ex and not BrokenCircuitException
                            ? MgxErrorClassifier.Classify(ex,
                                (ex as OperationCanceledException)?.CancellationToken.IsCancellationRequested ?? false)
                            : new MgxErrorInfo(MgxErrorClass.Permanent, 0);
                    return ValueTask.FromResult(MgxErrorPolicy.CountsAsCircuitFailure(info));
                },
                OnOpened = _ =>
                {
                    MgxTelemetryCollector.Current.RecordCircuitBreakerTrip();
                    return default;
                }
            })
            // Per-attempt timeout
            .AddTimeout(TimeSpan.FromSeconds(options.AttemptTimeoutSeconds))
            .Build();
    }
}
