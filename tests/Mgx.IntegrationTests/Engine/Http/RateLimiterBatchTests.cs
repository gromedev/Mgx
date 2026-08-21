using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Mgx.Engine.Http;
using Mgx.Engine.Models;

namespace Mgx.IntegrationTests;

[Collection("Pipeline")]
public class RateLimiterBatchTests
{
    /// <summary>
    /// Helper: build a batch response JSON string with N items at the given status.
    /// </summary>
    private static string BuildBatchResponse(int count, int status)
    {
        var items = string.Join(",\n", Enumerable.Range(1, count).Select(i =>
            $"{{ \"id\": \"{i}\", \"status\": {status}, \"body\": {{ \"id\": \"user{i}\" }} }}"));
        return $"{{ \"responses\": [{items}] }}";
    }

    // ── R2-5: Rate limiter enabled during batch operations ───────────────────

    [Fact]
    public async Task Batch_WithRateLimiter_AllRequestsSucceed()
    {
        // Setup: rate limiting ON (default behavior) with a tight burst limit.
        // Send 10 sequential batch requests through the rate limiter.
        // All should succeed because the rate limiter queues excess requests
        // rather than rejecting at this volume.
        ResiliencePipelineFactory.Reset();

        var batchResponse = BuildBatchResponse(2, status: 200);
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, batchResponse);

        using var httpClient = new HttpClient(handler);
        // Rate limiting ON: burst=5, 5/sec, queue=50 (tight but sufficient for 10 sequential requests)
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions
        {
            RateLimitBurst = 5,
            RateLimitPerSecond = 5,
            RateLimitQueueLimit = 50
        });
        var batchClient = new GraphBatchClient(client);

        var operations = new List<BatchOperation>
        {
            new("/users/user1", "GET"),
            new("/users/user2", "GET")
        };

        var successes = 0;
        var exceptions = new ConcurrentBag<Exception>();

        // Send 10 sequential batch requests
        for (int i = 0; i < 10; i++)
        {
            try
            {
                var result = await batchClient.ExecuteBatchIndexedAsync(operations);
                Assert.Equal(2, result.Results.Count);
                Assert.All(result.Results, r => Assert.Equal(200, r.Response.Status));
                successes++;
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }

        // All 10 batch requests should succeed (rate limiter queues, doesn't reject at low volume)
        Assert.Equal(10, successes);
        Assert.Empty(exceptions);
        // Each batch call = 1 HTTP POST to /$batch, so 10 total
        Assert.Equal(10, handler.RequestCount);

        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public async Task Batch_WithRateLimiter_ConcurrentBatches_AllSucceedOrQueue()
    {
        // Verify rate limiter doesn't deadlock or corrupt state under concurrent batch usage.
        // With tight limits (burst=3, queue=20), 5 concurrent batch calls must all resolve.
        ResiliencePipelineFactory.Reset();

        var batchResponse = BuildBatchResponse(2, status: 200);
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, batchResponse);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions
        {
            RateLimitBurst = 3,
            RateLimitPerSecond = 3,
            RateLimitQueueLimit = 20
        });

        var operations = new List<BatchOperation>
        {
            new("/users/user1", "GET"),
            new("/users/user2", "GET")
        };

        var successes = 0;
        var exceptions = new ConcurrentBag<Exception>();

        var tasks = Enumerable.Range(0, 5).Select(async _ =>
        {
            try
            {
                var bc = new GraphBatchClient(client);
                var result = await bc.ExecuteBatchIndexedAsync(operations);
                Interlocked.Increment(ref successes);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }).ToArray();

        // Must complete within 10s (no deadlock)
        var allDone = Task.WhenAll(tasks);
        var completed = await Task.WhenAny(allDone, Task.Delay(10_000)) == allDone;
        Assert.True(completed, "Rate limiter deadlocked under concurrent batch requests");

        // All 5 tasks must have resolved (success or exception, no silent drops)
        Assert.Equal(5, successes + exceptions.Count);
        // At least some should succeed (burst of 3 goes through immediately)
        Assert.True(successes > 0,
            $"Expected some batch requests to succeed, got {successes} successes, {exceptions.Count} exceptions");

        ResiliencePipelineFactory.Reset();
    }
}
