using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Mgx.Engine.Http;

namespace Mgx.IntegrationTests;

[Collection("Pipeline")]
public class RateLimiterTests
{
    [Fact]
    public async Task RateLimiter_ThrottlesConcurrentRequests()
    {
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient);

        // Fire 50 concurrent requests. The token bucket has 200 permit burst
        // and 50/sec replenishment, so 50 should go through but be shaped
        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, 50)
            .Select(i => client.GetAsync($"https://graph.microsoft.com/v1.0/users/{i}"))
            .ToList();

        var results = await Task.WhenAll(tasks);
        sw.Stop();

        // All 50 should succeed (within burst limit of 100)
        Assert.Equal(50, results.Length);
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(50, handler.RequestCount);
    }

    [Fact]
    public async Task RateLimiter_RejectsWhenExhausted()
    {
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient);

        // Fire way more concurrent requests than the token bucket allows
        // Token bucket: 200 burst + 500 queue limit = max 700
        // Fire 800 to try to exceed it
        var exceptions = new ConcurrentBag<Exception>();
        var successes = new ConcurrentBag<HttpResponseMessage>();

        var tasks = Enumerable.Range(0, 800)
            .Select(async i =>
            {
                try
                {
                    var r = await client.GetAsync($"https://graph.microsoft.com/v1.0/users/{i}");
                    successes.Add(r);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            })
            .ToList();

        await Task.WhenAll(tasks);

        // Some should succeed, some must be rejected by the rate limiter
        Assert.True(successes.Count > 0, "At least some requests should succeed");
        // With 200 burst + 500 queue = 700, firing 800 must produce rejections
        Assert.True(exceptions.Count > 0,
            $"Expected rejections but got 0. Successes: {successes.Count}. " +
            $"Burst(200) + Queue(500) = 700 capacity, fired 800 requests.");
        Assert.True(successes.Count + exceptions.Count == 800,
            $"Total should be 800: {successes.Count} succeeded, {exceptions.Count} failed");
        // Rejected requests should be rate-limiter exceptions
        Assert.Contains(exceptions, e => e.Message.Contains("Rate limit"));
    }

    [Fact]
    public async Task RateLimiter_ConcurrentContention_NoDeadlock()
    {
        // Tests the rate limiter under real concurrent contention with a tight token budget.
        // A deadlock in TokenBucketRateLimiter under contention would cause this to hang.
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        // Tight budget: 5 burst, 5/sec, queue 50. Forces contention.
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions
        {
            RateLimitBurst = 5,
            RateLimitPerSecond = 5,
            RateLimitQueueLimit = 50
        });

        var exceptions = new ConcurrentBag<Exception>();
        var successes = 0;

        // 20 concurrent requests with tight rate limit forces queuing
        var tasks = Enumerable.Range(0, 20).Select(async i =>
        {
            try
            {
                await client.GetAsync($"https://graph.microsoft.com/v1.0/users/{i}");
                Interlocked.Increment(ref successes);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }).ToArray();

        // Must complete within 10 seconds (no deadlock). 20 requests at 5/sec with 5 burst
        // should drain in ~3s. A deadlock would hang immediately, not after 9 seconds.
        var allDone = Task.WhenAll(tasks);
        var completed = await Task.WhenAny(allDone, Task.Delay(10_000)) == allDone;
        Assert.True(completed, "Rate limiter deadlocked under concurrent contention");

        // All 20 tasks must have resolved (success or exception, no silent drops)
        Assert.Equal(20, successes + exceptions.Count);
        // At least some requests should succeed (burst of 5 goes through immediately)
        Assert.True(successes > 0, $"Expected some successes, got {successes} with {exceptions.Count} exceptions");
        ResiliencePipelineFactory.Reset();
    }
}
