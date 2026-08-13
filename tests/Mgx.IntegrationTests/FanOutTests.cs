using System.Net;
using Mgx.Engine.Http;
using Mgx.Engine.Pagination;

namespace Mgx.IntegrationTests;

[Collection("Pipeline")]
public class FanOutTests
{
    private static readonly ResilientGraphClientOptions NoRateLimitOptions = new()
    {
        NoRateLimit = true,
        MaxRetryAttempts = 1,
        TotalTimeoutSeconds = 30,
        AttemptTimeoutSeconds = 10
    };

    // ── Partial Failure ────────────────────────────────────────────────

    [Fact]
    public async Task FetchAllAsync_PartialFailure_PreservesSuccessfulResults()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();

        // URL1 succeeds with users, URL2 returns 404
        var url1 = "https://graph.microsoft.com/v1.0/groups/g1/members";
        var url2 = "https://graph.microsoft.com/v1.0/groups/g2/members";

        handler.QueueResponse(HttpStatusCode.OK, TestData.UsersPage2);  // url1: success
        handler.QueueResponse(HttpStatusCode.NotFound);                  // url2: 404

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, NoRateLimitOptions);
        var fanOut = new ConcurrentFanOut(client, maxConcurrency: 2);

        var result = await fanOut.FetchAllAsync(new[] { url1, url2 });

        Assert.True(result.HasErrors, "Should report errors for URL2");
        Assert.True(result.Results.ContainsKey(url1), "URL1 results should be preserved");
        Assert.Single(result.Results[url1]); // "User Three" from UsersPage2
        Assert.True(result.Errors.ContainsKey(url2), "URL2 should be in errors");

        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public async Task FetchAllAsync_AllSucceed_NoErrors()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();

        var url1 = "https://graph.microsoft.com/v1.0/groups/g1/members";
        var url2 = "https://graph.microsoft.com/v1.0/groups/g2/members";

        handler.QueueResponse(HttpStatusCode.OK, TestData.UsersPage2);
        handler.QueueResponse(HttpStatusCode.OK, TestData.UsersPage2);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, NoRateLimitOptions);
        var fanOut = new ConcurrentFanOut(client, maxConcurrency: 2);

        var result = await fanOut.FetchAllAsync(new[] { url1, url2 });

        Assert.False(result.HasErrors);
        Assert.Equal(2, result.Results.Count);

        ResiliencePipelineFactory.Reset();
    }

    // ── Cancellation ───────────────────────────────────────────────────

    [Fact]
    public async Task FetchAllAsync_CancellationPropagates()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new SlowMockHttpHandler(delayMs: 5000);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, NoRateLimitOptions);
        var fanOut = new ConcurrentFanOut(client, maxConcurrency: 2);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(100); // Cancel after 100ms

        var urls = new[] { "https://graph.microsoft.com/v1.0/users" };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fanOut.FetchAllAsync(urls, cancellationToken: cts.Token));

        ResiliencePipelineFactory.Reset();
    }

    // ── Semaphore Release ──────────────────────────────────────────────

    [Fact]
    public async Task FetchAllAsync_SemaphoreReleased_OnFailure()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();

        // First URL fails, second should still run (semaphore not leaked)
        handler.QueueResponse(HttpStatusCode.InternalServerError);  // URL1 fails
        handler.QueueResponse(HttpStatusCode.InternalServerError);  // URL1 retry fails
        handler.QueueResponse(HttpStatusCode.OK, TestData.UsersPage2);  // URL2 succeeds

        var url1 = "https://graph.microsoft.com/v1.0/groups/g1/members";
        var url2 = "https://graph.microsoft.com/v1.0/groups/g2/members";

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, NoRateLimitOptions);
        var fanOut = new ConcurrentFanOut(client, maxConcurrency: 1); // Only 1 slot

        var result = await fanOut.FetchAllAsync(new[] { url1, url2 });

        // Both URLs should have been attempted (semaphore released after URL1 failed)
        Assert.True(result.Results.ContainsKey(url2), "URL2 should succeed despite URL1 failure");

        ResiliencePipelineFactory.Reset();
    }

    // ── ForEachAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task ForEachAsync_CollectsPerItemErrors()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, NoRateLimitOptions);
        var fanOut = new ConcurrentFanOut(client, maxConcurrency: 2);

        var items = new[] { "item1", "item2", "item3" };
        int callCount = 0;

        var errors = await fanOut.ForEachAsync(items, async (item, ct) =>
        {
            Interlocked.Increment(ref callCount);
            if (item == "item2")
                throw new InvalidOperationException("Simulated failure");
            await Task.CompletedTask;
        });

        Assert.Equal(3, callCount); // All items attempted
        Assert.Single(errors);     // Only item2 failed
        Assert.True(errors.ContainsKey("item2"));
        Assert.IsType<InvalidOperationException>(errors["item2"]);
    }

    // ── BulkWriteAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task BulkWriteAsync_PartialFailure_ReportsSuccessAndFailure()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();

        // Op1: 201 Created, Op2: 403 Forbidden, Op3: 204 No Content
        handler.QueueResponse(HttpStatusCode.Created, TestData.SingleUser);
        handler.QueueResponse(HttpStatusCode.Forbidden, """{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges"}}""");
        handler.QueueResponse(HttpStatusCode.NoContent);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, NoRateLimitOptions);
        var fanOut = new ConcurrentFanOut(client, maxConcurrency: 1);

        var ops = new List<(string id, string url)>
        {
            ("op1", "https://graph.microsoft.com/v1.0/groups"),
            ("op2", "https://graph.microsoft.com/v1.0/groups"),
            ("op3", "https://graph.microsoft.com/v1.0/groups/g1")
        };

        var result = await fanOut.BulkWriteAsync(HttpMethod.Post, ops, """{"displayName":"Test"}""");

        Assert.Equal(2, result.Succeeded); // op1 + op3
        Assert.Equal(1, result.Failed);    // op2
        Assert.Single(result.Errors);
        Assert.Equal("op2", result.Errors[0].Id);
        Assert.Equal(403, result.Errors[0].StatusCode);

        ResiliencePipelineFactory.Reset();
    }
}

/// <summary>
/// Mock handler that adds artificial delay to simulate slow responses.
/// Used for testing cancellation behavior.
/// </summary>
public class SlowMockHttpHandler : HttpMessageHandler
{
    private readonly int _delayMs;

    public SlowMockHttpHandler(int delayMs) => _delayMs = delayMs;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await Task.Delay(_delayMs, cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(TestData.SingleUser, System.Text.Encoding.UTF8, "application/json")
        };
    }
}
