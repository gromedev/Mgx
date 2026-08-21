using System.Net;
using System.Text.Json;
using Mgx.Engine.Http;
using Mgx.Engine.Models;

namespace Mgx.IntegrationTests;

[Collection("Pipeline")]
public class SpeedWinsTests
{
    // --- T2-1: ResponseHeadersRead ---

    [Fact]
    public async Task ResponseHeadersRead_StreamingResponseStillReadable()
    {
        // With HttpCompletionOption.ResponseHeadersRead, the response body is streamed
        // on demand. Verify that ReadAsStringAsync still works correctly.
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("displayName", body);
    }

    // --- T2-3: client-request-id ---

    [Fact]
    public async Task ClientRequestId_PresentOnEveryRequest()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");

        var request = handler.Requests[0];
        Assert.True(request.Headers.Contains("client-request-id"),
            "client-request-id header must be present on every request");
        var requestId = request.Headers.GetValues("client-request-id").First();
        Assert.True(Guid.TryParse(requestId, out _), $"client-request-id must be a valid GUID, got: {requestId}");
    }

    [Fact]
    public async Task ClientRequestId_UniquePerRequest()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");
        await client.GetAsync("https://graph.microsoft.com/v1.0/users/user2");

        var id1 = handler.Requests[0].Headers.GetValues("client-request-id").First();
        var id2 = handler.Requests[1].Headers.GetValues("client-request-id").First();
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public async Task ClientRequestId_SameAcrossRetries()
    {
        // The client-request-id should be the SAME across retry attempts for correlation.
        // This lets ops trace all retry attempts of a single logical request in Graph logs.
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.QueueResponse((HttpStatusCode)429, null, new() { ["Retry-After"] = "0" });
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");

        // Both the initial attempt and the retry should have the same client-request-id
        var id1 = handler.Requests[0].Headers.GetValues("client-request-id").First();
        var id2 = handler.Requests[1].Headers.GetValues("client-request-id").First();
        Assert.Equal(id1, id2);
    }

    // --- T2-2: BatchChunkConcurrency ---

    [Fact]
    public async Task BatchChunkConcurrency_Default1_SequentialExecution()
    {
        var batchResponse = """
        {
            "responses": [
                { "id": "1", "status": 200, "body": { "id": "user1" } },
                { "id": "2", "status": 200, "body": { "id": "user2" } }
            ]
        }
        """;
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, batchResponse);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        // Default concurrency = 1 (sequential)
        var batchClient = new GraphBatchClient(client, batchChunkConcurrency: 1);

        var ops = Enumerable.Range(0, 2).Select(i => new BatchOperation($"/users/user{i + 1}")).ToList();
        var result = await batchClient.ExecuteBatchIndexedAsync(ops);

        Assert.Equal(2, result.Results.Count);
        Assert.All(result.Results, r => Assert.Equal(200, r.Response.Status));
    }

    [Fact]
    public async Task BatchChunkConcurrency_Parallel_CorrectResultCount()
    {
        // 40 operations = 2 chunks of 20. With concurrency=3, both chunks run in parallel.
        var batchResponse = BuildBatchResponse(20, 200);
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, batchResponse);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client, batchChunkConcurrency: 3);

        var ops = Enumerable.Range(0, 40).Select(i => new BatchOperation($"/users/user{i}")).ToList();
        var result = await batchClient.ExecuteBatchIndexedAsync(ops);

        Assert.Equal(40, result.Results.Count);
        Assert.All(result.Results, r => Assert.Equal(200, r.Response.Status));
        Assert.Equal(2, handler.RequestCount); // 2 batch POST requests (2 chunks)
    }

    [Fact]
    public async Task BatchChunkConcurrency_Parallel_PreservesResultOrdering()
    {
        // Verify that parallel chunk execution preserves the original operation order.
        // If chunkOffsets has an off-by-one, results would silently misalign.
        var batchResponse = BuildBatchResponse(20, 200);
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, batchResponse);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client, batchChunkConcurrency: 3);

        var ops = Enumerable.Range(0, 40).Select(i => new BatchOperation($"/users/user{i}")).ToList();
        var result = await batchClient.ExecuteBatchIndexedAsync(ops);

        // Each result's Operation.Url must match the original operation at that index
        for (int i = 0; i < 40; i++)
        {
            Assert.Equal($"/users/user{i}", result.Results[i].Operation.Url);
        }
    }

    [Fact]
    public void BatchChunkConcurrency_RejectsInvalidValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ResilientGraphClientOptions { BatchChunkConcurrency = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ResilientGraphClientOptions { BatchChunkConcurrency = 11 });
    }

    [Fact]
    public void BatchChunkConcurrency_DefaultIs1()
    {
        var options = new ResilientGraphClientOptions();
        Assert.Equal(1, options.BatchChunkConcurrency);
    }

    private static string BuildBatchResponse(int count, int status)
    {
        var items = string.Join(",\n",
            Enumerable.Range(1, count).Select(i =>
                $"{{ \"id\": \"{i}\", \"status\": {status}, \"body\": {{ \"id\": \"item{i}\" }} }}"));
        return $"{{ \"responses\": [{items}] }}";
    }
}
