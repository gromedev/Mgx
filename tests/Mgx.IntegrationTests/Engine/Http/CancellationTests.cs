using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Mgx.Engine.Http;
using Mgx.Engine.Models;

namespace Mgx.IntegrationTests;

[Collection("Pipeline")]
public class CancellationTests
{
    // ── R2-6: CancellationToken cancels during batch retry delay ──────────────

    [Fact]
    public async Task Batch_CancellationDuringRetryDelay_ThrowsWithinTimeLimit()
    {
        // Setup: all items return 429 with Retry-After: 10 (10 seconds).
        // Cancel the token after 100ms. The batch client should throw
        // OperationCanceledException during the retry delay, well before 10s.
        var all429 = """
        {
            "responses": [
                { "id": "1", "status": 429, "headers": { "Retry-After": "10" }, "body": { "error": { "code": "TooManyRequests" } } }
            ]
        }
        """;

        var handler = new MockHttpHandler();
        // Queue enough 429 responses to cover multiple retry attempts (if cancellation fails)
        for (int i = 0; i < 10; i++)
            handler.QueueResponse(HttpStatusCode.OK, all429);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var batchClient = new GraphBatchClient(client);

        var operations = new List<BatchOperation> { new("/users/user1", "GET") };

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var sw = Stopwatch.StartNew();

        // Should throw OperationCanceledException or TaskCanceledException
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => batchClient.ExecuteBatchIndexedAsync(operations, cts.Token));

        sw.Stop();

        // Must complete well under the 10s Retry-After delay.
        // Allow generous margin (2s) for test environment variability.
        Assert.True(sw.Elapsed.TotalSeconds < 2.0,
            $"Expected cancellation within 2s, but took {sw.Elapsed.TotalSeconds:F1}s. " +
            $"The batch client did not honor the CancellationToken during retry delay.");
    }
}
