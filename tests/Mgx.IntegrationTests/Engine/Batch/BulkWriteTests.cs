using System.Net;
using Mgx.Engine.Http;
using Mgx.Engine.Models;
using Mgx.Engine.Pagination;
using Polly.CircuitBreaker;

namespace Mgx.IntegrationTests;

[Collection("Pipeline")]
public class BulkWriteTests
{
    private static readonly ResilientGraphClientOptions NoRateLimitOptions = new()
    {
        NoRateLimit = true,
        MaxRetryAttempts = 1,
        TotalTimeoutSeconds = 30,
        AttemptTimeoutSeconds = 10
    };

    [Fact]
    public async Task BulkWriteAsync_AllSucceed_ReturnsCorrectCounts()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();

        // 3 POSTs: all return 201 Created with response bodies
        handler.QueueResponse(HttpStatusCode.Created, TestData.SingleUser);
        handler.QueueResponse(HttpStatusCode.Created, TestData.SingleUser);
        handler.QueueResponse(HttpStatusCode.Created, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, NoRateLimitOptions);
        var fanOut = new ConcurrentFanOut(client, maxConcurrency: 2);

        var ops = new List<(string id, string url)>
        {
            ("op1", "https://graph.microsoft.com/v1.0/users"),
            ("op2", "https://graph.microsoft.com/v1.0/users"),
            ("op3", "https://graph.microsoft.com/v1.0/users")
        };

        var result = await fanOut.BulkWriteAsync(HttpMethod.Post, ops, """{"displayName":"Test"}""");

        Assert.Equal(3, result.Succeeded);
        Assert.Equal(0, result.Failed);
        Assert.Empty(result.Errors);
        Assert.Equal(3, result.Responses.Count);
        // Verify response bodies are actually deserialized, not empty/garbage
        Assert.All(result.Responses, r =>
            Assert.Equal("user1", r.Response.GetProperty("id").GetString()));

        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public async Task BulkWriteAsync_EmptyOperations_ReturnsEmptyResult()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, NoRateLimitOptions);
        var fanOut = new ConcurrentFanOut(client, maxConcurrency: 2);

        var ops = new List<(string id, string url)>();

        var result = await fanOut.BulkWriteAsync(HttpMethod.Post, ops, """{"displayName":"Test"}""");

        Assert.Equal(0, result.Succeeded);
        Assert.Equal(0, result.Failed);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Responses);
        Assert.Equal(0, handler.RequestCount);

        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public async Task BulkWriteAsync_CancellationPropagates()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new SlowMockHttpHandler(delayMs: 5000);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, NoRateLimitOptions);
        var fanOut = new ConcurrentFanOut(client, maxConcurrency: 2);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(100);

        var ops = new List<(string id, string url)>
        {
            ("op1", "https://graph.microsoft.com/v1.0/groups")
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fanOut.BulkWriteAsync(HttpMethod.Post, ops, """{"displayName":"Test"}""",
                cancellationToken: cts.Token));

        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public async Task BulkWriteAsync_SemaphoreReleasedOnFailure()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();

        // Op1 fails (500), op2 should still run (semaphore not leaked).
        // Uses POST because POST does NOT retry on 500 (only retries 429).
        // If changed to GET/PATCH, the retry would consume op2's queued response.
        handler.QueueResponse(HttpStatusCode.InternalServerError);
        handler.QueueResponse(HttpStatusCode.Created, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, NoRateLimitOptions);
        var fanOut = new ConcurrentFanOut(client, maxConcurrency: 1); // Only 1 slot

        var ops = new List<(string id, string url)>
        {
            ("op1", "https://graph.microsoft.com/v1.0/groups"),
            ("op2", "https://graph.microsoft.com/v1.0/groups")
        };

        var result = await fanOut.BulkWriteAsync(HttpMethod.Post, ops, """{"displayName":"Test"}""");

        // Op2 should succeed despite op1 failure (semaphore released correctly)
        Assert.Equal(1, result.Succeeded);
        Assert.Equal(1, result.Failed);
        Assert.Single(result.Errors);
        Assert.Equal("op1", result.Errors[0].Id);

        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public async Task BulkWriteAsync_NoContent204_NoResponseBody()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();

        // DELETE returns 204 No Content
        handler.QueueResponse(HttpStatusCode.NoContent);
        handler.QueueResponse(HttpStatusCode.NoContent);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, NoRateLimitOptions);
        var fanOut = new ConcurrentFanOut(client, maxConcurrency: 2);

        var ops = new List<(string id, string url)>
        {
            ("op1", "https://graph.microsoft.com/v1.0/groups/g1"),
            ("op2", "https://graph.microsoft.com/v1.0/groups/g2")
        };

        var result = await fanOut.BulkWriteAsync(HttpMethod.Delete, ops, null);

        Assert.Equal(2, result.Succeeded);
        Assert.Equal(0, result.Failed);
        Assert.Empty(result.Errors);
        // 204 No Content should not produce response bodies
        Assert.Empty(result.Responses);

        ResiliencePipelineFactory.Reset();
    }

    // ── Progress callback regression test ──────────────────────────────

    /// <summary>
    /// Proves onProgress fires for EVERY operation, including HTTP errors.
    /// Before the fix, the return on the HTTP error path (non-2xx) skipped
    /// the onProgress callback, so the count would never reach Total.
    /// </summary>
    [Fact]
    public async Task BulkWriteAsync_ProgressFires_ForSuccessAndHttpErrors()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();

        // 3 ops: success, HTTP error, success
        handler.QueueResponse(HttpStatusCode.Created, TestData.SingleUser);
        handler.QueueResponse(HttpStatusCode.NotFound, """{"error":{"code":"NotFound","message":"not found"}}""");
        handler.QueueResponse(HttpStatusCode.Created, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, NoRateLimitOptions);
        var fanOut = new ConcurrentFanOut(client, maxConcurrency: 1);

        var ops = new List<(string id, string url)>
        {
            ("op1", "https://graph.microsoft.com/v1.0/users"),
            ("op2", "https://graph.microsoft.com/v1.0/users/fake"),
            ("op3", "https://graph.microsoft.com/v1.0/users")
        };

        var progressCalls = new List<(int current, int total)>();
        var result = await fanOut.BulkWriteAsync(
            HttpMethod.Post, ops, """{"displayName":"Test"}""",
            onProgress: (current, total) => progressCalls.Add((current, total)));

        Assert.Equal(2, result.Succeeded);
        Assert.Equal(1, result.Failed);

        // onProgress must fire exactly 3 times (once per operation)
        Assert.Equal(3, progressCalls.Count);
        // Final progress call must report all operations complete
        Assert.Equal(ops.Count, progressCalls[^1].current);

        ResiliencePipelineFactory.Reset();
    }

    // ── Double-count regression test ───────────────────────────────────

    /// <summary>
    /// Proves that Succeeded + Failed never exceeds Total.
    /// Before the fix, Interlocked.Increment(ref succeeded) ran BEFORE
    /// deserialization. If deserialization threw, succeeded was incremented
    /// AND the error catch added to errorBag, giving Succeeded + Failed > Total.
    /// After the fix, the increment runs AFTER deserialization.
    /// </summary>
    [Fact]
    public async Task BulkWriteAsync_InvalidJsonBody_NoDoubleCount()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();

        // Return 201 Created with a body that is NOT valid JSON.
        // Deserialization will throw JsonException, which the outer catch
        // should capture as an error. Succeeded must NOT be incremented.
        handler.QueueResponse(HttpStatusCode.Created, "THIS IS NOT JSON");
        handler.QueueResponse(HttpStatusCode.Created, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, NoRateLimitOptions);
        var fanOut = new ConcurrentFanOut(client, maxConcurrency: 1);

        var ops = new List<(string id, string url)>
        {
            ("bad-json", "https://graph.microsoft.com/v1.0/users"),
            ("good", "https://graph.microsoft.com/v1.0/users")
        };

        var result = await fanOut.BulkWriteAsync(HttpMethod.Post, ops, """{"displayName":"Test"}""");

        // The invariant: Succeeded + Failed must equal Total
        Assert.Equal(ops.Count, result.Succeeded + result.Failed);
        Assert.Equal(1, result.Succeeded);
        Assert.Equal(1, result.Failed);
        Assert.Single(result.Errors);
        Assert.Equal("bad-json", result.Errors[0].Id);
        // StatusCode 0 proves error came from deserialization-failure catch path,
        // not from the HTTP-error path (which would carry the real status code)
        Assert.Equal(0, result.Errors[0].StatusCode);

        ResiliencePipelineFactory.Reset();
    }

    // ── Error data correctness for warning messages ─────────────────────

    /// <summary>
    /// Proves that BulkWriteResult.Errors carry the correct HTTP status codes,
    /// allowing HandleBulkWriteErrors to report only status codes that were
    /// actually encountered (not just which -Skip flags are set).
    /// </summary>
    [Fact]
    public async Task BulkWriteAsync_MixedErrors_PreservesStatusCodes()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();

        // 4 ops: 201 (success), 404 (not found), 404 (not found), 403 (forbidden)
        handler.QueueResponse(HttpStatusCode.Created, TestData.SingleUser);
        handler.QueueResponse(HttpStatusCode.NotFound, """{"error":{"code":"Request_ResourceNotFound","message":"not found"}}""");
        handler.QueueResponse(HttpStatusCode.NotFound, """{"error":{"code":"Request_ResourceNotFound","message":"not found"}}""");
        handler.QueueResponse(HttpStatusCode.Forbidden, """{"error":{"code":"Authorization_RequestDenied","message":"forbidden"}}""");

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, NoRateLimitOptions);
        // maxConcurrency: 1 to guarantee deterministic ordering
        var fanOut = new ConcurrentFanOut(client, maxConcurrency: 1);

        var ops = new List<(string id, string url)>
        {
            ("op1", "https://graph.microsoft.com/v1.0/users"),
            ("op2", "https://graph.microsoft.com/v1.0/users/fake1"),
            ("op3", "https://graph.microsoft.com/v1.0/users/fake2"),
            ("op4", "https://graph.microsoft.com/v1.0/groups/restricted")
        };

        var result = await fanOut.BulkWriteAsync(HttpMethod.Post, ops, """{"displayName":"Test"}""");

        Assert.Equal(1, result.Succeeded);
        Assert.Equal(3, result.Failed);

        // Verify status codes are preserved correctly so warning logic can count them
        var statusCodes = result.Errors.Select(e => e.StatusCode).OrderBy(s => s).ToArray();
        Assert.Equal(new[] { 403, 404, 404 }, statusCodes);

        ResiliencePipelineFactory.Reset();
    }

    // ── Per-item exception swallowing verification ─────────────────────

    /// <summary>
    /// Proves that ForEachAsync SWALLOWS HttpRequestException per-item
    /// (stores in errors dict) and does NOT let it propagate to the caller.
    /// </summary>
    [Fact]
    public async Task ForEachAsync_HttpRequestException_SwallowedPerItem()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new ExceptionThrowingHandler(new HttpRequestException("Connection refused"));

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, NoRateLimitOptions);
        var fanOut = new ConcurrentFanOut(client, maxConcurrency: 2);

        var items = new[] { "item1", "item2" };

        // ForEachAsync should NOT throw; it should capture the error per-item
        var errors = await fanOut.ForEachAsync(items, async (item, ct) =>
        {
            // This will throw HttpRequestException from the mock handler
            await client.GetAsync("https://graph.microsoft.com/v1.0/users", ct);
        });

        // Errors should be captured, not thrown
        Assert.Equal(2, errors.Count);
        Assert.True(errors.ContainsKey("item1"));
        Assert.True(errors.ContainsKey("item2"));

        ResiliencePipelineFactory.Reset();
    }

    /// <summary>
    /// Proves that BulkWriteAsync SWALLOWS HttpRequestException per-item
    /// and does NOT let it propagate to the caller.
    /// </summary>
    [Fact]
    public async Task BulkWriteAsync_HttpRequestException_SwallowedPerItem()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new ExceptionThrowingHandler(new HttpRequestException("Connection refused"));

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, NoRateLimitOptions);
        var fanOut = new ConcurrentFanOut(client, maxConcurrency: 2);

        var ops = new List<(string id, string url)>
        {
            ("op1", "https://graph.microsoft.com/v1.0/groups"),
            ("op2", "https://graph.microsoft.com/v1.0/groups")
        };

        // BulkWriteAsync should NOT throw; it should capture errors in the result
        var result = await fanOut.BulkWriteAsync(HttpMethod.Post, ops, """{"displayName":"Test"}""");

        Assert.Equal(0, result.Succeeded);
        Assert.Equal(2, result.Failed);
        Assert.Equal(2, result.Errors.Count);
        // Status code 0 because HttpRequestException is not a Graph HTTP error
        Assert.All(result.Errors, e => Assert.Equal(0, e.StatusCode));

        ResiliencePipelineFactory.Reset();
    }

    /// <summary>
    /// Proves that BulkWriteAsync SWALLOWS BrokenCircuitException per-item,
    /// just like HttpRequestException. Both go through the same outer catch
    /// block at ConcurrentFanOut.BulkWriteAsync.
    /// </summary>
    [Fact]
    public async Task BulkWriteAsync_BrokenCircuitException_SwallowedPerItem()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new ExceptionThrowingHandler(
            new BrokenCircuitException("Circuit is open"));

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, NoRateLimitOptions);
        var fanOut = new ConcurrentFanOut(client, maxConcurrency: 2);

        var ops = new List<(string id, string url)>
        {
            ("op1", "https://graph.microsoft.com/v1.0/groups"),
            ("op2", "https://graph.microsoft.com/v1.0/groups")
        };

        // BulkWriteAsync should NOT throw; it should capture errors in the result
        var result = await fanOut.BulkWriteAsync(HttpMethod.Post, ops, """{"displayName":"Test"}""");

        Assert.Equal(0, result.Succeeded);
        Assert.Equal(2, result.Failed);
        Assert.Equal(2, result.Errors.Count);
        Assert.All(result.Errors, e => Assert.Equal(0, e.StatusCode));

        ResiliencePipelineFactory.Reset();
    }
}

/// <summary>
/// Mock handler that throws a fresh exception instance on every request.
/// Creates new instances per call to avoid stack trace corruption from
/// throwing the same exception object across concurrent threads.
/// </summary>
public class ExceptionThrowingHandler : HttpMessageHandler
{
    private readonly Func<Exception> _exceptionFactory;

    public ExceptionThrowingHandler(Exception template)
        => _exceptionFactory = () => (Exception)Activator.CreateInstance(
            template.GetType(), template.Message)!;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        throw _exceptionFactory();
    }
}
