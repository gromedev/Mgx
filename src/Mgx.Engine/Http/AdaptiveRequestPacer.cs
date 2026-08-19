using System.Diagnostics;
using System.Globalization;

namespace Mgx.Engine.Http;

/// <summary>
/// Process-wide proactive request pacer. Spaces outgoing requests *before* the token-bucket
/// lease so the process backs off ahead of Graph's throttle instead of only reacting to 429s.
/// State is partitioned by <see cref="WorkloadBucket"/> so a throttle on one service never
/// slows an unrelated workload in the same process.
///
/// Three mechanisms, all off (zero delay) in steady clean state:
///  - AIMD cap: a 429 caps the bucket's rate (entry 4 rps, halving on repeat, floor 2 rps);
///    additive recovery once per clean second; the cap expires after a quiet
///    AdaptiveRecoveryWindow, matching GraphBatchClient semantics.
///  - Slow start: a cold bucket (first use, or quiet for AdaptiveRecoveryWindow) opens with a
///    conservative rate *cap* that doubles each clean second until it reaches the ceiling.
///    Cap-based, never delay-based: request #1 is never delayed; the cap only bites when
///    demand exceeds it, i.e. fan-outs - the "throttled before a single item" case.
///  - Proximity damping: when Graph reports x-ms-throttle-limit-percentage >= 0.8, a
///    per-request delay ramps linearly to its maximum at 1.2, while the header stays fresh.
///
/// A 429's Retry-After additionally pushes the whole bucket's next send slot forward, so
/// concurrent callers hold off together instead of each discovering the throttle separately.
///
/// Polly's DelayGenerator stays reactive-per-request (delays retries of a failed request);
/// this pacer is proactive-inter-request. They compose without double-delaying. Batch outer
/// POSTs skip the gate (GraphBatchClient owns batch throughput) but still feed signal state.
/// </summary>
internal static class AdaptiveRequestPacer
{
    // --- tuning constants ---

    /// <summary>Cap applied on the first 429 in a previously uncapped bucket.</summary>
    internal const int ThrottledEntryRate = 4;

    /// <summary>Opening rate cap for a cold bucket (slow start).</summary>
    internal const int SlowStartInitialRate = 4;

    /// <summary>Clean interval after which caps ramp (recovery / slow-start doubling).</summary>
    internal static readonly TimeSpan RampInterval = TimeSpan.FromSeconds(1);

    /// <summary>How long a reported throttle percentage stays authoritative.</summary>
    internal static readonly TimeSpan PercentageFreshness = TimeSpan.FromSeconds(30);

    /// <summary>Damping starts at 0.8 (per mille: 800) - the documented emission floor.</summary>
    internal const int DampingStartPerMille = 800;

    /// <summary>Damping reaches its maximum at 1.2 (20% of requests already throttled).</summary>
    internal const int DampingFullPerMille = 1200;

    /// <summary>Per-request delay at or above DampingFullPerMille.</summary>
    internal const int DampingMaxDelayMs = 2000;

    private const int Buckets = AdaptivePacing.WorkloadBucketCount;

    // --- configuration (set via Configure, from ResiliencePipelineFactory.GetOrCreate) ---

    /// <summary>
    /// Test seam. The integration suite disables the gate process-wide via a
    /// [ModuleInitializer] - hundreds of pre-pacing tests would otherwise inherit slow-start
    /// spacing and multiply suite time - and pacer tests re-enable it locally (serially, in
    /// the Pipeline collection). Never set from production code.
    /// </summary>
    internal static volatile bool DisabledForTests;

    private static volatile bool s_enabled = true;
    private static volatile int s_ceilingRate = 50;      // full-recovery target (rps)
    private static volatile int s_maxDelayMs = 120_000;  // MaxRetryAfterSeconds clamp

    // --- per-bucket state ---

    private static readonly object[] s_stateLocks = CreateLocks();
    private static readonly long[] s_nextSlotTicks = new long[Buckets];
    private static readonly long[] s_lastRequestTicks = new long[Buckets];
    private static readonly long[] s_lastThrottleTicks = new long[Buckets];
    private static readonly long[] s_lastRampTicks = new long[Buckets];
    private static readonly int[] s_adaptedRate = new int[Buckets];    // 0 = inactive
    private static readonly int[] s_slowStartRate = new int[Buckets];  // 0 = inactive
    private static readonly int[] s_lastPercentagePerMille = InitPercentages();
    private static readonly long[] s_lastPercentageTicks = new long[Buckets];
    private static readonly long[] s_latencyBaselineMs = new long[Buckets]; // EMA, 0 = no data
    private static readonly long[] s_lastLatencyMs = new long[Buckets];

    private static object[] CreateLocks()
    {
        var locks = new object[Buckets];
        for (var i = 0; i < Buckets; i++) locks[i] = new object();
        return locks;
    }

    private static int[] InitPercentages()
    {
        var p = new int[Buckets];
        Array.Fill(p, -1);
        return p;
    }

    /// <summary>
    /// Apply option-derived configuration. Called from ResiliencePipelineFactory.GetOrCreate,
    /// the chokepoint every client build passes through, so Set-MgxOption changes take effect
    /// on the next cmdlet invocation like every other option.
    /// </summary>
    internal static void Configure(ResilientGraphClientOptions options)
    {
        s_enabled = !options.NoAdaptivePacing;
        // The HTTP token bucket is the hard backstop; the pacer only ever caps *below* it.
        // With the bucket disabled there is no configured rate to recover toward, so keep
        // the default ceiling rather than inventing one.
        s_ceilingRate = options.NoRateLimit ? 50 : options.RateLimitPerSecond;
        s_maxDelayMs = options.MaxRetryAfterSeconds * 1000;
    }

    /// <summary>Clears all learned state. Called on credential change
    /// (ResiliencePipelineFactory.Reset) and from test setup.</summary>
    internal static void Reset()
    {
        for (var i = 0; i < Buckets; i++)
        {
            lock (s_stateLocks[i])
            {
                s_nextSlotTicks[i] = 0;
                s_lastRequestTicks[i] = 0;
                s_lastThrottleTicks[i] = 0;
                s_lastRampTicks[i] = 0;
                s_adaptedRate[i] = 0;
                s_slowStartRate[i] = 0;
                s_lastPercentagePerMille[i] = -1;
                s_lastPercentageTicks[i] = 0;
                s_latencyBaselineMs[i] = 0;
                s_lastLatencyMs[i] = 0;
            }
        }
        // Configuration is deliberately NOT reverted here. Reset clears LEARNED state - adapted
        // caps, slow start, gauges, baselines. Resetting s_enabled/s_ceilingRate/s_maxDelayMs to
        // defaults re-enabled pacing under a client built with NoAdaptivePacing, which then
        // recorded activations until the next cmdlet invocation happened to re-Configure. The
        // exposed window was in-flight fan-outs and parallel runspaces, where no re-Configure
        // intervenes. Callers changing configuration call Configure; it is not Reset's business.
    }

    // --- pure math (the testable core) ---

    /// <summary>Effective rate cap from the two cap sources. 0 = uncapped.</summary>
    internal static int EffectiveRateCap(int adaptedRate, int slowStartRate)
    {
        if (adaptedRate > 0 && slowStartRate > 0) return Math.Min(adaptedRate, slowStartRate);
        return adaptedRate > 0 ? adaptedRate : slowStartRate;
    }

    /// <summary>
    /// Per-request delay from the last reported throttle percentage. Zero below the documented
    /// 0.8 emission floor or when the report has gone stale; linear ramp to the maximum at 1.2.
    /// </summary>
    internal static long DampingDelayMs(int perMille, long ageTicks)
    {
        if (perMille < DampingStartPerMille) return 0;
        if (ageTicks > (long)(PercentageFreshness.TotalSeconds * Stopwatch.Frequency)) return 0;

        var span = DampingFullPerMille - DampingStartPerMille;
        var over = Math.Min(perMille - DampingStartPerMille, span);
        return DampingMaxDelayMs * over / span;
    }

    /// <summary>Inter-request spacing in Stopwatch ticks: the stricter of the rate cap's
    /// interval and the damping delay. 0 = no spacing.</summary>
    internal static long ComputeIntervalTicks(int rateCap, long dampingDelayMs)
    {
        var capTicks = rateCap > 0 ? Stopwatch.Frequency / rateCap : 0;
        var dampTicks = dampingDelayMs > 0 ? dampingDelayMs * Stopwatch.Frequency / 1000 : 0;
        return Math.Max(capTicks, dampTicks);
    }

    // --- the gate ---

    /// <summary>
    /// Waits until this request may be sent, per the bucket's current pacing state.
    /// Returns the milliseconds actually waited (0 on the fast path). The caller records
    /// telemetry and verbose output so messages land in the right cmdlet's stream.
    /// </summary>
    internal static async ValueTask<long> WaitAsync(WorkloadBucket bucket, CancellationToken cancellationToken)
    {
        // Batch envelopes are exempt by construction - GraphBatchClient passes paceGate: false
        // and runs its own item-level AIMD. Guarding here as well means a batch can never claim
        // a slot even if a future caller forgets the flag, and keeps two AIMD controllers from
        // compounding their backoff on one workload.
        if (bucket == WorkloadBucket.Batch) return 0;

        if (!s_enabled || DisabledForTests) return 0;

        var b = (int)bucket;
        long intervalTicks;

        lock (s_stateLocks[b])
        {
            var now = Stopwatch.GetTimestamp();

            // Expire an adapted cap after a quiet recovery window.
            if (s_adaptedRate[b] > 0 && AdaptivePacing.AdaptedRateHasExpired(s_lastThrottleTicks[b], now))
                s_adaptedRate[b] = 0;

            // Cold bucket (first use or quiet period): enter slow start. An active adapted
            // cap wins over slow start, so don't stack one on top of the other.
            var quietTicks = now - s_lastRequestTicks[b];
            var cold = s_lastRequestTicks[b] == 0
                || quietTicks > (long)(AdaptivePacing.AdaptiveRecoveryWindow.TotalSeconds * Stopwatch.Frequency);
            if (cold && s_adaptedRate[b] == 0)
            {
                // Clamp to the ceiling, as the throttle path already does. Without it, a caller
                // configuring -RateLimitPerSecond 1..3 (values the tuning help recommends) got a
                // slow-start cap of 4 sitting ABOVE their configured rate, and telemetry
                // reporting "slow-start 4 rps" against a 2 rps ceiling.
                s_slowStartRate[b] = Math.Min(SlowStartInitialRate, s_ceilingRate);
                s_lastRampTicks[b] = now;
            }

            // Ramp caps once per clean interval: additive recovery for the adapted cap,
            // doubling for slow start. A cap that reaches the ceiling deactivates.
            var rampTicks = (long)(RampInterval.TotalSeconds * Stopwatch.Frequency);
            if (now - s_lastRampTicks[b] >= rampTicks)
            {
                if (s_adaptedRate[b] > 0 && s_lastThrottleTicks[b] < s_lastRampTicks[b])
                {
                    var next = AdaptivePacing.RecoverRate(s_adaptedRate[b], s_ceilingRate);
                    s_adaptedRate[b] = next >= s_ceilingRate ? 0 : next;
                }
                if (s_slowStartRate[b] > 0)
                {
                    var next = s_slowStartRate[b] * 2;
                    s_slowStartRate[b] = next >= s_ceilingRate ? 0 : next;
                }
                s_lastRampTicks[b] = now;
            }

            s_lastRequestTicks[b] = now;

            var cap = EffectiveRateCap(s_adaptedRate[b], s_slowStartRate[b]);
            var damping = DampingDelayMs(s_lastPercentagePerMille[b], now - s_lastPercentageTicks[b]);
            intervalTicks = ComputeIntervalTicks(cap, damping);
        }

        // Fast path: nothing active, but a Retry-After push may still hold the bucket.
        long targetTicks;
        long claimNow;
        while (true)
        {
            var next = Interlocked.Read(ref s_nextSlotTicks[b]);
            claimNow = Stopwatch.GetTimestamp();
            targetTicks = Math.Max(claimNow, next);
            if (intervalTicks == 0 && next <= claimNow)
                return 0; // no spacing and no pending hold - don't advance the slot
            var newNext = targetTicks + intervalTicks;
            if (Interlocked.CompareExchange(ref s_nextSlotTicks[b], newNext, next) == next)
                break;
        }

        var delayTicks = targetTicks - claimNow;
        if (delayTicks <= 0) return 0;

        // Honour the claimed slot in full. Clamping the sleep here while still advancing the
        // slot by the whole interval was a synchronised-burst generator: every claimant whose
        // turn fell past the clamp woke at exactly the clamp and sent together, and the slot ran
        // away ahead of any wait that would ever be served. That fires a burst precisely when
        // the bucket is most oversubscribed - the opposite of the mechanism's purpose.
        //
        // Unbounded waiting is not the risk it looks like. The slot advances by at most one
        // interval per request, and the interval is bounded by MinAdaptiveRate (2 rps => 500 ms)
        // plus DampingMaxDelayMs (2 s). A server Retry-After is clamped to s_maxDelayMs where it
        // is applied, in RecordThrottle, so a hostile or absurd Retry-After still cannot push the
        // slot beyond the horizon. The wait is cancellable, which is the real escape hatch.
        //
        // s_maxDelayMs derives from MaxRetryAfterSeconds - a cap on how long to honour ONE
        // server response. Reusing it as a queue-depth cap conflated two different limits.
        var delayMs = delayTicks * 1000 / Stopwatch.Frequency;
        if (delayMs <= 0) return 0;

        await Task.Delay((int)delayMs, cancellationToken);
        return delayMs;
    }

    // --- signal recording ---

    /// <summary>
    /// Record a 429 for the bucket: cap the rate (entry rate on first throttle, halving on
    /// repeat) and, when the server sent Retry-After, push the whole bucket's next send slot
    /// past it so concurrent callers hold off together. Called from the shared pipeline's
    /// OnRetry (before the throttled response is disposed) and on a final 429.
    /// </summary>
    internal static void RecordThrottle(WorkloadBucket bucket, TimeSpan? retryAfter)
    {
        if (!s_enabled || DisabledForTests) return;

        var b = (int)bucket;
        lock (s_stateLocks[b])
        {
            var now = Stopwatch.GetTimestamp();
            s_lastThrottleTicks[b] = now;
            // Both branches clamp to the ceiling. Only the entry branch used to, so a REPEAT
            // throttle could raise the cap: ReduceRate floors at MinAdaptiveRate (2), so with
            // -RateLimitPerSecond 1 - a value the tuning help recommends verbatim - the second
            // 429 moved the cap from 1 to 2, halved the spacing, and printed the
            // self-contradictory "capped 2/1 rps". A throttle must never widen the gate.
            var reduced = s_adaptedRate[b] > 0
                ? AdaptivePacing.ReduceRate(s_adaptedRate[b])
                : ThrottledEntryRate;
            s_adaptedRate[b] = Math.Min(reduced, s_ceilingRate);
            s_slowStartRate[b] = 0; // the adapted cap governs from here
            s_lastRampTicks[b] = now;
        }

        if (retryAfter is { } ra && ra > TimeSpan.Zero)
        {
            var holdTicks = (long)(Math.Min(ra.TotalMilliseconds, s_maxDelayMs) / 1000.0 * Stopwatch.Frequency);
            var target = Stopwatch.GetTimestamp() + holdTicks;
            while (true)
            {
                var next = Interlocked.Read(ref s_nextSlotTicks[(int)bucket]);
                if (next >= target) break;
                if (Interlocked.CompareExchange(ref s_nextSlotTicks[(int)bucket], target, next) == next)
                    break;
            }
        }
    }

    /// <summary>
    /// Record the final response for a request: throttle-proximity percentage (fed into
    /// damping and surfaced as a telemetry gauge) and a final 429 that exhausted its retries
    /// (OnRetry never fires for the last attempt).
    /// </summary>
    internal static void RecordResponse(WorkloadBucket bucket, HttpResponseMessage response)
    {
        if (!s_enabled || DisabledForTests) return;

        if (response.Headers.TryGetValues("x-ms-throttle-limit-percentage", out var pctValues)
            && double.TryParse(pctValues.FirstOrDefault(), NumberStyles.Any, CultureInfo.InvariantCulture, out var pct)
            && pct > 0)
        {
            var b = (int)bucket;
            Volatile.Write(ref s_lastPercentagePerMille[b], (int)(pct * 1000));
            Volatile.Write(ref s_lastPercentageTicks[b], Stopwatch.GetTimestamp());
        }

        if ((int)response.StatusCode == 429)
        {
            TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta
                ?? (response.Headers.RetryAfter?.Date is { } d ? d - DateTimeOffset.UtcNow : null);
            RecordThrottle(bucket, retryAfter);
        }
    }

    /// <summary>
    /// Record transport latency for the bucket (per attempt, network time only). Maintains a
    /// slow EMA baseline so the SPO soft-clamp - latency stretching 5x+ with no 429s and no
    /// headers - is visible in telemetry. Telemetry-only in 2.1; not a pacing input.
    /// </summary>
    internal static void RecordLatency(WorkloadBucket bucket, long httpMs)
    {
        if (httpMs <= 0 || !s_enabled || DisabledForTests) return;
        var b = (int)bucket;
        lock (s_stateLocks[b])
        {
            s_lastLatencyMs[b] = httpMs;
            s_latencyBaselineMs[b] = s_latencyBaselineMs[b] == 0
                ? httpMs
                : s_latencyBaselineMs[b] + (httpMs - s_latencyBaselineMs[b]) / 8;
        }
    }

    // --- telemetry surface ---

    /// <summary>Most recently reported throttle percentage across all buckets (raw ratio,
    /// e.g. 0.85), or -1 when never seen.</summary>
    internal static double LastThrottlePercentage
    {
        get
        {
            long bestTicks = 0;
            var best = -1;
            for (var i = 0; i < Buckets; i++)
            {
                var t = Volatile.Read(ref s_lastPercentageTicks[i]);
                if (t > bestTicks)
                {
                    bestTicks = t;
                    best = Volatile.Read(ref s_lastPercentagePerMille[i]);
                }
            }
            return best < 0 ? -1 : best / 1000.0;
        }
    }

    /// <summary>
    /// Human-readable summary of active pacing state per bucket, or null when nothing is
    /// active and no latency data exists. Rendered by Get-MgxTelemetry.
    /// </summary>
    internal static string? DescribeState()
    {
        var parts = new List<string>();
        var now = Stopwatch.GetTimestamp();
        for (var i = 0; i < Buckets; i++)
        {
            var name = ((WorkloadBucket)i).ToString().ToLowerInvariant();
            List<string>? facts = null;
            lock (s_stateLocks[i])
            {
                if (s_adaptedRate[i] > 0)
                {
                    var ago = (now - s_lastThrottleTicks[i]) / Stopwatch.Frequency;
                    (facts ??= []).Add($"capped {s_adaptedRate[i]}/{s_ceilingRate} rps (last 429 {ago}s ago)");
                }
                if (s_slowStartRate[i] > 0)
                    (facts ??= []).Add($"slow-start {s_slowStartRate[i]} rps");
                if (s_lastPercentagePerMille[i] >= DampingStartPerMille
                    && now - s_lastPercentageTicks[i] <= (long)(PercentageFreshness.TotalSeconds * Stopwatch.Frequency))
                    (facts ??= []).Add($"proximity {s_lastPercentagePerMille[i] / 10}%");
                if (s_latencyBaselineMs[i] > 0)
                {
                    var ratio = (double)s_lastLatencyMs[i] / s_latencyBaselineMs[i];
                    (facts ??= []).Add($"latency {s_lastLatencyMs[i]}ms ({ratio:0.0}x of {s_latencyBaselineMs[i]}ms baseline)");
                }
            }
            if (facts != null)
                parts.Add($"{name}: {string.Join(", ", facts)}");
        }
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    // --- test accessors ---

    internal static int GetAdaptedRate(WorkloadBucket bucket) => Volatile.Read(ref s_adaptedRate[(int)bucket]);
    internal static int GetSlowStartRate(WorkloadBucket bucket) => Volatile.Read(ref s_slowStartRate[(int)bucket]);
    internal static long GetNextSlotTicks(WorkloadBucket bucket) => Interlocked.Read(ref s_nextSlotTicks[(int)bucket]);
    internal static bool Enabled => s_enabled;
}
