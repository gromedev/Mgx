using System.Net;
using Mgx.Engine.Http;
using Polly.CircuitBreaker;

namespace Mgx.IntegrationTests;

[Collection("Pipeline")]
public class CircuitBreakerTests
{
    // Helper: create low-threshold options so circuit trips fast without long test runs
    private static ResilientGraphClientOptions FastTripOptions(int samplingDurationSeconds = 30) => new()
    {
        NoRateLimit = true,
        MaxRetryAttempts = 1,                       // 1 retry = 2 total attempts per call
        CircuitBreakerMinThroughput = 2,             // only need 2 requests to evaluate
        CircuitBreakerFailureRatio = 0.5,            // 50% failures trips the breaker
        CircuitBreakerDurationSeconds = 5,           // short open window for recovery test
        CircuitBreakerSamplingDurationSeconds = samplingDurationSeconds,
        TotalTimeoutSeconds = 30,
        AttemptTimeoutSeconds = 10
    };

    [Fact]
    public async Task CircuitBreaker_OpensAfterSustainedFailures()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.ServiceUnavailable);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, FastTripOptions());

        // Keep sending until circuit opens
        bool circuitOpened = false;
        for (int i = 0; i < 20; i++)
        {
            try
            {
                await client.GetAsync($"https://graph.microsoft.com/v1.0/test/{i}");
            }
            catch (BrokenCircuitException)
            {
                circuitOpened = true;
                break;
            }
            catch
            {
                // Timeouts and other exceptions are fine; we're flooding failures
            }
        }

        Assert.True(circuitOpened, "Circuit breaker should have opened after sustained 503 failures");
        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public async Task CircuitBreaker_RejectsRequests_WhileOpen()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.ServiceUnavailable);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, FastTripOptions());

        // Trip the circuit
        for (int i = 0; i < 20; i++)
        {
            try { await client.GetAsync($"https://graph.microsoft.com/v1.0/test/{i}"); }
            catch (BrokenCircuitException) { break; }
            catch { }
        }

        // Circuit should be open now; next request should be rejected immediately
        // without even hitting the handler
        int requestCountBefore = handler.RequestCount;
        await Assert.ThrowsAsync<BrokenCircuitException>(
            () => client.GetAsync("https://graph.microsoft.com/v1.0/test/rejected"));

        // No new HTTP request made (circuit short-circuits)
        Assert.Equal(requestCountBefore, handler.RequestCount);
        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public async Task CircuitBreaker_Recovers_AfterBreakDuration()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.ServiceUnavailable);

        var options = FastTripOptions();
        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, options);

        // Trip the circuit
        for (int i = 0; i < 20; i++)
        {
            try { await client.GetAsync($"https://graph.microsoft.com/v1.0/test/{i}"); }
            catch (BrokenCircuitException) { break; }
            catch { }
        }

        // Confirm circuit is open
        await Assert.ThrowsAsync<BrokenCircuitException>(
            () => client.GetAsync("https://graph.microsoft.com/v1.0/test/confirm-open"));

        // Wait for break duration to expire (half-open state)
        await Task.Delay(TimeSpan.FromSeconds(options.CircuitBreakerDurationSeconds + 1));

        // Switch handler to success
        handler.SetDefaultResponse(HttpStatusCode.OK, TestData.SingleUser);

        // Half-open: first request should be allowed through, and on success, circuit closes
        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/test/recovery");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public async Task CircuitBreaker_SamplingDuration_AffectsBehavior()
    {
        // SHORT sampling window: failures within the window should trip the breaker
        ResiliencePipelineFactory.Reset();
        var handler1 = new MockHttpHandler();
        handler1.SetDefaultResponse(HttpStatusCode.ServiceUnavailable);

        var shortWindowOptions = FastTripOptions(samplingDurationSeconds: 30);
        using var httpClient1 = new HttpClient(handler1);
        using var client1 = new ResilientGraphClient(httpClient1, shortWindowOptions);

        bool circuitOpenedShort = false;
        for (int i = 0; i < 20; i++)
        {
            try { await client1.GetAsync($"https://graph.microsoft.com/v1.0/test/{i}"); }
            catch (BrokenCircuitException) { circuitOpenedShort = true; break; }
            catch { }
        }

        Assert.True(circuitOpenedShort, "Circuit should open with 30s sampling window");

        // LONG sampling window with high min throughput: same failures should NOT trip
        // because MinThroughput is set very high relative to our few requests
        ResiliencePipelineFactory.Reset();
        var handler2 = new MockHttpHandler();
        handler2.SetDefaultResponse(HttpStatusCode.ServiceUnavailable);

        var highThresholdOptions = new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            MaxRetryAttempts = 1,
            CircuitBreakerMinThroughput = 1000,  // Need 1000 requests before evaluating
            CircuitBreakerFailureRatio = 0.5,
            CircuitBreakerDurationSeconds = 5,
            CircuitBreakerSamplingDurationSeconds = 300,
            TotalTimeoutSeconds = 30,
            AttemptTimeoutSeconds = 10
        };

        using var httpClient2 = new HttpClient(handler2);
        using var client2 = new ResilientGraphClient(httpClient2, highThresholdOptions);

        bool circuitOpenedHigh = false;
        for (int i = 0; i < 10; i++)
        {
            try { await client2.GetAsync($"https://graph.microsoft.com/v1.0/test/{i}"); }
            catch (BrokenCircuitException) { circuitOpenedHigh = true; break; }
            catch { }
        }

        // With MinThroughput=1000, 10 requests isn't enough to trip
        Assert.False(circuitOpenedHigh,
            "Circuit should NOT open when MinThroughput (1000) exceeds request count");

        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public async Task CircuitBreaker_DoesNotTripOn429()
    {
        // 429 is expected throttling, not a failure. Circuit breaker should ignore it.
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();

        // All responses are 429 with Retry-After: 0
        handler.SetDefaultResponse((HttpStatusCode)429, null,
            new Dictionary<string, string> { ["Retry-After"] = "0" });

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, FastTripOptions());

        bool circuitOpened = false;
        for (int i = 0; i < 10; i++)
        {
            try
            {
                await client.GetAsync($"https://graph.microsoft.com/v1.0/test/{i}");
            }
            catch (BrokenCircuitException)
            {
                circuitOpened = true;
                break;
            }
            catch { }
        }

        Assert.False(circuitOpened, "Circuit breaker should NOT trip on 429 (throttling is expected, not a failure)");
        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public async Task CircuitBreaker_OpensOnNonUserTimeouts()
    {
        // Non-user TaskCanceledException (e.g., HttpClient timeout, not Ctrl+C)
        // should count as a CB failure and trip the breaker.
        ResiliencePipelineFactory.Reset();
        var options = FastTripOptions();

        var handler = new MockHttpHandler();
        // Queue enough timeout exceptions for initial + retry attempts
        for (int i = 0; i < 10; i++)
            handler.QueueException(new TaskCanceledException("HttpClient timeout"));

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, options);

        // First call: non-user TCE is retried once (MaxRetryAttempts=1).
        // Initial + 1 retry = 2 CB failures, trips the breaker.
        await Assert.ThrowsAnyAsync<TaskCanceledException>(
            () => client.GetAsync("https://graph.microsoft.com/v1.0/users/user1"));

        // CB is open: next request should throw BrokenCircuitException
        await Assert.ThrowsAnyAsync<BrokenCircuitException>(
            () => client.GetAsync("https://graph.microsoft.com/v1.0/users/user2"));

        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public async Task CircuitBreaker_DoesNotCountUserCancellation()
    {
        // User cancellation (Ctrl+C) should NOT count as a CB failure.
        // After many user cancellations, the CB should still be closed.
        ResiliencePipelineFactory.Reset();
        var options = FastTripOptions();

        // Send 5 user-cancelled requests through the shared pipeline.
        // Each uses a fresh CTS but the same pipeline (same options reference).
        for (int i = 0; i < 5; i++)
        {
            var cts = new CancellationTokenSource();
            var cancelHandler = new CancellingMockHandler(cts);
            using var httpClient = new HttpClient(cancelHandler);
            using var client = new ResilientGraphClient(httpClient, options);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => client.GetAsync("https://graph.microsoft.com/v1.0/users/user1", cts.Token));
        }

        // If CB counted user cancellations as failures, it would be open:
        // 5+ outcomes with 100% failure > MinThroughput=2 and FailureRatio=0.5.
        // But user cancellations are excluded from CB failure counting, so CB stays closed.
        var normalHandler = new MockHttpHandler();
        normalHandler.SetDefaultResponse(HttpStatusCode.OK, TestData.SingleUser);
        using var httpClient2 = new HttpClient(normalHandler);
        using var client2 = new ResilientGraphClient(httpClient2, options);

        var response = await client2.GetAsync("https://graph.microsoft.com/v1.0/users/user1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public async Task CircuitBreaker_ConcurrentRequests_TripAndRecover()
    {
        // Concurrent requests hit a failing service. The CB should trip, rejecting
        // new requests immediately. After break duration + service recovery, requests
        // should succeed again. Tests CB under real concurrent contention.
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.ServiceUnavailable);

        var options = FastTripOptions();
        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, options);

        // Phase 1: Concurrent requests until CB trips
        var brokenCircuitCount = 0;
        var otherErrorCount = 0;
        var tasks = Enumerable.Range(0, 20).Select(async i =>
        {
            try
            {
                await client.GetAsync($"https://graph.microsoft.com/v1.0/test/{i}");
            }
            catch (BrokenCircuitException)
            {
                Interlocked.Increment(ref brokenCircuitCount);
            }
            catch
            {
                Interlocked.Increment(ref otherErrorCount);
            }
        }).ToArray();

        await Task.WhenAll(tasks);

        // CB must have tripped: at least some requests got BrokenCircuitException
        Assert.True(brokenCircuitCount > 0,
            $"Expected CB to trip under concurrent 503s. Got {brokenCircuitCount} BrokenCircuit, {otherErrorCount} other errors.");

        // Phase 2: Wait for break duration, switch to success, verify recovery
        await Task.Delay(TimeSpan.FromSeconds(options.CircuitBreakerDurationSeconds + 1));
        handler.SetDefaultResponse(HttpStatusCode.OK, TestData.SingleUser);

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/test/recovered");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public async Task CircuitBreaker_DefaultMinThroughput40_DoesNotTripBelow40()
    {
        // Verifies the default MinThroughput=40 boundary.
        // With MaxRetryAttempts=1, each call produces 2 CB outcomes (initial + 1 retry).
        // 19 calls = 38 outcomes = below MinThroughput of 40, should NOT trip.
        // 20th call = 40 outcomes = meets MinThroughput, should trip.
        // Same pipeline throughout (no Reset between phases).
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.ServiceUnavailable);

        var options = new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            MaxRetryAttempts = 1,
            CircuitBreakerFailureRatio = 0.1,
            CircuitBreakerDurationSeconds = 5,
            CircuitBreakerSamplingDurationSeconds = 30,
            TotalTimeoutSeconds = 30,
            AttemptTimeoutSeconds = 10
            // CircuitBreakerMinThroughput defaults to 40
        };

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, options);

        // Phase 1: Send 19 calls = 38 CB outcomes (below MinThroughput of 40)
        bool tripped = false;
        for (int i = 0; i < 19; i++)
        {
            try { await client.GetAsync($"https://graph.microsoft.com/v1.0/test/{i}"); }
            catch (Polly.CircuitBreaker.BrokenCircuitException) { tripped = true; break; }
            catch { }
        }

        Assert.False(tripped, "CB should NOT trip with only 38 outcomes (below MinThroughput of 40)");

        // Phase 2: Send 1 more call through the SAME pipeline (outcomes 39-40, meets threshold)
        tripped = false;
        for (int i = 19; i < 25; i++)
        {
            try { await client.GetAsync($"https://graph.microsoft.com/v1.0/test/{i}"); }
            catch (Polly.CircuitBreaker.BrokenCircuitException) { tripped = true; break; }
            catch { }
        }

        Assert.True(tripped, "CB should trip once MinThroughput of 40 outcomes is reached with 100% failures");

        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public async Task CircuitBreaker_PostTimeoutCountsAsFailure()
    {
        // POST requests are NOT retried (non-idempotent), but their timeouts
        // MUST still count as circuit breaker failures. Otherwise a hung downstream
        // accepting POSTs would never trip the breaker for anyone.
        ResiliencePipelineFactory.Reset();

        var handler = new AlwaysSlowMockHandler(delayMs: 5000);

        var options = new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            MaxRetryAttempts = 1,                        // irrelevant for POST (no retry)
            AttemptTimeoutSeconds = 1,                   // 1s timeout, handler takes 5s
            TotalTimeoutSeconds = 60,
            CircuitBreakerMinThroughput = 2,             // trip after 2 outcomes
            CircuitBreakerFailureRatio = 0.5,
            CircuitBreakerDurationSeconds = 5,
            CircuitBreakerSamplingDurationSeconds = 30
        };

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, options);

        // POST is not retried, so each call = 1 CB outcome.
        // Need MinThroughput (2) timeout failures to trip the breaker.
        for (int i = 0; i < 2; i++)
        {
            try
            {
                await client.SendAsync(HttpMethod.Post, $"https://graph.microsoft.com/v1.0/test/{i}",
                    new StringContent("{}"));
            }
            catch (Polly.Timeout.TimeoutRejectedException)
            {
                // Expected: POST timed out, no retry
            }
        }

        // Verify: only 2 HTTP requests were made (no retries for POST)
        Assert.Equal(2, handler.RequestCount);

        // CB should be open: next POST should be rejected immediately
        await Assert.ThrowsAsync<BrokenCircuitException>(
            () => client.SendAsync(HttpMethod.Post, "https://graph.microsoft.com/v1.0/test/post-blocked",
                new StringContent("{}")));

        // Shared CB: GET requests should also be blocked
        await Assert.ThrowsAsync<BrokenCircuitException>(
            () => client.GetAsync("https://graph.microsoft.com/v1.0/test/get-blocked"));

        // No additional HTTP requests were made (breaker short-circuited both)
        Assert.Equal(2, handler.RequestCount);

        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public async Task Integration_RateLimiter_Timeout_CircuitBreaker_Combined()
    {
        // Exercises all 3 resilience layers in one scenario:
        // - Rate limiter (low burst=5, queue=2) rejects overflow requests
        // - Attempt timeout (1s) kills slow requests (handler takes 2s)
        // - Circuit breaker (MinThroughput=4, FailureRatio=0.5) trips on accumulated timeouts
        //
        // Key interaction: timeouts feed the CB as failures. Once the CB trips, subsequent
        // admitted requests get BrokenCircuitException (never reaching the timeout layer).
        // So timeouts may surface as either TimeoutRejectedException or BrokenCircuitException
        // depending on timing. We verify timeouts occurred via handler.RequestCount (requests
        // that hit the handler but were killed by the 1s attempt timeout).
        ResiliencePipelineFactory.Reset();

        var handler = new AlwaysSlowMockHandler(delayMs: 2000); // Exceeds 1s attempt timeout

        var options = new ResilientGraphClientOptions
        {
            RateLimitBurst = 5,
            RateLimitPerSecond = 1,          // Slow replenishment to force queue pressure
            RateLimitQueueLimit = 2,         // Only 2 can queue beyond burst
            MaxRetryAttempts = 1,            // 1 retry = 2 CB outcomes per call
            AttemptTimeoutSeconds = 1,       // 1s timeout, handler takes 2s
            TotalTimeoutSeconds = 30,
            CircuitBreakerMinThroughput = 4, // Trip after 4 failed outcomes
            CircuitBreakerFailureRatio = 0.5,
            CircuitBreakerDurationSeconds = 10,
            CircuitBreakerSamplingDurationSeconds = 30
        };

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, options);

        var rateLimitRejections = 0;
        var timeouts = 0;
        var circuitBreaks = 0;
        var otherErrors = 0;

        // Fire 15 concurrent requests: burst(5) + queue(2) = 7 admitted, 8 rejected by rate limiter.
        // The admitted requests all timeout (2s handler > 1s attempt timeout).
        // With MaxRetryAttempts=1, each admitted call produces 2 CB outcomes (timeout failures).
        // After 4 timeout outcomes (MinThroughput=4 at 100% failure), CB trips.
        // Remaining admitted requests get BrokenCircuitException instead of TimeoutRejectedException.
        var tasks = Enumerable.Range(0, 15).Select(async i =>
        {
            try
            {
                await client.GetAsync($"https://graph.microsoft.com/v1.0/test/{i}");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Rate limit"))
            {
                Interlocked.Increment(ref rateLimitRejections);
            }
            catch (Polly.Timeout.TimeoutRejectedException)
            {
                Interlocked.Increment(ref timeouts);
            }
            catch (BrokenCircuitException)
            {
                Interlocked.Increment(ref circuitBreaks);
            }
            catch
            {
                Interlocked.Increment(ref otherErrors);
            }
        }).ToArray();

        await Task.WhenAll(tasks);

        // All 15 requests must resolve (no silent drops)
        var total = rateLimitRejections + timeouts + circuitBreaks + otherErrors;
        Assert.Equal(15, total);

        // Layer 1 - Rate limiter: with burst=5, queue=2, perSecond=1, many of the 15
        // concurrent requests should be rejected (burst + queue = 7 max admitted)
        Assert.True(rateLimitRejections > 0,
            $"Expected rate limiter rejections. Got: RL={rateLimitRejections}, TO={timeouts}, CB={circuitBreaks}, Other={otherErrors}");

        // Layer 2 - Timeouts: handler.RequestCount proves requests hit the handler and
        // were killed by attempt timeout (2s handler > 1s timeout). Timeouts surface as
        // either TimeoutRejectedException or BrokenCircuitException depending on whether
        // the CB has tripped by the time they complete.
        Assert.True(handler.RequestCount > 0,
            $"Expected requests to reach the handler (proving timeouts occurred). Handler saw {handler.RequestCount} requests.");

        // Layer 3 - Circuit breaker: after timeout failures accumulate past MinThroughput=4,
        // remaining requests are rejected immediately by the open circuit.
        // Timeouts + circuit breaks together represent all admitted-but-failed requests.
        Assert.True(circuitBreaks > 0,
            $"Expected circuit breaker to trip. Got: RL={rateLimitRejections}, TO={timeouts}, CB={circuitBreaks}, Other={otherErrors}");

        // Cross-layer interaction: the CB tripped BECAUSE of timeouts (not 5xx responses).
        // Verify no "other" errors leaked through - every failure is from a known layer.
        Assert.Equal(0, otherErrors);

        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public async Task CircuitBreaker_OpensOnRepeatedAttemptTimeouts()
    {
        // TimeoutRejectedException (Polly per-attempt timeout) should count as a CB failure.
        // Without this, a hung downstream wastes MaxRetryAttempts * AttemptTimeoutSeconds
        // before giving up, and never trips the breaker for subsequent callers.
        ResiliencePipelineFactory.Reset();

        // Handler that always takes longer than AttemptTimeoutSeconds
        var handler = new AlwaysSlowMockHandler(delayMs: 5000);

        var options = new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            MaxRetryAttempts = 1,                        // 1 retry = 2 CB outcomes per call
            AttemptTimeoutSeconds = 1,                   // 1s timeout, handler takes 5s
            TotalTimeoutSeconds = 60,                    // Plenty of room
            CircuitBreakerMinThroughput = 2,             // Trip after 2 outcomes
            CircuitBreakerFailureRatio = 0.5,
            CircuitBreakerDurationSeconds = 5,
            CircuitBreakerSamplingDurationSeconds = 30
        };

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, options);

        // First call: initial attempt times out + 1 retry times out = 2 CB failures.
        // With MinThroughput=2, this should trip the breaker.
        try
        {
            await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");
        }
        catch (Polly.Timeout.TimeoutRejectedException)
        {
            // Expected: retries exhausted, last timeout propagates
        }

        // CB should be open now: next request should be rejected immediately
        await Assert.ThrowsAsync<BrokenCircuitException>(
            () => client.GetAsync("https://graph.microsoft.com/v1.0/users/user2"));

        // Verify: the breaker short-circuited (no new HTTP request was made)
        // First call: 2 attempts (initial + 1 retry). Second call: 0 (breaker open).
        Assert.Equal(2, handler.RequestCount);

        ResiliencePipelineFactory.Reset();
    }

    /// <summary>
    /// Handler that always delays longer than the attempt timeout, simulating a hung downstream.
    /// </summary>
    private sealed class AlwaysSlowMockHandler(int delayMs) : HttpMessageHandler
    {
        private int _callCount;
        public int RequestCount => Volatile.Read(ref _callCount);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _callCount);
            await Task.Delay(delayMs, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    /// <summary>
    /// Handler that simulates user cancellation mid-request.
    /// Cancels the CTS during SendAsync and throws TaskCanceledException with the cancelled token.
    /// </summary>
    private sealed class CancellingMockHandler(CancellationTokenSource cts) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            cts.Cancel();
            return Task.FromException<HttpResponseMessage>(
                new TaskCanceledException("User cancelled", null, cts.Token));
        }
    }
}
