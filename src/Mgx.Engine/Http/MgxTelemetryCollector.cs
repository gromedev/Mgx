namespace Mgx.Engine.Http;

/// <summary>
/// Session-lifetime telemetry aggregator for Graph API operations.
/// Thread-safe via Interlocked; accumulates across all cmdlet invocations in a session.
/// Reset via MgxTelemetryCollector.Current.Reset() or Get-MgxTelemetry -Reset.
/// </summary>
public sealed class MgxTelemetryCollector
{
    public static readonly MgxTelemetryCollector Current = new();

    private long _totalRequests;
    private long _succeeded;
    private long _failed;
    private long _throttleRetries;   // 429 retries
    private long _otherRetries;      // 5xx / network retries
    private long _cbTrips;           // Circuit breaker trips (OnBreak events)
    private long _rateLimiterWaitMs; // Time waiting in token bucket queue
    private long _retryDelayMs;      // Time in Polly retry delays (Retry-After / backoff)
    private long _httpMs;            // Time in _httpClient.SendAsync (actual network)
    private long _elapsedMs;         // Total wall-clock time in SendAsync (all phases)
    private long _resourceUnits;     // x-ms-resource-unit sum across all responses
    private long _batchItemThrottles; // Per-item 429s inside $batch responses (distinct from Polly-level _throttleRetries)
    private long _pacingWaitMs;      // Time spent in the adaptive pacer's proactive gate
    private long _pacingActivations; // Requests the pacer actually delayed (wait > 0)
    private long _contentBytes;      // Bytes downloaded via Get-MgxContent (both hops)

    public void RecordRequest(bool succeeded, long elapsedMs)
    {
        Interlocked.Increment(ref _totalRequests);
        if (succeeded)
            Interlocked.Increment(ref _succeeded);
        else
            Interlocked.Increment(ref _failed);
        Interlocked.Add(ref _elapsedMs, elapsedMs);
    }

    public void RecordHttpTime(long ms) =>
        Interlocked.Add(ref _httpMs, ms);

    public void RecordRateLimiterWait(long ms) =>
        Interlocked.Add(ref _rateLimiterWaitMs, ms);

    public void RecordRetry(bool isThrottle, long delayMs)
    {
        if (isThrottle)
            Interlocked.Increment(ref _throttleRetries);
        else
            Interlocked.Increment(ref _otherRetries);
        Interlocked.Add(ref _retryDelayMs, delayMs);
    }

    public void RecordCircuitBreakerTrip() =>
        Interlocked.Increment(ref _cbTrips);

    public void RecordResourceUnit(long units) =>
        Interlocked.Add(ref _resourceUnits, units);

    public void RecordBatchItemThrottles(int count) =>
        Interlocked.Add(ref _batchItemThrottles, count);

    /// <summary>
    /// Time spent in per-item retry delays inside $batch processing. Folded into
    /// RetryDelayMs so the session view reflects batch retry waits: without this,
    /// a batch-heavy session reports zero retry delay while spending hours in
    /// Retry-After sleeps that only per-call BatchTelemetry could see.
    /// </summary>
    public void RecordBatchRetryDelay(long ms) =>
        Interlocked.Add(ref _retryDelayMs, ms);

    /// <summary>A request the adaptive pacer held before sending.</summary>
    public void RecordPacingWait(long ms)
    {
        Interlocked.Increment(ref _pacingActivations);
        Interlocked.Add(ref _pacingWaitMs, ms);
    }

    public void RecordContentBytes(long bytes) =>
        Interlocked.Add(ref _contentBytes, bytes);

    public void Reset()
    {
        Interlocked.Exchange(ref _totalRequests, 0);
        Interlocked.Exchange(ref _succeeded, 0);
        Interlocked.Exchange(ref _failed, 0);
        Interlocked.Exchange(ref _throttleRetries, 0);
        Interlocked.Exchange(ref _otherRetries, 0);
        Interlocked.Exchange(ref _cbTrips, 0);
        Interlocked.Exchange(ref _rateLimiterWaitMs, 0);
        Interlocked.Exchange(ref _retryDelayMs, 0);
        Interlocked.Exchange(ref _httpMs, 0);
        Interlocked.Exchange(ref _elapsedMs, 0);
        Interlocked.Exchange(ref _resourceUnits, 0);
        Interlocked.Exchange(ref _batchItemThrottles, 0);
        Interlocked.Exchange(ref _pacingWaitMs, 0);
        Interlocked.Exchange(ref _pacingActivations, 0);
        Interlocked.Exchange(ref _contentBytes, 0);
        // Pacer control state (adapted rates, slow start) is deliberately NOT cleared here:
        // it describes the tenant's current throttle regime, not accumulated statistics.
    }

    public MgxTelemetrySummary GetSummary() => new(
        TotalRequests: Interlocked.Read(ref _totalRequests),
        Succeeded: Interlocked.Read(ref _succeeded),
        Failed: Interlocked.Read(ref _failed),
        ThrottleRetries: Interlocked.Read(ref _throttleRetries),
        OtherRetries: Interlocked.Read(ref _otherRetries),
        CircuitBreakerTrips: Interlocked.Read(ref _cbTrips),
        RateLimiterWaitMs: Interlocked.Read(ref _rateLimiterWaitMs),
        RetryDelayMs: Interlocked.Read(ref _retryDelayMs),
        HttpMs: Interlocked.Read(ref _httpMs),
        ElapsedMs: Interlocked.Read(ref _elapsedMs),
        ResourceUnitsConsumed: Interlocked.Read(ref _resourceUnits),
        BatchItemThrottles: Interlocked.Read(ref _batchItemThrottles),
        AdaptivePacingWaitMs: Interlocked.Read(ref _pacingWaitMs),
        AdaptivePacingActivations: Interlocked.Read(ref _pacingActivations),
        LastThrottlePercentage: AdaptiveRequestPacer.LastThrottlePercentage,
        PacingState: AdaptiveRequestPacer.DescribeState(),
        ContentBytesDownloaded: Interlocked.Read(ref _contentBytes));
}

/// <summary>
/// Snapshot of session telemetry from MgxTelemetryCollector.
/// </summary>
public sealed record MgxTelemetrySummary(
    long TotalRequests,
    long Succeeded,
    long Failed,
    long ThrottleRetries,
    long OtherRetries,
    long CircuitBreakerTrips,
    long RateLimiterWaitMs,
    long RetryDelayMs,
    long HttpMs,
    long ElapsedMs,
    long ResourceUnitsConsumed,
    long BatchItemThrottles,
    long AdaptivePacingWaitMs,
    long AdaptivePacingActivations,
    double LastThrottlePercentage,
    string? PacingState,
    long ContentBytesDownloaded);
