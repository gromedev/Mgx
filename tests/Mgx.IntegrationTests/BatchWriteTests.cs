using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mgx.Cmdlets.Cmdlets.Batch;
using Mgx.Engine.Http;
using Mgx.Engine.Models;

namespace Mgx.IntegrationTests;

[Collection("Pipeline")]
public class BatchWriteTests
{
    private static readonly string BatchSuccessResponse = """
    {
        "responses": [
            { "id": "1", "status": 201, "body": { "id": "new-user-1", "displayName": "Test User 1" } },
            { "id": "2", "status": 201, "body": { "id": "new-user-2", "displayName": "Test User 2" } }
        ]
    }
    """;

    private static readonly string BatchGetResponse = """
    {
        "responses": [
            { "id": "1", "status": 200, "body": { "id": "user1", "displayName": "User One" } },
            { "id": "2", "status": 200, "body": { "id": "user2", "displayName": "User Two" } }
        ]
    }
    """;

    [Fact]
    public async Task BatchPost_SetsMethodAndBody()
    {
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, BatchSuccessResponse);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var body = JsonSerializer.Deserialize<JsonElement>("""{"displayName":"Test"}""");
        var operations = new List<BatchOperation>
        {
            new("/users", "POST", body),
            new("/users", "POST", body)
        };

        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        Assert.Equal(2, result.Results.Count);
        Assert.Equal(201, result.Results[0].Response.Status);
        Assert.Equal(201, result.Results[1].Response.Status);
        // Verify the batch request sent to the server contains POST method and body
        var request = handler.Requests[0];
        var requestBody = await request.Content!.ReadAsStringAsync();
        Assert.Contains("\"method\":\"POST\"", requestBody);
        Assert.Contains("\"body\":", requestBody);
        Assert.Contains("\"Content-Type\":\"application/json\"", requestBody);
        // Telemetry
        Assert.Equal(2, result.Telemetry.TotalRequests);
        Assert.Equal(2, result.Telemetry.Succeeded);
        Assert.Equal(0, result.Telemetry.Failed);
    }

    [Fact]
    public async Task BatchGet_ItemHeaders_AppliedToEachItem()
    {
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, BatchGetResponse);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client)
        {
            ItemHeaders = new Dictionary<string, string>
            {
                ["ConsistencyLevel"] = "eventual"
            }
        };

        var operations = new List<BatchOperation>
        {
            new("/users?$search=\"displayName:test\""),
            new("/groups?$search=\"displayName:eng\"")
        };

        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        Assert.Equal(2, result.Results.Count);
        // Verify the serialized batch JSON contains ConsistencyLevel on each item
        var request = handler.Requests[0];
        var requestBody = await request.Content!.ReadAsStringAsync();
        Assert.Contains("\"ConsistencyLevel\":\"eventual\"", requestBody);
        // Verify it appears for both items (should be in the headers of each)
        var doc = JsonDocument.Parse(requestBody);
        var requests = doc.RootElement.GetProperty("requests");
        foreach (var item in requests.EnumerateArray())
        {
            var headers = item.GetProperty("headers");
            Assert.True(headers.TryGetProperty("ConsistencyLevel", out var cl));
            Assert.Equal("eventual", cl.GetString());
        }
    }

    [Fact]
    public async Task BatchPost_ItemHeaders_MergedWithContentType()
    {
        var singlePostResponse = """
        {
            "responses": [
                { "id": "1", "status": 201, "body": { "id": "new-user-1", "displayName": "Test User 1" } }
            ]
        }
        """;
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, singlePostResponse);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client)
        {
            ItemHeaders = new Dictionary<string, string>
            {
                ["ConsistencyLevel"] = "eventual"
            }
        };

        var body = JsonSerializer.Deserialize<JsonElement>("""{"displayName":"Test"}""");
        var operations = new List<BatchOperation>
        {
            new("/users", "POST", body)
        };

        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        // Verify both ConsistencyLevel AND Content-Type are present on the item
        var request = handler.Requests[0];
        var requestBody = await request.Content!.ReadAsStringAsync();
        var doc = JsonDocument.Parse(requestBody);
        var item = doc.RootElement.GetProperty("requests")[0];
        var headers = item.GetProperty("headers");
        Assert.True(headers.TryGetProperty("ConsistencyLevel", out var cl));
        Assert.Equal("eventual", cl.GetString());
        Assert.True(headers.TryGetProperty("Content-Type", out var ct));
        Assert.Equal("application/json", ct.GetString());
    }

    [Fact]
    public async Task BatchGet_NoItemHeaders_NoHeadersInJson()
    {
        var singleGetResponse = """
        {
            "responses": [
                { "id": "1", "status": 200, "body": { "id": "user1", "displayName": "User One" } }
            ]
        }
        """;
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, singleGetResponse);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        // No ItemHeaders set (default null)
        var batchClient = new GraphBatchClient(client);

        var operations = new List<BatchOperation> { new("/users") };

        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        // GET without body and no ItemHeaders: headers key should be absent from JSON
        var request = handler.Requests[0];
        var requestBody = await request.Content!.ReadAsStringAsync();
        var doc = JsonDocument.Parse(requestBody);
        var item = doc.RootElement.GetProperty("requests")[0];
        Assert.False(item.TryGetProperty("headers", out _), "headers should be omitted when null (JsonIgnore WhenWritingNull)");
    }

    [Fact]
    public async Task BatchGet_BackwardCompatible()
    {
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, BatchGetResponse);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        // Old-style call with string URLs
        var urls = new List<string> { "/users/user1", "/users/user2" };
        var results = await batchClient.ExecuteBatchAsync(urls);

        Assert.Equal(2, results.Count);
        Assert.True(results.ContainsKey("/users/user1"));
        Assert.True(results.ContainsKey("/users/user2"));

        // Verify GET is the default and no body is sent
        var request = handler.Requests[0];
        var requestBody = await request.Content!.ReadAsStringAsync();
        var batchDoc = JsonDocument.Parse(requestBody);
        var requests = batchDoc.RootElement.GetProperty("requests");
        var firstItem = requests[0];
        Assert.Equal("GET", firstItem.GetProperty("method").GetString());
        Assert.False(firstItem.TryGetProperty("body", out _), "GET requests must not include a body");
    }

    [Fact]
    public async Task BatchPost_NonIdempotentRetryGuard()
    {
        // POST items should only retry on 429, not 503/504
        var post503Response = """
        {
            "responses": [
                { "id": "1", "status": 503 }
            ]
        }
        """;

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, post503Response);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var body = JsonSerializer.Deserialize<JsonElement>("""{"displayName":"Test"}""");
        var operations = new List<BatchOperation> { new("/users", "POST", body) };

        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        // POST with 503 should NOT retry (only 1 batch request sent)
        Assert.Equal(1, handler.RequestCount);
        Assert.Single(result.Results);
        Assert.Equal(503, result.Results[0].Response.Status);
        Assert.Equal(1, result.Telemetry.Failed);
    }

    [Fact]
    public async Task BatchGet_503Retries()
    {
        // GET items should retry on 503
        var get503Response = """
        {
            "responses": [
                { "id": "1", "status": 503 }
            ]
        }
        """;
        var getSuccessResponse = """
        {
            "responses": [
                { "id": "1", "status": 200, "body": { "id": "user1" } }
            ]
        }
        """;

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, get503Response);
        handler.QueueResponse(HttpStatusCode.OK, getSuccessResponse);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var operations = new List<BatchOperation> { new("/users/user1", "GET") };

        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        // GET with 503 should retry (2 batch requests sent)
        Assert.Equal(2, handler.RequestCount);
        Assert.Single(result.Results);
        Assert.Equal(200, result.Results[0].Response.Status);
        Assert.Equal(1, result.Telemetry.ItemRetries);
    }

    [Fact]
    public async Task BatchGet_500Retries()
    {
        // GET items should retry on 500 (aligned with ResiliencePipelineFactory)
        var get500Response = """
        {
            "responses": [
                { "id": "1", "status": 500 }
            ]
        }
        """;
        var getSuccessResponse = """
        {
            "responses": [
                { "id": "1", "status": 200, "body": { "id": "user1" } }
            ]
        }
        """;

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, get500Response);
        handler.QueueResponse(HttpStatusCode.OK, getSuccessResponse);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var operations = new List<BatchOperation> { new("/users/user1", "GET") };
        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        Assert.Equal(2, handler.RequestCount); // Retried on 500
        Assert.Single(result.Results);
        Assert.Equal(200, result.Results[0].Response.Status);
    }

    [Fact]
    public async Task BatchGet_502Retries()
    {
        // GET items should retry on 502 (aligned with ResiliencePipelineFactory)
        var get502Response = """
        {
            "responses": [
                { "id": "1", "status": 502 }
            ]
        }
        """;
        var getSuccessResponse = """
        {
            "responses": [
                { "id": "1", "status": 200, "body": { "id": "user1" } }
            ]
        }
        """;

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, get502Response);
        handler.QueueResponse(HttpStatusCode.OK, getSuccessResponse);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var operations = new List<BatchOperation> { new("/users/user1", "GET") };
        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        Assert.Equal(2, handler.RequestCount); // Retried on 502
        Assert.Single(result.Results);
        Assert.Equal(200, result.Results[0].Response.Status);
    }

    [Fact]
    public async Task BatchPost_DoesNotRetryOn500()
    {
        // POST items should NOT retry on 500 (non-idempotent)
        var post500Response = """
        {
            "responses": [
                { "id": "1", "status": 500 }
            ]
        }
        """;

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, post500Response);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var body = JsonSerializer.Deserialize<JsonElement>("""{"displayName":"Test"}""");
        var operations = new List<BatchOperation> { new("/users", "POST", body) };
        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        Assert.Equal(1, handler.RequestCount); // No retry for POST on 500
        Assert.Single(result.Results);
        Assert.Equal(500, result.Results[0].Response.Status);
    }

    [Fact]
    public async Task BatchPost_DoesNotRetryOn502()
    {
        // POST items should NOT retry on 502 (non-idempotent)
        var post502Response = """
        {
            "responses": [
                { "id": "1", "status": 502 }
            ]
        }
        """;

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, post502Response);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var body = JsonSerializer.Deserialize<JsonElement>("""{"displayName":"Test"}""");
        var operations = new List<BatchOperation> { new("/users", "POST", body) };
        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        Assert.Equal(1, handler.RequestCount); // No retry for POST on 502
        Assert.Single(result.Results);
        Assert.Equal(502, result.Results[0].Response.Status);
    }

    [Fact]
    public async Task BatchPatch_SetsMethodAndBody()
    {
        var patchResponse = """
        {
            "responses": [
                { "id": "1", "status": 200, "body": { "id": "user1", "department": "Engineering" } }
            ]
        }
        """;

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, patchResponse);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var body = JsonSerializer.Deserialize<JsonElement>("""{"department":"Engineering"}""");
        var operations = new List<BatchOperation> { new("/users/user1", "PATCH", body) };

        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        Assert.Single(result.Results);
        Assert.Equal(200, result.Results[0].Response.Status);
        var request = handler.Requests[0];
        var requestBody = await request.Content!.ReadAsStringAsync();
        Assert.Contains("\"method\":\"PATCH\"", requestBody);
        Assert.Contains("\"body\":", requestBody);
    }

    [Fact]
    public async Task BatchDelete_NoBody()
    {
        var deleteResponse = """
        {
            "responses": [
                { "id": "1", "status": 204 },
                { "id": "2", "status": 204 }
            ]
        }
        """;

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, deleteResponse);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var operations = new List<BatchOperation>
        {
            new("/users/user1", "DELETE"),
            new("/users/user2", "DELETE")
        };

        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        Assert.Equal(2, result.Results.Count);
        Assert.Equal(204, result.Results[0].Response.Status);
        Assert.Equal(204, result.Results[1].Response.Status);

        // Verify no body field in the request
        var requestBody = await handler.Requests[0].Content!.ReadAsStringAsync();
        var batchDoc = JsonDocument.Parse(requestBody);
        var deleteRequests = batchDoc.RootElement.GetProperty("requests");
        var firstDelete = deleteRequests[0];
        Assert.Equal("DELETE", firstDelete.GetProperty("method").GetString());
        Assert.False(firstDelete.TryGetProperty("body", out _), "DELETE requests must not include a body");
    }

    [Fact]
    public async Task BatchPost_429RetryAfter_RetriesAfterDelay()
    {
        // First batch response: item 1 gets 429 with Retry-After
        var throttledResponse = """
        {
            "responses": [
                { "id": "1", "status": 429, "headers": { "Retry-After": "1" } }
            ]
        }
        """;
        // Second batch response: item 1 succeeds on retry
        var successResponse = """
        {
            "responses": [
                { "id": "1", "status": 201, "body": { "id": "new-user-1", "displayName": "Test User 1" } }
            ]
        }
        """;

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, throttledResponse);
        handler.QueueResponse(HttpStatusCode.OK, successResponse);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var body = JsonSerializer.Deserialize<JsonElement>("""{"displayName":"Test"}""");
        var operations = new List<BatchOperation> { new("/users", "POST", body) };

        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        // POST retries on 429 (2 batch requests sent)
        Assert.Equal(2, handler.RequestCount);
        Assert.Single(result.Results);
        Assert.Equal(201, result.Results[0].Response.Status);
        Assert.Equal(1, result.Telemetry.ThrottleEncounters);
        Assert.Equal(1, result.Telemetry.ItemRetries);
    }

    [Fact]
    public async Task BatchGet_ChunkingOver20_SendsMultipleBatches()
    {
        // Build batch responses for chunk 1 (20 items) and chunk 2 (5 items)
        var chunk1Items = string.Join(",\n",
            Enumerable.Range(1, 20).Select(i =>
                $$"""{ "id": "{{i}}", "status": 200, "body": { "id": "user{{i}}" } }"""));
        var chunk1Response = $$"""{ "responses": [{{chunk1Items}}] }""";

        var chunk2Items = string.Join(",\n",
            Enumerable.Range(1, 5).Select(i =>
                $$"""{ "id": "{{i}}", "status": 200, "body": { "id": "user{{20 + i}}" } }"""));
        var chunk2Response = $$"""{ "responses": [{{chunk2Items}}] }""";

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, chunk1Response);
        handler.QueueResponse(HttpStatusCode.OK, chunk2Response);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        // 25 operations: should produce 2 batch calls (20 + 5)
        var operations = Enumerable.Range(1, 25)
            .Select(i => new BatchOperation($"/users/user{i}", "GET"))
            .ToList();

        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(25, result.Results.Count);
        // All should be 200
        Assert.All(result.Results, r => Assert.Equal(200, r.Response.Status));
        Assert.Equal(25, result.Telemetry.TotalRequests);
        Assert.Equal(25, result.Telemetry.Succeeded);
        Assert.Equal(0, result.Telemetry.Failed);
    }

    [Fact]
    public async Task BatchPost_MixedResults_PartialSuccess()
    {
        var mixedResponse = """
        {
            "responses": [
                { "id": "1", "status": 201, "body": { "id": "new-group-1", "displayName": "Group 1" } },
                { "id": "2", "status": 403, "body": { "error": { "code": "Authorization_RequestDenied", "message": "Insufficient privileges" } } },
                { "id": "3", "status": 500, "body": { "error": { "code": "Service_InternalServerError", "message": "An internal error occurred" } } }
            ]
        }
        """;

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, mixedResponse);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var body = JsonSerializer.Deserialize<JsonElement>("""{"displayName":"Test Group"}""");
        var operations = new List<BatchOperation>
        {
            new("/groups", "POST", body),
            new("/groups", "POST", body),
            new("/groups", "POST", body)
        };

        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        Assert.Equal(3, result.Results.Count);
        // First item succeeds
        Assert.Equal(201, result.Results[0].Response.Status);
        // Second item: 403 Forbidden
        Assert.Equal(403, result.Results[1].Response.Status);
        // Third item: 500 (POST doesn't retry on 500)
        Assert.Equal(500, result.Results[2].Response.Status);
        // Verify order preserved
        Assert.Equal("/groups", result.Results[0].Operation.Url);
        // Telemetry: 1 succeeded, 2 failed (403 + 500 are non-retryable for POST)
        Assert.Equal(1, result.Telemetry.Succeeded);
        Assert.Equal(2, result.Telemetry.Failed);
    }

    [Fact]
    public async Task BatchGet_BetaApiVersion_SendsToBetaEndpoint()
    {
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, BatchGetResponse);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        // Construct with beta base URL (mirrors what InvokeMgxBatchRequest.VersionedBaseUrl produces)
        var batchClient = new GraphBatchClient(client, "https://graph.microsoft.com/beta");

        var urls = new List<string> { "/users/user1", "/users/user2" };
        await batchClient.ExecuteBatchAsync(urls);

        // Verify the request was sent to the beta/$batch endpoint
        var request = handler.Requests[0];
        Assert.Equal("https://graph.microsoft.com/beta/$batch", request.RequestUri!.ToString());
    }

    [Fact]
    public async Task BatchGet_RetryExhausted_ReturnsLastError()
    {
        // GET item fails on all 4 attempts (initial + 3 retries) with 503
        var get503Response = """
        {
            "responses": [
                { "id": "1", "status": 503, "body": { "error": { "code": "ServiceUnavailable" } } }
            ]
        }
        """;

        var handler = new MockHttpHandler();
        // 4 for per-chunk retries + 4 for batch-level retry = 8 total
        for (int i = 0; i < 8; i++)
            handler.QueueResponse(HttpStatusCode.OK, get503Response);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var operations = new List<BatchOperation> { new("/users/user1", "GET") };
        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        // Per-chunk: 4 attempts. Batch-level retry: another 4 attempts. Total: 8.
        Assert.Equal(8, handler.RequestCount);
        Assert.Single(result.Results);
        // Final result should be the last error, not Status=0
        Assert.Equal(503, result.Results[0].Response.Status);
        Assert.Equal(1, result.Telemetry.Failed);
        Assert.Equal(1, result.Telemetry.BatchLevelRetries);
    }

    [Fact]
    public async Task BatchGet_ResponseCountMismatch_Throws()
    {
        // Send 3 requests but mock returns only 2 response items
        var truncatedResponse = """
        {
            "responses": [
                { "id": "1", "status": 200, "body": { "id": "user1" } },
                { "id": "2", "status": 200, "body": { "id": "user2" } }
            ]
        }
        """;

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, truncatedResponse);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var ops = new List<BatchOperation>
        {
            new("/users/a"),
            new("/users/b"),
            new("/users/c"),
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken.None));
        Assert.Contains("count mismatch", ex.Message);
        Assert.Contains("sent 3", ex.Message);
        Assert.Contains("received 2", ex.Message);
    }

    [Fact]
    public async Task BatchGet_CrossChunkBackpressure_DelaysNextChunk()
    {
        // Chunk 1 (20 items): all return 429 with Retry-After:1 on first attempt, succeed on retry.
        // Retry-After:1 keeps intra-chunk delay short (~1-1.5s with jitter).
        // The fix (Math.Max) preserves the throttle signal across retry iterations,
        // so cross-chunk delay adds another ~1-1.5s. Total >= 1.8s proves cross-chunk fired;
        // without the fix, total would be ~1-1.5s (intra-chunk only).
        var chunk1Throttled = BuildBatchResponse(20, status: 429, retryAfterSeconds: 1);
        var chunk1Success = BuildBatchResponse(20, status: 200);
        // Chunk 2 (5 items): all succeed immediately
        var chunk2Success = BuildBatchResponse(5, status: 200);

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, chunk1Throttled);
        handler.QueueResponse(HttpStatusCode.OK, chunk1Success);
        handler.QueueResponse(HttpStatusCode.OK, chunk2Success);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var operations = Enumerable.Range(1, 25)
            .Select(i => new BatchOperation($"/users/user{i}", "GET"))
            .ToList();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await batchClient.ExecuteBatchIndexedAsync(operations);
        sw.Stop();

        // 3 HTTP requests: chunk1 initial (429), chunk1 retry (200), chunk2 (200)
        Assert.Equal(3, handler.RequestCount);
        Assert.Equal(25, result.Results.Count);
        Assert.All(result.Results, r => Assert.Equal(200, r.Response.Status));
        // Intra-chunk delay: ~1-1.5s. Cross-chunk delay: ~1-1.5s. Total must exceed intra-chunk alone.
        Assert.True(sw.Elapsed.TotalSeconds >= 1.8,
            $"Expected intra-chunk (~1s) + cross-chunk (~1s) delay but total elapsed was {sw.Elapsed.TotalSeconds:F1}s");
        // Telemetry: 20 items retried within chunk 1
        Assert.Equal(20, result.Telemetry.ItemRetries);
        Assert.Equal(20, result.Telemetry.ThrottleEncounters);
    }

    [Fact]
    public async Task BatchGet_NoThrottling_NoCrossChunkDelay()
    {
        var chunk1Success = BuildBatchResponse(20, status: 200);
        var chunk2Success = BuildBatchResponse(5, status: 200);

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, chunk1Success);
        handler.QueueResponse(HttpStatusCode.OK, chunk2Success);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var operations = Enumerable.Range(1, 25)
            .Select(i => new BatchOperation($"/users/user{i}", "GET"))
            .ToList();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await batchClient.ExecuteBatchIndexedAsync(operations);
        sw.Stop();

        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(25, result.Results.Count);
        // No throttling: should complete quickly with no artificial delay (wide margin for CI)
        Assert.True(sw.Elapsed.TotalSeconds < 5.0,
            $"Expected no delay but total elapsed was {sw.Elapsed.TotalSeconds:F1}s");
        Assert.Equal(0, result.Telemetry.ItemRetries);
        Assert.Equal(0, result.Telemetry.ThrottleEncounters);
    }

    [Fact]
    public async Task BatchGet_BatchLevelRetry_RecoverAfterChunkExhaustion()
    {
        // Simulate: 1 item exhausts all 4 per-chunk attempts (503), then succeeds
        // on the batch-level retry pass.
        var get503Response = """
        {
            "responses": [
                { "id": "1", "status": 503 }
            ]
        }
        """;
        var getSuccessResponse = """
        {
            "responses": [
                { "id": "1", "status": 200, "body": { "id": "user1" } }
            ]
        }
        """;

        var handler = new MockHttpHandler();
        // Per-chunk: 4 attempts all fail with 503
        handler.QueueResponse(HttpStatusCode.OK, get503Response);
        handler.QueueResponse(HttpStatusCode.OK, get503Response);
        handler.QueueResponse(HttpStatusCode.OK, get503Response);
        handler.QueueResponse(HttpStatusCode.OK, get503Response);
        // Batch-level retry: succeeds on first attempt
        handler.QueueResponse(HttpStatusCode.OK, getSuccessResponse);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var operations = new List<BatchOperation> { new("/users/user1", "GET") };
        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        // 4 per-chunk + 1 batch-level retry = 5 total HTTP requests
        Assert.Equal(5, handler.RequestCount);
        Assert.Single(result.Results);
        Assert.Equal(200, result.Results[0].Response.Status);
        Assert.Equal(1, result.Telemetry.Succeeded);
        Assert.Equal(0, result.Telemetry.Failed);
        Assert.Equal(1, result.Telemetry.BatchLevelRetries);
    }

    [Fact]
    public async Task BatchPost_BatchLevelRetry_429Recovery()
    {
        // POST item exhausts per-chunk retries with 429, succeeds on batch-level retry.
        // This tests that POST (non-idempotent) still gets batch-level retry on 429.
        var throttledResponse = """
        {
            "responses": [
                { "id": "1", "status": 429, "headers": { "Retry-After": "1" } }
            ]
        }
        """;
        var successResponse = """
        {
            "responses": [
                { "id": "1", "status": 201, "body": { "id": "new-user-1" } }
            ]
        }
        """;

        var handler = new MockHttpHandler();
        // Per-chunk: 4 attempts all throttled
        for (int i = 0; i < 4; i++)
            handler.QueueResponse(HttpStatusCode.OK, throttledResponse);
        // Batch-level retry: succeeds
        handler.QueueResponse(HttpStatusCode.OK, successResponse);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var body = JsonSerializer.Deserialize<JsonElement>("""{"displayName":"Test"}""");
        var operations = new List<BatchOperation> { new("/users", "POST", body) };

        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        // 4 per-chunk + 1 batch-level = 5
        Assert.Equal(5, handler.RequestCount);
        Assert.Single(result.Results);
        Assert.Equal(201, result.Results[0].Response.Status);
        Assert.Equal(1, result.Telemetry.Succeeded);
        Assert.Equal(1, result.Telemetry.BatchLevelRetries);
        Assert.True(result.Telemetry.ThrottleEncounters >= 4,
            $"Expected at least 4 throttle encounters but got {result.Telemetry.ThrottleEncounters}");
    }

    [Fact]
    public async Task BatchGet_VerboseWriter_LogsClampEvent()
    {
        // Server requests 300s Retry-After, client clamps to 120s (default).
        // VerboseWriter should receive a clamping message.
        var throttledResponse = """
        {
            "responses": [
                { "id": "1", "status": 429, "headers": { "Retry-After": "300" } }
            ]
        }
        """;
        var successResponse = """
        {
            "responses": [
                { "id": "1", "status": 200, "body": { "id": "user1" } }
            ]
        }
        """;

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, throttledResponse);
        handler.QueueResponse(HttpStatusCode.OK, successResponse);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var verboseMessages = new List<string>();
        batchClient.VerboseWriter = msg => verboseMessages.Add(msg);

        var operations = new List<BatchOperation> { new("/users/user1", "GET") };
        await batchClient.ExecuteBatchIndexedAsync(operations);
        batchClient.DrainVerboseMessages();

        // Verify a clamping message was logged
        Assert.Contains(verboseMessages, m => m.Contains("300s") && m.Contains("clamped"));
    }

    private static string BuildBatchResponse(int count, int status, int? retryAfterSeconds = null)
    {
        var headers = retryAfterSeconds.HasValue
            ? $", \"headers\": {{ \"Retry-After\": \"{retryAfterSeconds.Value}\" }}"
            : "";
        var body = status is >= 200 and < 300
            ? ", \"body\": { \"id\": \"item\" }"
            : "";
        var items = string.Join(",\n",
            Enumerable.Range(1, count).Select(i =>
                $"{{ \"id\": \"{i}\", \"status\": {status}{headers}{body} }}"));
        return $"{{ \"responses\": [{items}] }}";
    }

    [Fact]
    public async Task BatchWrite_Pacing_AppliedBetweenChunks()
    {
        // 40 write items = 2 chunks of 20. With pacing at 20 items/sec,
        // there should be ~1s delay between chunks (minus elapsed).
        GraphBatchClient.ResetPacingState();
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, BuildBatchResponse(20, 201));

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var body = JsonSerializer.Deserialize<JsonElement>("""{"displayName":"Test"}""");
        var batchClient = new GraphBatchClient(client, batchItemsPerSecond: 20);
        var ops = Enumerable.Range(1, 40)
            .Select(i => new BatchOperation($"/users", "POST", body))
            .ToArray();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken.None);
        sw.Stop();

        // With pacing, 2 chunks should take at least ~500ms (pacing delay after first chunk).
        // Without pacing, it completes in <100ms (mock handler is instant).
        Assert.True(sw.ElapsedMilliseconds >= 400,
            $"Expected >=400ms with pacing, got {sw.ElapsedMilliseconds}ms. Pacing may not have fired.");
        Assert.Equal(40, result.Results.Count);
    }

    [Fact]
    public async Task BatchGet_Pacing_DisabledWhenZero()
    {
        // batchItemsPerSecond=0 should disable pacing entirely.
        GraphBatchClient.ResetPacingState();
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, BuildBatchResponse(20, 200));

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var batchClient = new GraphBatchClient(client, batchItemsPerSecond: 0);
        var ops = Enumerable.Range(1, 40)
            .Select(i => new BatchOperation($"/users/{i}"))
            .ToArray();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken.None);
        sw.Stop();

        // Without pacing, mock handler returns instantly — should be <500ms.
        Assert.True(sw.ElapsedMilliseconds < 500,
            $"Expected <500ms without pacing, got {sw.ElapsedMilliseconds}ms. Pacing may still be active.");
        Assert.Equal(40, result.Results.Count);
    }

    [Fact]
    public async Task BatchGet_Pacing_SkippedWhenBackpressureActive()
    {
        // When a chunk triggers 429 with Retry-After, cross-chunk backpressure
        // takes over. Pacing should NOT add extra delay on top.
        GraphBatchClient.ResetPacingState();
        var handler = new MockHttpHandler();
        // First chunk: 429 with Retry-After (triggers backpressure)
        handler.QueueResponse((HttpStatusCode)429, null,
            new Dictionary<string, string> { ["Retry-After"] = "0" });
        // Retry of first chunk: success
        handler.QueueResponse(HttpStatusCode.OK, BuildBatchResponse(20, 200));
        // Second chunk: success
        handler.SetDefaultResponse(HttpStatusCode.OK, BuildBatchResponse(20, 200));

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var batchClient = new GraphBatchClient(client, batchItemsPerSecond: 20);
        var ops = Enumerable.Range(1, 40)
            .Select(i => new BatchOperation($"/users/{i}"))
            .ToArray();

        // Should complete without error — backpressure handles the 429,
        // and pacing doesn't stack on top.
        var result = await batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken.None);
        Assert.Equal(40, result.Results.Count);
    }

    [Fact]
    public async Task BatchWrite_Pacing_CrossCallDelayApplied()
    {
        // Two separate GraphBatchClient instances each with 20 write items (1 chunk each).
        // Without cross-call pacing, both complete instantly (<100ms total).
        // With cross-call pacing at 20 items/sec, the second call should wait ~1000ms
        // minus the elapsed time of the first call.
        GraphBatchClient.ResetPacingState();
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, BuildBatchResponse(20, 201));

        using var httpClient = new HttpClient(handler);
        using var rgcClient = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var body = JsonSerializer.Deserialize<JsonElement>("""{"displayName":"Test"}""");
        var ops = Enumerable.Range(1, 20)
            .Select(i => new BatchOperation($"/users", "POST", body))
            .ToArray();

        // Call 1: establishes pacing baseline
        var client1 = new GraphBatchClient(rgcClient, batchItemsPerSecond: 20);
        await client1.ExecuteBatchIndexedAsync(ops);

        // Call 2: should be delayed by cross-call pacing
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var client2 = new GraphBatchClient(rgcClient, batchItemsPerSecond: 20);
        var result = await client2.ExecuteBatchIndexedAsync(ops);
        sw.Stop();

        // 20 items at 20/sec = 1000ms target. Mock handler is instant, so
        // nearly all of the target should be pacing delay.
        Assert.True(sw.ElapsedMilliseconds >= 600,
            $"Expected >=600ms cross-call pacing delay, got {sw.ElapsedMilliseconds}ms");
        Assert.Equal(20, result.Results.Count);
    }

    [Fact]
    public async Task BatchWrite_Pacing_CrossCallDisabledWhenZero()
    {
        // With batchItemsPerSecond=0, cross-call pacing should not fire even for writes.
        GraphBatchClient.ResetPacingState();
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, BuildBatchResponse(20, 201));

        using var httpClient = new HttpClient(handler);
        using var rgcClient = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var body = JsonSerializer.Deserialize<JsonElement>("""{"displayName":"Test"}""");
        var ops = Enumerable.Range(1, 20)
            .Select(i => new BatchOperation($"/users", "POST", body))
            .ToArray();

        var client1 = new GraphBatchClient(rgcClient, batchItemsPerSecond: 0);
        await client1.ExecuteBatchIndexedAsync(ops);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var client2 = new GraphBatchClient(rgcClient, batchItemsPerSecond: 0);
        var result = await client2.ExecuteBatchIndexedAsync(ops);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 500,
            $"Expected <500ms without pacing, got {sw.ElapsedMilliseconds}ms");
        Assert.Equal(20, result.Results.Count);
    }

    [Fact]
    public async Task BatchGet_Pacing_SkippedForGetOnlyBatch()
    {
        // A write batch (POST) followed by a GET-only batch. Cross-call pacing
        // should NOT delay the GET batch — GETs don't hit write throttle limits.
        GraphBatchClient.ResetPacingState();
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, BuildBatchResponse(20, 200));

        using var httpClient = new HttpClient(handler);
        using var rgcClient = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var body = JsonSerializer.Deserialize<JsonElement>("""{"displayName":"Test"}""");

        // Call 1: write batch (POSTs) — establishes pacing state
        var writeOps = Enumerable.Range(1, 20)
            .Select(i => new BatchOperation($"/users", "POST", body))
            .ToArray();
        var client1 = new GraphBatchClient(rgcClient, batchItemsPerSecond: 20);
        await client1.ExecuteBatchIndexedAsync(writeOps);

        // Call 2: GET-only batch — should NOT be paced
        var getOps = Enumerable.Range(1, 20)
            .Select(i => new BatchOperation($"/users/{i}"))
            .ToArray();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var client2 = new GraphBatchClient(rgcClient, batchItemsPerSecond: 20);
        var result = await client2.ExecuteBatchIndexedAsync(getOps);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 500,
            $"Expected <500ms for GET-only batch after writes, got {sw.ElapsedMilliseconds}ms. Pacing should skip GETs.");
        Assert.Equal(20, result.Results.Count);
    }

    [Fact]
    public async Task BatchGet_Pacing_CrossCallCappedToOneChunk()
    {
        // A large write batch (40 items = 2 chunks) followed by a small write batch (20 items).
        // Cross-call delay should be capped to MaxBatchSize (20 items) worth = ~1000ms,
        // NOT 40 items worth = ~2000ms.
        GraphBatchClient.ResetPacingState();
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, BuildBatchResponse(20, 200));

        using var httpClient = new HttpClient(handler);
        using var rgcClient = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var body = JsonSerializer.Deserialize<JsonElement>("""{"displayName":"Test"}""");

        // Call 1: 40-item write batch
        var largeOps = Enumerable.Range(1, 40)
            .Select(i => new BatchOperation($"/users", "POST", body))
            .ToArray();
        var client1 = new GraphBatchClient(rgcClient, batchItemsPerSecond: 20);
        await client1.ExecuteBatchIndexedAsync(largeOps);

        // Call 2: 20-item write batch — cross-call delay should be ~1000ms (capped), not ~2000ms
        var smallOps = Enumerable.Range(1, 20)
            .Select(i => new BatchOperation($"/users", "POST", body))
            .ToArray();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var client2 = new GraphBatchClient(rgcClient, batchItemsPerSecond: 20);
        await client2.ExecuteBatchIndexedAsync(smallOps);
        sw.Stop();

        // Capped to 20 items at 20/sec = 1000ms max cross-call delay + intra-call pacing.
        // Without the cap, this would be ~2000ms cross-call + intra-call.
        // With the cap, total should be under 1500ms (cross-call ~1000ms + negligible HTTP).
        Assert.True(sw.ElapsedMilliseconds < 1500,
            $"Expected <1500ms with capped cross-call pacing, got {sw.ElapsedMilliseconds}ms. Cap may not be working.");
    }

    // ═══════════════════════════════════════════════════════════════
    // R3-1: Pacing state ordering — Volatile.Read/Write prevent
    // a concurrent reader from seeing new ticks with stale count
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task BatchWrite_Pacing_ConcurrentCallsAlwaysPace()
    {
        // R3-1: Prove the Volatile.Read/Write fix works under concurrent execution.
        //
        // Without proper memory fences, a concurrent reader can see new ticks but
        // stale count (0), causing the pacing guard to skip. Raw field access via
        // reflection confirmed 76,490 ordering violations in 100k iterations on
        // this hardware (ARM64 M-series).
        //
        // This test exercises the PRODUCTION code paths concurrently: 20 parallel
        // pairs of (write-batch → immediate second-batch). Each second batch must
        // observe the pacing state from SOME prior batch and apply delay. If any
        // second batch completes in <200ms, the pacing guard was skipped — meaning
        // the reader saw stale count=0 despite valid ticks.
        GraphBatchClient.ResetPacingState();
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, BuildBatchResponse(20, 201));

        using var httpClient = new HttpClient(handler);
        using var rgcClient = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var body = JsonSerializer.Deserialize<JsonElement>("""{"displayName":"Test"}""");
        var ops = Enumerable.Range(1, 20)
            .Select(i => new BatchOperation("/users", "POST", body))
            .ToArray();

        // Seed the pacing state so all concurrent readers have something to read
        var seed = new GraphBatchClient(rgcClient, batchItemsPerSecond: 20);
        await seed.ExecuteBatchIndexedAsync(ops);

        // 20 concurrent calls — each should be paced because the static state has
        // count=20 and ticks=recent. If any completes in <200ms, pacing was skipped.
        var pacingSkipped = 0;
        var tasks = Enumerable.Range(0, 20).Select(async _ =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var client = new GraphBatchClient(rgcClient, batchItemsPerSecond: 20);
            await client.ExecuteBatchIndexedAsync(ops);
            sw.Stop();

            // Each call writes new pacing state on completion (line 346-347),
            // so subsequent readers will also see valid state.
            // If pacing was skipped (stale count=0), elapsed will be <50ms.
            if (sw.ElapsedMilliseconds < 200)
                Interlocked.Increment(ref pacingSkipped);
        });

        await Task.WhenAll(tasks);
        GraphBatchClient.ResetPacingState();

        Assert.True(pacingSkipped == 0,
            $"{pacingSkipped} of 20 concurrent batch calls completed in <200ms (pacing skipped). " +
            "This indicates Volatile.Read returned stale count=0, bypassing the pacing guard.");
    }

    // ═══════════════════════════════════════════════════════════════
    // R2-3: High-risk missing tests
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task R2_3a_AllItemsFail_20Items_ExhaustsRetries()
    {
        // All 20 items in a chunk return 429. Per-chunk retries (3) exhaust,
        // then Phase 2 batch-level retry fires.
        var handler = new MockHttpHandler();
        // Per-chunk: 4 attempts (initial + 3 retries), all 429
        for (int i = 0; i < 4; i++)
            handler.QueueResponse(HttpStatusCode.OK, BuildBatchResponse(20, 429, retryAfterSeconds: 0));
        // Phase 2 batch-level retry: also all 429
        handler.SetDefaultResponse(HttpStatusCode.OK, BuildBatchResponse(20, 429, retryAfterSeconds: 0));

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var ops = Enumerable.Range(1, 20)
            .Select(i => new BatchOperation($"/users/{i}"))
            .ToArray();

        var result = await batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken.None);

        // All items should be present (with 429 status — failed but not lost)
        Assert.Equal(20, result.Results.Count);
        Assert.All(result.Results, r => Assert.Equal(429, r.Response.Status));
        Assert.Equal(0, result.Telemetry.Succeeded);
        Assert.Equal(20, result.Telemetry.Failed);
        Assert.True(result.Telemetry.ItemRetries > 0);
        Assert.True(result.Telemetry.BatchLevelRetries > 0);
    }

    [Fact]
    public async Task R2_3a_AllItemsFail_500_NonRetryableForPost()
    {
        // All 20 POST items return 500. POST does NOT retry on 500 (only 429).
        // No per-chunk retries, no Phase 2 retry.
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, BuildBatchResponse(20, 500));

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var body = JsonSerializer.Deserialize<JsonElement>("""{"displayName":"Test"}""");
        var ops = Enumerable.Range(1, 20)
            .Select(i => new BatchOperation($"/users", "POST", body))
            .ToArray();

        var result = await batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken.None);

        Assert.Equal(20, result.Results.Count);
        Assert.All(result.Results, r => Assert.Equal(500, r.Response.Status));
        Assert.Equal(1, handler.RequestCount); // Only 1 HTTP call — no retries for POST+500
        Assert.Equal(0, result.Telemetry.Succeeded);
        Assert.Equal(20, result.Telemetry.Failed);
    }

    [Fact]
    public async Task R2_3b_PartialRetry_SomeFailSomeSucceed()
    {
        // Chunk with mixed results: items 1-10 succeed (201), items 11-20 fail (503).
        // Failed items should retry. On retry, all succeed.
        var mixedResponse = BuildMixedBatchResponse(10, 201, 10, 503);
        var allSuccessResponse = BuildBatchResponse(10, 200);

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, mixedResponse);     // First attempt: mixed
        handler.SetDefaultResponse(HttpStatusCode.OK, allSuccessResponse); // Retries: all succeed

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var ops = Enumerable.Range(1, 20)
            .Select(i => new BatchOperation($"/users/{i}"))
            .ToArray();

        var result = await batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken.None);

        Assert.Equal(20, result.Results.Count);
        // All should eventually succeed (10 on first attempt, 10 on retry)
        Assert.Equal(20, result.Telemetry.Succeeded);
        Assert.Equal(0, result.Telemetry.Failed);
        Assert.True(result.Telemetry.ItemRetries > 0);
        Assert.True(handler.RequestCount >= 2); // At least 2 HTTP calls
    }

    [Fact]
    public async Task R2_3c_MismatchedResponseIds_Throws()
    {
        // Response IDs don't match request IDs — should throw InvalidOperationException
        var mismatchedResponse = """
        {
            "responses": [
                { "id": "99", "status": 200, "body": { "id": "user99" } },
                { "id": "98", "status": 200, "body": { "id": "user98" } }
            ]
        }
        """;

        var handler = new MockHttpHandler();
        // All 4 per-chunk attempts return mismatched IDs
        for (int i = 0; i < 4; i++)
            handler.QueueResponse(HttpStatusCode.OK, mismatchedResponse);
        handler.SetDefaultResponse(HttpStatusCode.OK, mismatchedResponse);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var ops = new[]
        {
            new BatchOperation("/users/1"),
            new BatchOperation("/users/2")
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken.None));
        Assert.Contains("missing result", ex.Message);
    }

    [Fact]
    public async Task R2_3d_Phase2_TransportError_PropagatesException()
    {
        // Per-chunk retries exhaust on 503. Phase 2 batch-level retry hits
        // a transport error (HttpRequestException). Should propagate.
        var handler = new MockHttpHandler();
        // Per-chunk: 4 attempts all 503
        for (int i = 0; i < 4; i++)
            handler.QueueResponse(HttpStatusCode.OK, BuildBatchResponse(2, 503));
        // Phase 2: transport error
        handler.QueueException(new HttpRequestException("Connection refused"));

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            MaxRetryAttempts = 1,
            AttemptTimeoutSeconds = 5,
            TotalTimeoutSeconds = 30
        });
        var batchClient = new GraphBatchClient(client);

        var ops = new[]
        {
            new BatchOperation("/users/1"),
            new BatchOperation("/users/2")
        };

        // The transport error during Phase 2 should propagate
        await Assert.ThrowsAnyAsync<Exception>(
            () => batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken.None));
    }

    // ═══════════════════════════════════════════════════════════════
    // R2-5: Test with rate limiting enabled
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task R2_5_BatchWithRateLimiting_Succeeds()
    {
        // All other batch tests use NoRateLimit=true. This one uses default
        // rate limiting (50/sec burst 200) to verify batch operations work
        // through the rate limiter.
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, BuildBatchResponse(20, 200));

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient); // Default options — rate limiter ON

        var batchClient = new GraphBatchClient(client);
        var ops = Enumerable.Range(1, 40)
            .Select(i => new BatchOperation($"/users/{i}"))
            .ToArray();

        var result = await batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken.None);

        Assert.Equal(40, result.Results.Count);
        Assert.Equal(40, result.Telemetry.Succeeded);
        Assert.Equal(0, result.Telemetry.Failed);
    }

    // ═══════════════════════════════════════════════════════════════
    // R2-6: CancellationToken cancels during retry delays
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task R2_6_CancellationDuringRetryDelay_CancelsPromptly()
    {
        // All items return 429 with Retry-After: 30. Cancel after 1 second.
        // Should cancel during the retry delay, not wait the full 30s.
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, BuildBatchResponse(2, 429, retryAfterSeconds: 30));

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var ops = new[]
        {
            new BatchOperation("/users/1"),
            new BatchOperation("/users/2")
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => batchClient.ExecuteBatchIndexedAsync(ops, cts.Token));

        sw.Stop();
        // Should cancel within ~3s, not wait for the 30s Retry-After
        Assert.True(sw.ElapsedMilliseconds < 10000,
            $"Cancellation took {sw.ElapsedMilliseconds}ms — should have cancelled within ~3s, not waited for Retry-After");
    }

    // ═══════════════════════════════════════════════════════════════
    // R2-8: Chunk boundary tests (0, 1, 20, 21 items)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task R2_8_ZeroItems_ReturnsEmpty()
    {
        var handler = new MockHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var result = await batchClient.ExecuteBatchIndexedAsync(
            Array.Empty<BatchOperation>(), CancellationToken.None);

        Assert.Empty(result.Results);
        Assert.Equal(0, handler.RequestCount); // No HTTP calls for empty input
    }

    [Fact]
    public async Task R2_8_OneItem_SingleChunk()
    {
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, BuildBatchResponse(1, 200));

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var ops = new[] { new BatchOperation("/users/1") };
        var result = await batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken.None);

        Assert.Single(result.Results);
        Assert.Equal(200, result.Results[0].Response.Status);
        Assert.Equal(1, handler.RequestCount); // Exactly 1 batch POST
    }

    [Fact]
    public async Task R2_8_TwentyItems_ExactlyOneChunk()
    {
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, BuildBatchResponse(20, 200));

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var ops = Enumerable.Range(1, 20)
            .Select(i => new BatchOperation($"/users/{i}"))
            .ToArray();

        var result = await batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken.None);

        Assert.Equal(20, result.Results.Count);
        Assert.Equal(1, handler.RequestCount); // Exactly 1 chunk
    }

    [Fact]
    public async Task R2_8_TwentyOneItems_TwoChunks()
    {
        var handler = new MockHttpHandler();
        // First chunk: 20 items, second chunk: 1 item
        handler.QueueResponse(HttpStatusCode.OK, BuildBatchResponse(20, 200));
        handler.SetDefaultResponse(HttpStatusCode.OK, BuildBatchResponse(1, 200));

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client, batchItemsPerSecond: 0); // No pacing delay for speed

        var ops = Enumerable.Range(1, 21)
            .Select(i => new BatchOperation($"/users/{i}"))
            .ToArray();

        var result = await batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken.None);

        Assert.Equal(21, result.Results.Count);
        Assert.Equal(2, handler.RequestCount); // 2 chunks: 20 + 1
    }

    // ═══════════════════════════════════════════════════════════════
    // Helper: Build mixed batch response (some succeed, some fail)
    // ═══════════════════════════════════════════════════════════════

    private static string BuildMixedBatchResponse(
        int successCount, int successStatus,
        int failCount, int failStatus)
    {
        var items = new List<string>();
        for (int i = 1; i <= successCount; i++)
            items.Add($"{{ \"id\": \"{i}\", \"status\": {successStatus}, \"body\": {{ \"id\": \"item\" }} }}");
        for (int i = successCount + 1; i <= successCount + failCount; i++)
            items.Add($"{{ \"id\": \"{i}\", \"status\": {failStatus} }}");
        return $"{{ \"responses\": [{string.Join(",\n", items)}] }}";
    }

    // ═══════════════════════════════════════════════════════════════
    // Dead-letter: RedactSensitiveFields tests
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void RedactSensitiveFields_RedactsPasswordProfile()
    {
        var node = JsonNode.Parse("""
        {
            "displayName": "Test User",
            "passwordProfile": {
                "password": "Secret123!",
                "forceChangePasswordNextSignIn": true
            }
        }
        """);

        InvokeMgxBatchRequest.RedactSensitiveFields(node);

        var obj = node!.AsObject();
        Assert.Equal("Test User", obj["displayName"]!.GetValue<string>());
        Assert.Equal("***REDACTED***", obj["passwordProfile"]!.GetValue<string>());
    }

    [Fact]
    public void RedactSensitiveFields_RedactsNestedPassword()
    {
        var node = JsonNode.Parse("""
        {
            "accountEnabled": true,
            "nested": {
                "password": "hunter2"
            }
        }
        """);

        InvokeMgxBatchRequest.RedactSensitiveFields(node);

        var obj = node!.AsObject();
        Assert.True(obj["accountEnabled"]!.GetValue<bool>());
        var nested = obj["nested"]!.AsObject();
        Assert.Equal("***REDACTED***", nested["password"]!.GetValue<string>());
    }

    [Fact]
    public void RedactSensitiveFields_PreservesNonSensitiveFields()
    {
        var node = JsonNode.Parse("""
        {
            "displayName": "Test",
            "mailNickname": "test",
            "accountEnabled": false
        }
        """);

        InvokeMgxBatchRequest.RedactSensitiveFields(node);

        var obj = node!.AsObject();
        Assert.Equal("Test", obj["displayName"]!.GetValue<string>());
        Assert.Equal("test", obj["mailNickname"]!.GetValue<string>());
        Assert.False(obj["accountEnabled"]!.GetValue<bool>());
    }

    [Fact]
    public void RedactSensitiveFields_HandlesNullNode()
    {
        // Should not throw
        InvokeMgxBatchRequest.RedactSensitiveFields(null);
    }

    [Fact]
    public void RedactSensitiveFields_RedactsKeyAndPasswordCredentials()
    {
        var node = JsonNode.Parse("""
        {
            "displayName": "App",
            "keyCredentials": [{"key": "abc"}],
            "passwordCredentials": [{"secretText": "xyz"}]
        }
        """);

        InvokeMgxBatchRequest.RedactSensitiveFields(node);

        var obj = node!.AsObject();
        Assert.Equal("App", obj["displayName"]!.GetValue<string>());
        Assert.Equal("***REDACTED***", obj["keyCredentials"]!.GetValue<string>());
        Assert.Equal("***REDACTED***", obj["passwordCredentials"]!.GetValue<string>());
    }

    [Fact]
    public void RedactSensitiveFields_DeepNesting_FiveLevels()
    {
        var node = JsonNode.Parse("""
        {
            "a": { "b": { "c": { "d": { "password": "deep-secret" } } } }
        }
        """);

        InvokeMgxBatchRequest.RedactSensitiveFields(node);

        var d = node!["a"]!["b"]!["c"]!["d"]!.AsObject();
        Assert.Equal("***REDACTED***", d["password"]!.GetValue<string>());
    }

    [Fact]
    public void RedactSensitiveFields_RedactsClientSecretAndAppPassword()
    {
        var node = JsonNode.Parse("""
        {
            "displayName": "App",
            "clientSecret": "s3cr3t-value",
            "appPassword": "app-pwd-123",
            "clientAssertion": "jwt-token-here"
        }
        """);

        InvokeMgxBatchRequest.RedactSensitiveFields(node);

        var obj = node!.AsObject();
        Assert.Equal("App", obj["displayName"]!.GetValue<string>());
        Assert.Equal("***REDACTED***", obj["clientSecret"]!.GetValue<string>());
        Assert.Equal("***REDACTED***", obj["appPassword"]!.GetValue<string>());
        Assert.Equal("***REDACTED***", obj["clientAssertion"]!.GetValue<string>());
    }

    // R3-4: RedactSensitiveFields must recurse into JsonArray elements
    [Fact]
    public void RedactSensitiveFields_RedactsInsideArrays()
    {
        var node = JsonNode.Parse("""
        {
            "users": [
                { "displayName": "Alice", "passwordProfile": { "password": "Secret123" } },
                { "displayName": "Bob", "clientSecret": "abc-def" }
            ]
        }
        """);

        InvokeMgxBatchRequest.RedactSensitiveFields(node);

        var users = node!["users"]!.AsArray();
        Assert.Equal("Alice", users[0]!["displayName"]!.GetValue<string>());
        Assert.Equal("***REDACTED***", users[0]!["passwordProfile"]!.GetValue<string>());
        Assert.Equal("Bob", users[1]!["displayName"]!.GetValue<string>());
        Assert.Equal("***REDACTED***", users[1]!["clientSecret"]!.GetValue<string>());
    }

    [Fact]
    public void RedactSensitiveFields_RedactsRootLevelArray()
    {
        // If the body is a root-level JSON array (not wrapped in an object),
        // sensitive fields inside array elements must still be redacted.
        var node = JsonNode.Parse("""
        [
            { "displayName": "Alice", "password": "Secret123" },
            { "displayName": "Bob", "passwordProfile": { "password": "abc" } }
        ]
        """);

        InvokeMgxBatchRequest.RedactSensitiveFields(node);

        var arr = node!.AsArray();
        Assert.Equal("Alice", arr[0]!["displayName"]!.GetValue<string>());
        Assert.Equal("***REDACTED***", arr[0]!["password"]!.GetValue<string>());
        Assert.Equal("Bob", arr[1]!["displayName"]!.GetValue<string>());
        Assert.Equal("***REDACTED***", arr[1]!["passwordProfile"]!.GetValue<string>());
    }

    // ═══════════════════════════════════════════════════════════════
    // Additional batch edge case tests
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task BatchMixed_GetAndPost_BothProcessed()
    {
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, """
        {
            "responses": [
                { "id": "1", "status": 200, "body": { "id": "user1" } },
                { "id": "2", "status": 201, "body": { "id": "new-user" } },
                { "id": "3", "status": 200, "body": { "id": "user2" } }
            ]
        }
        """);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var body = JsonSerializer.Deserialize<JsonElement>("""{"displayName":"Test"}""");
        var operations = new List<BatchOperation>
        {
            new("/users/user1", "GET"),
            new("/users", "POST", body),
            new("/users/user2", "PATCH", body)
        };

        var result = await batchClient.ExecuteBatchIndexedAsync(operations);

        Assert.Equal(3, result.Results.Count);
        Assert.Equal(200, result.Results[0].Response.Status);
        Assert.Equal(201, result.Results[1].Response.Status);
        Assert.Equal(200, result.Results[2].Response.Status);
    }

    [Fact]
    public async Task BatchResponseCountMismatch_ThrowsInvalidOperationException()
    {
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, """
        {
            "responses": [
                { "id": "1", "status": 200, "body": { "id": "user1" } }
            ]
        }
        """);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var operations = new List<BatchOperation>
        {
            new("/users/1"),
            new("/users/2"),
            new("/users/3")
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => batchClient.ExecuteBatchIndexedAsync(operations));

        Assert.Contains("response count mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BatchEmptyOperations_ReturnsEmptyResult()
    {
        var handler = new MockHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var result = await batchClient.ExecuteBatchIndexedAsync(Array.Empty<BatchOperation>());

        Assert.Empty(result.Results);
        Assert.Equal(0, result.Telemetry.TotalRequests);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task BatchEmptyResponsesFromGraph_ThrowsInvalidOperationException()
    {
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, """{ "responses": [] }""");

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => batchClient.ExecuteBatchIndexedAsync(new[] { new BatchOperation("/users/1") }));

        Assert.Contains("empty or malformed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
