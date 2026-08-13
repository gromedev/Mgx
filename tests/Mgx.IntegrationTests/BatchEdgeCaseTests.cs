using System.Net;
using System.Text.Json;
using Mgx.Engine.Http;
using Mgx.Engine.Models;

namespace Mgx.IntegrationTests;

[Collection("Pipeline")]
public class BatchEdgeCaseTests
{
    /// <summary>
    /// Helper: build a batch response JSON string with N items at the given status.
    /// Supports optional Retry-After header and error body for 429 responses.
    /// </summary>
    private static string BuildBatchResponse(int count, int status, int? retryAfterSeconds = null)
    {
        var items = string.Join(",\n", Enumerable.Range(1, count).Select(i =>
        {
            var headers = retryAfterSeconds.HasValue
                ? $", \"headers\": {{ \"Retry-After\": \"{retryAfterSeconds.Value}\" }}"
                : "";
            var body = status == 429
                ? ", \"body\": { \"error\": { \"code\": \"TooManyRequests\" } }"
                : status is >= 200 and < 300
                    ? $", \"body\": {{ \"id\": \"user{i}\" }}"
                    : "";
            return $"{{ \"id\": \"{i}\", \"status\": {status}{headers}{body} }}";
        }));
        return $"{{ \"responses\": [{items}] }}";
    }

    /// <summary>
    /// Helper: build a mixed batch response where the first successCount items succeed
    /// and the remaining failCount items get the given fail status.
    /// </summary>
    private static string BuildMixedBatchResponse(int successCount, int failCount,
        int successStatus = 201, int failStatus = 429, int? retryAfterSeconds = null)
    {
        var items = new List<string>();
        for (int i = 1; i <= successCount; i++)
        {
            items.Add($"{{ \"id\": \"{i}\", \"status\": {successStatus}, \"body\": {{ \"id\": \"user{i}\" }} }}");
        }
        for (int i = successCount + 1; i <= successCount + failCount; i++)
        {
            var headers = retryAfterSeconds.HasValue
                ? $", \"headers\": {{ \"Retry-After\": \"{retryAfterSeconds.Value}\" }}"
                : "";
            items.Add($"{{ \"id\": \"{i}\", \"status\": {failStatus}{headers}, \"body\": {{ \"error\": {{ \"code\": \"TooManyRequests\" }} }} }}");
        }
        return $"{{ \"responses\": [{string.Join(",\n", items)}] }}";
    }

    // ── R2-3: All 20 items fail with 429 ──────────────────────────────────────

    [Fact]
    public async Task Batch_All20Items_429_RetriedAndSucceed()
    {
        // First response: all 20 items return 429
        var all429 = BuildBatchResponse(20, status: 429, retryAfterSeconds: 0);
        // Second response: all 20 items succeed
        var all200 = BuildBatchResponse(20, status: 200);

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, all429);
        handler.QueueResponse(HttpStatusCode.OK, all200);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var operations = Enumerable.Range(1, 20)
            .Select(i => new BatchOperation($"/users/user{i}", "GET"))
            .ToList();

        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        // 2 HTTP calls: first (all 429), retry (all 200)
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(20, result.Results.Count);
        Assert.All(result.Results, r => Assert.Equal(200, r.Response.Status));
        Assert.Equal(20, result.Telemetry.Succeeded);
        Assert.Equal(0, result.Telemetry.Failed);
        Assert.Equal(20, result.Telemetry.ItemRetries);
        Assert.Equal(20, result.Telemetry.ThrottleEncounters);
    }

    // ── R2-3: Partial retry — 10 succeed, 10 fail with 429 ───────────────────

    [Fact]
    public async Task Batch_PartialRetry_10Succeed_10Throttled_ThenAllSucceed()
    {
        // First response: items 1-10 succeed (200), items 11-20 get 429
        var partialThrottled = BuildMixedBatchResponse(
            successCount: 10, failCount: 10,
            successStatus: 200, failStatus: 429, retryAfterSeconds: 0);
        // Second response: the 10 retried items succeed
        var retrySuccess = BuildBatchResponse(10, status: 200);

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, partialThrottled);
        handler.QueueResponse(HttpStatusCode.OK, retrySuccess);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var operations = Enumerable.Range(1, 20)
            .Select(i => new BatchOperation($"/users/user{i}", "GET"))
            .ToList();

        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        // 2 HTTP calls: initial (mixed), retry (10 items)
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(20, result.Results.Count);
        Assert.All(result.Results, r => Assert.Equal(200, r.Response.Status));
        Assert.Equal(20, result.Telemetry.Succeeded);
        Assert.Equal(0, result.Telemetry.Failed);
        Assert.Equal(10, result.Telemetry.ItemRetries);
        Assert.Equal(10, result.Telemetry.ThrottleEncounters);
    }

    // ── R2-3: Mismatched response count ───────────────────────────────────────

    [Fact]
    public async Task Batch_MismatchedResponseCount_Throws()
    {
        // Send 5 items but server returns only 3 responses
        var truncatedResponse = """
        {
            "responses": [
                { "id": "1", "status": 200, "body": { "id": "user1" } },
                { "id": "2", "status": 200, "body": { "id": "user2" } },
                { "id": "3", "status": 200, "body": { "id": "user3" } }
            ]
        }
        """;

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, truncatedResponse);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var operations = Enumerable.Range(1, 5)
            .Select(i => new BatchOperation($"/users/user{i}", "GET"))
            .ToList();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => batchClient.ExecuteBatchIndexedAsync(operations));

        Assert.Contains("count mismatch", ex.Message);
        Assert.Contains("sent 5", ex.Message);
        Assert.Contains("received 3", ex.Message);
    }

    // ── R2-8: 0 items — empty batch ──────────────────────────────────────────

    [Fact]
    public async Task Batch_ZeroItems_ReturnsEmptyResult_NoHttpCalls()
    {
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, """{ "responses": [] }""");

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var operations = new List<BatchOperation>();

        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        Assert.Empty(result.Results);
        Assert.Equal(0, handler.RequestCount);
        Assert.Equal(0, result.Telemetry.TotalRequests);
    }

    // ── R2-8: 1 item — single item batch ─────────────────────────────────────

    [Fact]
    public async Task Batch_SingleItem_OneHttpCall_OneResult()
    {
        var singleResponse = """
        {
            "responses": [
                { "id": "1", "status": 200, "body": { "id": "user1", "displayName": "User One" } }
            ]
        }
        """;

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, singleResponse);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var operations = new List<BatchOperation> { new("/users/user1", "GET") };

        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        Assert.Equal(1, handler.RequestCount);
        Assert.Single(result.Results);
        Assert.Equal(200, result.Results[0].Response.Status);
        Assert.Equal(1, result.Telemetry.TotalRequests);
        Assert.Equal(1, result.Telemetry.Succeeded);
    }

    // ── R2-8: 20 items — exact batch boundary ────────────────────────────────

    [Fact]
    public async Task Batch_Exactly20Items_SingleHttpCall()
    {
        var response20 = BuildBatchResponse(20, status: 200);

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, response20);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var operations = Enumerable.Range(1, 20)
            .Select(i => new BatchOperation($"/users/user{i}", "GET"))
            .ToList();

        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        // Exactly 20 items = exactly 1 batch (MaxBatchSize = 20)
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(20, result.Results.Count);
        Assert.All(result.Results, r => Assert.Equal(200, r.Response.Status));
        Assert.Equal(20, result.Telemetry.TotalRequests);
        Assert.Equal(20, result.Telemetry.Succeeded);
    }

    // ── R2-8: 21 items — one over boundary ───────────────────────────────────

    [Fact]
    public async Task Batch_21Items_TwoHttpCalls()
    {
        var chunk1Response = BuildBatchResponse(20, status: 200);
        var chunk2Response = BuildBatchResponse(1, status: 200);

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, chunk1Response);
        handler.QueueResponse(HttpStatusCode.OK, chunk2Response);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var operations = Enumerable.Range(1, 21)
            .Select(i => new BatchOperation($"/users/user{i}", "GET"))
            .ToList();

        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        // 21 items = 2 batches (20 + 1)
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(21, result.Results.Count);
        Assert.All(result.Results, r => Assert.Equal(200, r.Response.Status));
        Assert.Equal(21, result.Telemetry.TotalRequests);
        Assert.Equal(21, result.Telemetry.Succeeded);
    }
}
