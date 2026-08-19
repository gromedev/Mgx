using System.Diagnostics;
using System.Net;
using Mgx.Engine.Http;

namespace Mgx.IntegrationTests.Engine;

/// <summary>
/// The request-level pacer: pure math (damping, caps, intervals) plus the stateful behaviors
/// - slow start on cold buckets, AIMD capping on 429, Retry-After propagation, bucket
/// isolation - and the two integration guarantees: both SendAsync paths are gated, and batch
/// outer POSTs are not.
///
/// Stateful tests run inside PacerScope (which re-enables the gate the suite-wide
/// ModuleInitializer turned off) and in the Pipeline collection so nothing runs concurrently
/// with them against the process-static state.
/// </summary>
[Collection("Pipeline")]
public class AdaptiveRequestPacerTests
{
    /// <summary>Re-enables the pacer for one test; restores the suite default on dispose.</summary>
    private sealed class PacerScope : IDisposable
    {
        public PacerScope()
        {
            AdaptiveRequestPacer.Reset();
            // Reset clears learned state only - it deliberately no longer reverts configuration,
            // because doing so re-enabled pacing under a client built with NoAdaptivePacing. So
            // the scope must restore defaults explicitly, or a test that configured a low
            // ceiling or disabled pacing leaks that setting into every later test in the run.
            AdaptiveRequestPacer.Configure(ResilientGraphClientOptions.Default);
            AdaptiveRequestPacer.DisabledForTests = false;
        }

        public void Dispose()
        {
            AdaptiveRequestPacer.Reset();
            AdaptiveRequestPacer.Configure(ResilientGraphClientOptions.Default);
            AdaptiveRequestPacer.DisabledForTests = true;
        }
    }

    // --- pure math ---

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(10, 0, 10)]
    [InlineData(0, 8, 8)]
    [InlineData(10, 8, 8)]
    [InlineData(4, 16, 4)]
    public void EffectiveRateCap_takes_the_stricter_active_cap(int adapted, int slowStart, int expected) =>
        Assert.Equal(expected, AdaptiveRequestPacer.EffectiveRateCap(adapted, slowStart));

    [Theory]
    [InlineData(790, 0)]     // below the documented 0.8 emission floor: no damping
    [InlineData(800, 0)]     // ramp starts AT 0.8, so 0.8 itself is zero delay
    [InlineData(1000, 1000)] // 1.0 = throttling begins: half the maximum
    [InlineData(1200, 2000)] // 1.2 = full damping
    [InlineData(1800, 2000)] // clamped above 1.2
    public void DampingDelay_ramps_linearly_from_documented_emission_floor(int perMille, long expectedMs) =>
        Assert.Equal(expectedMs, AdaptiveRequestPacer.DampingDelayMs(perMille, ageTicks: 0));

    [Fact]
    public void DampingDelay_ignores_stale_reports()
    {
        var staleTicks = (long)((AdaptiveRequestPacer.PercentageFreshness.TotalSeconds + 1) * Stopwatch.Frequency);
        Assert.Equal(0, AdaptiveRequestPacer.DampingDelayMs(1200, staleTicks));
    }

    [Fact]
    public void ComputeInterval_zero_when_nothing_is_active() =>
        Assert.Equal(0, AdaptiveRequestPacer.ComputeIntervalTicks(rateCap: 0, dampingDelayMs: 0));

    [Fact]
    public void ComputeInterval_takes_the_stricter_of_cap_and_damping()
    {
        // 10 rps = 100ms interval; 500ms damping is stricter.
        var ticks = AdaptiveRequestPacer.ComputeIntervalTicks(rateCap: 10, dampingDelayMs: 500);
        Assert.Equal(500 * Stopwatch.Frequency / 1000, ticks);

        // 2 rps = 500ms interval; 100ms damping is weaker.
        ticks = AdaptiveRequestPacer.ComputeIntervalTicks(rateCap: 2, dampingDelayMs: 100);
        Assert.Equal(Stopwatch.Frequency / 2, ticks);
    }

    // --- stateful behavior ---

    [Fact]
    public async Task First_request_of_a_cold_bucket_is_never_delayed()
    {
        using var scope = new PacerScope();

        var waited = await AdaptiveRequestPacer.WaitAsync(WorkloadBucket.Directory, CancellationToken.None);

        Assert.Equal(0, waited);
        // ...but slow start is now armed for the requests that follow.
        Assert.Equal(AdaptiveRequestPacer.SlowStartInitialRate,
            AdaptiveRequestPacer.GetSlowStartRate(WorkloadBucket.Directory));
    }

    [Fact]
    public async Task Slow_start_spaces_an_immediate_burst()
    {
        using var scope = new PacerScope();

        await AdaptiveRequestPacer.WaitAsync(WorkloadBucket.Directory, CancellationToken.None);
        var second = await AdaptiveRequestPacer.WaitAsync(WorkloadBucket.Directory, CancellationToken.None);

        // At the 4 rps opening cap the second request waits ~250ms.
        Assert.True(second > 0, $"second request should have been paced, waited {second}ms");
    }

    [Fact]
    public void Throttle_enters_the_cap_at_the_entry_rate_then_halves_to_the_floor()
    {
        using var scope = new PacerScope();

        AdaptiveRequestPacer.RecordThrottle(WorkloadBucket.Directory, retryAfter: null);
        Assert.Equal(AdaptiveRequestPacer.ThrottledEntryRate,
            AdaptiveRequestPacer.GetAdaptedRate(WorkloadBucket.Directory));

        AdaptiveRequestPacer.RecordThrottle(WorkloadBucket.Directory, retryAfter: null);
        Assert.Equal(AdaptivePacing.ReduceRate(AdaptiveRequestPacer.ThrottledEntryRate),
            AdaptiveRequestPacer.GetAdaptedRate(WorkloadBucket.Directory));

        // Repeat throttles never fall below the floor.
        AdaptiveRequestPacer.RecordThrottle(WorkloadBucket.Directory, retryAfter: null);
        Assert.Equal(AdaptivePacing.MinAdaptiveRate,
            AdaptiveRequestPacer.GetAdaptedRate(WorkloadBucket.Directory));
    }

    [Fact]
    public async Task Throttle_cancels_slow_start_and_the_adapted_cap_governs()
    {
        using var scope = new PacerScope();

        await AdaptiveRequestPacer.WaitAsync(WorkloadBucket.Drive, CancellationToken.None);
        Assert.True(AdaptiveRequestPacer.GetSlowStartRate(WorkloadBucket.Drive) > 0);

        AdaptiveRequestPacer.RecordThrottle(WorkloadBucket.Drive, retryAfter: null);

        Assert.Equal(0, AdaptiveRequestPacer.GetSlowStartRate(WorkloadBucket.Drive));
        Assert.Equal(AdaptiveRequestPacer.ThrottledEntryRate,
            AdaptiveRequestPacer.GetAdaptedRate(WorkloadBucket.Drive));
    }

    [Fact]
    public void RetryAfter_pushes_the_whole_buckets_next_slot()
    {
        using var scope = new PacerScope();

        var before = Stopwatch.GetTimestamp();
        AdaptiveRequestPacer.RecordThrottle(WorkloadBucket.Directory, TimeSpan.FromSeconds(3));

        var slot = AdaptiveRequestPacer.GetNextSlotTicks(WorkloadBucket.Directory);
        // The hold lands ~3s out; assert at least 2s to stay timing-tolerant.
        Assert.True(slot > before + 2 * Stopwatch.Frequency,
            "Retry-After should hold the whole bucket, not just the throttled request");
    }

    [Fact]
    public void Buckets_are_isolated_a_throttle_on_one_never_caps_another()
    {
        using var scope = new PacerScope();

        AdaptiveRequestPacer.RecordThrottle(WorkloadBucket.Other, retryAfter: null); // e.g. Teams

        Assert.True(AdaptiveRequestPacer.GetAdaptedRate(WorkloadBucket.Other) > 0);
        Assert.Equal(0, AdaptiveRequestPacer.GetAdaptedRate(WorkloadBucket.Directory));
        Assert.Equal(0, AdaptiveRequestPacer.GetAdaptedRate(WorkloadBucket.Drive));
    }

    [Fact]
    public void RecordResponse_captures_the_throttle_percentage_gauge()
    {
        using var scope = new PacerScope();

        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("x-ms-throttle-limit-percentage", "0.95");

        AdaptiveRequestPacer.RecordResponse(WorkloadBucket.Directory, response);

        Assert.Equal(0.95, AdaptiveRequestPacer.LastThrottlePercentage, precision: 3);
        var state = AdaptiveRequestPacer.DescribeState();
        Assert.NotNull(state);
        Assert.Contains("proximity 95%", state);
    }

    [Fact]
    public async Task Proximity_gauge_paces_requests_beyond_what_the_rate_cap_alone_would()
    {
        // The gauge is opportunistic - Graph emits x-ms-throttle-limit-percentage rarely, so it
        // may never be seen live - but the wiring from RecordResponse into the gate must work
        // when a header does arrive. Nothing else in the suite drives a reported percentage
        // through WaitAsync, so severing damping from the gate is otherwise invisible.
        using var scope = new PacerScope();

        // Cold-bucket slow start is the only other spacing source here and it caps at 4 rps,
        // i.e. a 250ms interval. A reported 1.0 ("throttling has begun") sits halfway up the
        // damping ramp: 1000ms, four times anything the cap can produce.
        await AdaptiveRequestPacer.WaitAsync(WorkloadBucket.Directory, CancellationToken.None);

        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("x-ms-throttle-limit-percentage", "1.0");
        AdaptiveRequestPacer.RecordResponse(WorkloadBucket.Directory, response);

        // The interval computed for a request governs the slot the *next* one claims, so the
        // damped spacing is observable on the request after the first post-header send.
        await AdaptiveRequestPacer.WaitAsync(WorkloadBucket.Directory, CancellationToken.None);
        var damped = await AdaptiveRequestPacer.WaitAsync(WorkloadBucket.Directory, CancellationToken.None);

        Assert.True(damped > 600,
            "a request sent while Graph reports it is at the throttle limit must be spaced by the "
            + $"proximity damping (~1000ms), well past the 250ms the 4 rps cap alone gives; waited {damped}ms");
    }

    [Fact]
    public void RecordResponse_treats_a_final_429_as_a_throttle()
    {
        using var scope = new PacerScope();

        // OnRetry never fires for the last attempt, so a 429 that survives its retries is
        // only observable on the final response.
        using var response = new HttpResponseMessage((HttpStatusCode)429);
        response.Headers.TryAddWithoutValidation("Retry-After", "2");

        AdaptiveRequestPacer.RecordResponse(WorkloadBucket.Directory, response);

        Assert.Equal(AdaptiveRequestPacer.ThrottledEntryRate,
            AdaptiveRequestPacer.GetAdaptedRate(WorkloadBucket.Directory));
    }

    [Fact]
    public async Task NoAdaptivePacing_disables_the_gate_and_signal_recording()
    {
        using var scope = new PacerScope();
        AdaptiveRequestPacer.Configure(new ResilientGraphClientOptions { NoAdaptivePacing = true });

        var first = await AdaptiveRequestPacer.WaitAsync(WorkloadBucket.Directory, CancellationToken.None);
        var second = await AdaptiveRequestPacer.WaitAsync(WorkloadBucket.Directory, CancellationToken.None);
        AdaptiveRequestPacer.RecordThrottle(WorkloadBucket.Directory, TimeSpan.FromSeconds(60));

        Assert.Equal(0, first);
        Assert.Equal(0, second);
        Assert.Equal(0, AdaptiveRequestPacer.GetAdaptedRate(WorkloadBucket.Directory));
    }

    [Fact]
    public void Reset_clears_learned_state()
    {
        using var scope = new PacerScope();

        AdaptiveRequestPacer.RecordThrottle(WorkloadBucket.Directory, TimeSpan.FromSeconds(30));
        Assert.True(AdaptiveRequestPacer.GetAdaptedRate(WorkloadBucket.Directory) > 0);

        AdaptiveRequestPacer.Reset();

        Assert.Equal(0, AdaptiveRequestPacer.GetAdaptedRate(WorkloadBucket.Directory));
        Assert.Equal(0, AdaptiveRequestPacer.GetNextSlotTicks(WorkloadBucket.Directory));
        Assert.Equal(-1, AdaptiveRequestPacer.LastThrottlePercentage);
    }

    [Fact]
    public async Task Slow_start_cap_doubles_after_a_clean_interval()
    {
        using var scope = new PacerScope();

        await AdaptiveRequestPacer.WaitAsync(WorkloadBucket.Directory, CancellationToken.None);
        Assert.Equal(AdaptiveRequestPacer.SlowStartInitialRate,
            AdaptiveRequestPacer.GetSlowStartRate(WorkloadBucket.Directory));

        // RampInterval is 1s; sleep past it with margin, then the next request ramps the cap.
        await Task.Delay(1300);
        await AdaptiveRequestPacer.WaitAsync(WorkloadBucket.Directory, CancellationToken.None);

        Assert.Equal(AdaptiveRequestPacer.SlowStartInitialRate * 2,
            AdaptiveRequestPacer.GetSlowStartRate(WorkloadBucket.Directory));
    }

    [Fact]
    public async Task Adapted_cap_climbs_back_out_of_a_throttle_after_clean_intervals()
    {
        // The "AI" of AIMD. A 429 halves the bucket's rate; if the additive recovery step never
        // ran, one 429 would pace that workload forever and throughput would never return.
        using var scope = new PacerScope();
        // A 5 rps ceiling makes one additive step (4 + max(1, 5/10)) reach the ceiling, so a
        // recovered bucket becomes observable within two ramp intervals rather than the ten a
        // 50 rps ceiling would need.
        AdaptiveRequestPacer.Configure(new ResilientGraphClientOptions { RateLimitPerSecond = 5 });
        AdaptiveRequestPacer.DisabledForTests = false;

        AdaptiveRequestPacer.RecordThrottle(WorkloadBucket.Directory, retryAfter: null);

        // While the cap holds, back-to-back requests are spaced (4 rps => ~250ms).
        await AdaptiveRequestPacer.WaitAsync(WorkloadBucket.Directory, CancellationToken.None);
        var whileCapped = await AdaptiveRequestPacer.WaitAsync(WorkloadBucket.Directory, CancellationToken.None);
        Assert.True(whileCapped > 0, "a bucket capped by a 429 must still be pacing before it recovers");

        // Two clean ramp intervals. The first ramp after a throttle only re-arms the window -
        // the throttle shares its timestamp - and the second performs the additive increase.
        for (var i = 0; i < 2; i++)
        {
            await Task.Delay(1300);
            await AdaptiveRequestPacer.WaitAsync(WorkloadBucket.Directory, CancellationToken.None);
        }

        // Recovery is observable as restored throughput: a burst is no longer spaced at all.
        var afterRecovery = new long[3];
        for (var i = 0; i < afterRecovery.Length; i++)
            afterRecovery[i] = await AdaptiveRequestPacer.WaitAsync(WorkloadBucket.Directory, CancellationToken.None);

        Assert.True(afterRecovery.All(w => w == 0),
            "a bucket that stayed clean must climb back out of its post-429 cap, but requests are still "
            + $"being paced: [{string.Join(", ", afterRecovery)}] ms");
    }

    // --- integration: the two request paths and the batch exemption ---

    [Fact]
    public async Task SendAsync_is_gated_and_a_burst_is_paced()
    {
        using var scope = new PacerScope();
        ResiliencePipelineFactory.Reset();
        AdaptiveRequestPacer.DisabledForTests = false; // factory Reset re-cleared pacer state only

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, TestData.SingleUser);
        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        MgxTelemetryCollector.Current.Reset();
        await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");
        await client.GetAsync("https://graph.microsoft.com/v1.0/users/user2");
        await client.GetAsync("https://graph.microsoft.com/v1.0/users/user3");

        var summary = MgxTelemetryCollector.Current.GetSummary();
        Assert.True(summary.AdaptivePacingActivations >= 1,
            "an immediate 3-request burst on a cold bucket must trip the slow-start cap");
        Assert.True(summary.AdaptivePacingWaitMs > 0);
    }

    [Fact]
    public async Task A_429_through_SendAsync_feeds_the_pacer_with_the_right_bucket()
    {
        using var scope = new PacerScope();
        ResiliencePipelineFactory.Reset();
        AdaptiveRequestPacer.DisabledForTests = false;

        var handler = new MockHttpHandler();
        handler.QueueResponse((HttpStatusCode)429, null, new() { ["Retry-After"] = "0" });
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser);
        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(AdaptiveRequestPacer.ThrottledEntryRate,
            AdaptiveRequestPacer.GetAdaptedRate(WorkloadBucket.Directory));
        Assert.Equal(0, AdaptiveRequestPacer.GetAdaptedRate(WorkloadBucket.Drive));
    }

    [Fact]
    public async Task The_delegating_handler_path_is_gated_too()
    {
        // The Enable-MgxResilience bridge: SDK traffic bypasses ResilientGraphClient, so the
        // pacer must sit in ResilientDelegatingHandler as well or M365DSC-style consumers
        // get no pacing at all.
        using var scope = new PacerScope();
        ResiliencePipelineFactory.Reset();
        AdaptiveRequestPacer.DisabledForTests = false;

        var options = new ResilientGraphClientOptions { NoRateLimit = true };
        var (pipeline, rateLimiter) = ResiliencePipelineFactory.GetOrCreate(options);
        var mock = new MockHttpHandler();
        mock.SetDefaultResponse(HttpStatusCode.OK, TestData.SingleUser);
        using var resilient = new ResilientDelegatingHandler(pipeline, rateLimiter) { InnerHandler = mock };
        using var httpClient = new HttpClient(resilient);

        MgxTelemetryCollector.Current.Reset();
        await httpClient.GetAsync("https://graph.microsoft.com/v1.0/users/user1");
        await httpClient.GetAsync("https://graph.microsoft.com/v1.0/users/user2");
        await httpClient.GetAsync("https://graph.microsoft.com/v1.0/users/user3");

        var summary = MgxTelemetryCollector.Current.GetSummary();
        Assert.True(summary.AdaptivePacingActivations >= 1,
            "the SDK-bridge path must be paced exactly like ResilientGraphClient.SendAsync");

        // Telemetry parity. Reporting pacing activations and retries while TotalRequests stayed
        // 0 was self-contradictory, and made the throttle-rate formula in the Get-MgxTelemetry
        // help divide by zero on exactly the path M365DSC-style consumers use.
        Assert.Equal(3, summary.TotalRequests);
        Assert.Equal(3, summary.Succeeded);
        Assert.Equal(0, summary.Failed);
    }

    [Fact]
    public async Task Concurrent_waiters_stay_spaced_when_the_queue_exceeds_the_delay_cap()
    {
        // The slot is claimed at full interval but the sleep used to be clamped to
        // MaxRetryAfterSeconds, so every claimant queued past the clamp woke at the clamp and
        // sent simultaneously - a synchronised burst produced exactly when the bucket was most
        // oversubscribed, which is the opposite of what the gate is for.
        using var scope = new PacerScope();
        // NoRateLimit pins the ceiling at 50, so cold-start slow start caps at 4 rps => a 250 ms
        // interval. MaxRetryAfterSeconds = 1 sets the old 1000 ms sleep clamp, which 12 queued
        // claims (~2.75 s of slots) overshoot decisively.
        AdaptiveRequestPacer.Configure(new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            MaxRetryAfterSeconds = 1,
        });
        AdaptiveRequestPacer.DisabledForTests = false;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var waits = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(async _ =>
            {
                await AdaptiveRequestPacer.WaitAsync(WorkloadBucket.Directory, CancellationToken.None);
                return sw.ElapsedMilliseconds;
            }));

        var ordered = waits.OrderBy(x => x).ToArray();
        // Under the old clamp every claim past ~1000 ms woke at the clamp: eight of these twelve
        // would land inside one 200 ms window and send together.
        var clustered = ordered.Count(t => t > 900 && t < 1100);
        Assert.True(clustered <= 2,
            $"waiters bunched at the delay cap instead of staying spaced: [{string.Join(", ", ordered)}]");
        Assert.True(ordered[^1] >= 2000,
            $"the last waiter should honour its claimed slot (~2750ms), got {ordered[^1]}ms");
    }

    [Fact]
    public async Task Batch_bucket_never_claims_a_pacing_slot()
    {
        // GraphBatchClient owns batch throughput through its own item-level AIMD and passes
        // paceGate: false. The gate refuses the bucket as well, so a caller that forgets the
        // flag cannot accidentally stack two AIMD controllers on one workload. Before $batch
        // had its own bucket it classified as Other, and an outer 429 capped unrelated
        // Exchange, Teams and Intune traffic on evidence that had nothing to do with them.
        using var scope = new PacerScope();
        AdaptiveRequestPacer.DisabledForTests = false;

        var waited = await AdaptiveRequestPacer.WaitAsync(WorkloadBucket.Batch, CancellationToken.None);
        Assert.Equal(0, waited);
    }

    [Fact]
    public async Task Batch_outer_posts_skip_the_gate()
    {
        // GraphBatchClient owns batch throughput (cross-call pacing, item-level AIMD).
        // Pacing the outer POSTs too would stack two AIMD controllers on one workload.
        using var scope = new PacerScope();
        ResiliencePipelineFactory.Reset();
        AdaptiveRequestPacer.DisabledForTests = false;

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK,
            """{"responses":[{"id":"1","status":200,"body":{"id":"user1"}}]}""");
        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batch = new GraphBatchClient(client);

        MgxTelemetryCollector.Current.Reset();
        // Three back-to-back batch calls: gated traffic at this cadence would trip slow start.
        await batch.ExecuteBatchAsync(["https://graph.microsoft.com/v1.0/users/user1"]);
        await batch.ExecuteBatchAsync(["https://graph.microsoft.com/v1.0/users/user1"]);
        await batch.ExecuteBatchAsync(["https://graph.microsoft.com/v1.0/users/user1"]);

        var summary = MgxTelemetryCollector.Current.GetSummary();
        Assert.Equal(0, summary.AdaptivePacingActivations);
    }
}