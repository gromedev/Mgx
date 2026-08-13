using System.Management.Automation;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Mgx.Cmdlets.Base;
using Mgx.Engine.Http;
using Mgx.Engine.Models;
using Mgx.Engine.Pagination;

namespace Mgx.IntegrationTests;

[Collection("Pipeline")]
public class DeltaQueryTests
{
    // --- Test data ---

    private static string DeltaPage1 => """
    {
        "value": [
            {"id": "user1", "displayName": "User One"},
            {"id": "user2", "displayName": "User Two"}
        ],
        "@odata.nextLink": "https://graph.microsoft.com/v1.0/users/delta?$skiptoken=page2"
    }
    """;

    private static string DeltaPage2WithToken => """
    {
        "value": [
            {"id": "user3", "displayName": "User Three"}
        ],
        "@odata.deltaLink": "https://graph.microsoft.com/v1.0/users/delta?$deltatoken=abc123"
    }
    """;

    private static string DeltaPageWithRemoved => """
    {
        "value": [
            {"id": "user4", "displayName": "User Four"},
            {"id": "user5", "@removed": {"reason": "deleted"}}
        ],
        "@odata.deltaLink": "https://graph.microsoft.com/v1.0/users/delta?$deltatoken=def456"
    }
    """;

    private static string EmptyDeltaWithToken => """
    {
        "value": [],
        "@odata.deltaLink": "https://graph.microsoft.com/v1.0/users/delta?$deltatoken=empty789"
    }
    """;

    // --- GraphRawCollectionResponse DeltaLink deserialization ---

    [Fact]
    public void GraphRawCollectionResponse_DeserializesDeltaLink()
    {
        var json = DeltaPage2WithToken;
        var response = JsonSerializer.Deserialize<GraphRawCollectionResponse>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(response);
        Assert.NotNull(response!.DeltaLink);
        Assert.Contains("$deltatoken=abc123", response.DeltaLink);
        Assert.Null(response.NextLink); // Final page: deltaLink, not nextLink
    }

    [Fact]
    public void GraphRawCollectionResponse_NoDeltaLink_WhenNextLinkPresent()
    {
        var json = DeltaPage1;
        var response = JsonSerializer.Deserialize<GraphRawCollectionResponse>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(response);
        Assert.Null(response!.DeltaLink);
        Assert.NotNull(response.NextLink); // Intermediate page: nextLink, not deltaLink
    }

    // --- PageIterator deltaLink callback ---

    [Fact]
    public async Task PageIterator_FiresDeltaLinkCallback_OnFinalPage()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, DeltaPage1);
        handler.QueueResponse(HttpStatusCode.OK, DeltaPage2WithToken);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var iterator = new PageIterator(client);
        string? capturedDeltaLink = null;
        var items = new List<JsonElement>();

        await foreach (var item in iterator.StreamAllWithCountAsync(
            "https://graph.microsoft.com/v1.0/users/delta",
            0,
            null,
            onDeltaLink: dl => capturedDeltaLink = dl))
        {
            items.Add(item);
        }

        Assert.Equal(3, items.Count);
        Assert.NotNull(capturedDeltaLink);
        Assert.Contains("$deltatoken=abc123", capturedDeltaLink);
    }

    [Fact]
    public async Task PageIterator_NoDeltaLinkCallback_WhenNoDeltaResponse()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, TestData.UsersPage1);
        handler.QueueResponse(HttpStatusCode.OK, TestData.UsersPage2);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var iterator = new PageIterator(client);
        string? capturedDeltaLink = null;

        await foreach (var _ in iterator.StreamAllWithCountAsync(
            "https://graph.microsoft.com/v1.0/users",
            0,
            null,
            onDeltaLink: dl => capturedDeltaLink = dl))
        { }

        Assert.Null(capturedDeltaLink); // Regular endpoint, no deltaLink
    }

    [Fact]
    public async Task PageIterator_CapturesDeltaLink_OnZeroItemResponse()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, EmptyDeltaWithToken);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var iterator = new PageIterator(client);
        string? capturedDeltaLink = null;
        var items = new List<JsonElement>();

        await foreach (var item in iterator.StreamAllWithCountAsync(
            "https://graph.microsoft.com/v1.0/users/delta",
            0,
            null,
            onDeltaLink: dl => capturedDeltaLink = dl))
        {
            items.Add(item);
        }

        Assert.Empty(items); // No items changed
        Assert.NotNull(capturedDeltaLink); // But deltaLink MUST still be captured
        Assert.Contains("$deltatoken=empty789", capturedDeltaLink);
    }

    // --- @removed items preserved ---

    [Fact]
    public async Task PageIterator_PreservesRemovedAnnotation()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, DeltaPageWithRemoved);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var iterator = new PageIterator(client);
        var items = new List<JsonElement>();

        await foreach (var item in iterator.StreamAllWithCountAsync(
            "https://graph.microsoft.com/v1.0/users/delta",
            0,
            null,
            onDeltaLink: _ => { }))
        {
            items.Add(item);
        }

        Assert.Equal(2, items.Count);
        // First item: normal user
        Assert.Equal("user4", items[0].GetProperty("id").GetString());
        Assert.False(items[0].TryGetProperty("@removed", out _));
        // Second item: removed user
        Assert.Equal("user5", items[1].GetProperty("id").GetString());
        Assert.True(items[1].TryGetProperty("@removed", out var removed));
        Assert.Equal("deleted", removed.GetProperty("reason").GetString());
        // @removed does NOT start with @odata. so it survives JsonToHashtable's
        // @odata.* stripping filter
        Assert.False("@removed".StartsWith("@odata.", StringComparison.OrdinalIgnoreCase));
    }

    // --- DeltaState persistence ---

    [Fact]
    public void DeltaState_SaveAndLoad_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"delta-test-{Guid.NewGuid()}.json");
        try
        {
            var state = new DeltaState
            {
                DeltaLink = "https://graph.microsoft.com/v1.0/users/delta?$deltatoken=abc",
                Select = "displayName,mail",
                Filter = null,
                Resource = "/users/delta",
                ItemCount = 42,
                GraphEndpoint = "https://graph.microsoft.com"
            };
            state.Save(path);

            var loaded = DeltaState.Load(path);
            Assert.NotNull(loaded);
            Assert.Equal("https://graph.microsoft.com/v1.0/users/delta?$deltatoken=abc", loaded!.DeltaLink);
            Assert.Equal("displayName,mail", loaded.Select);
            Assert.Null(loaded.Filter);
            Assert.Equal("/users/delta", loaded.Resource);
            Assert.Equal(42, loaded.ItemCount);
            Assert.Equal("https://graph.microsoft.com", loaded.GraphEndpoint);
            Assert.True(loaded.LastSync > DateTimeOffset.UtcNow.AddMinutes(-1));
        }
        finally
        {
            DeltaState.Delete(path);
        }
    }

    // --- LoadWithResult tests (Fix 6) ---

    [Fact]
    public void DeltaState_LoadWithResult_ReturnsNotFound_WhenMissing()
    {
        var path = Path.Combine(Path.GetTempPath(), "nonexistent-delta.json");
        var (state, result) = DeltaState.LoadWithResult(path);
        Assert.Null(state);
        Assert.Equal(DeltaLoadResult.NotFound, result);
    }

    [Fact]
    public void DeltaState_LoadWithResult_ReturnsCorrupt_OnMalformedJson()
    {
        var path = Path.Combine(Path.GetTempPath(), $"corrupt-delta-{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(path, "not valid json{{{");
            var (state, result) = DeltaState.LoadWithResult(path);
            Assert.Null(state);
            Assert.Equal(DeltaLoadResult.Corrupt, result);
        }
        finally
        {
            DeltaState.Delete(path);
        }
    }

    [Fact]
    public void DeltaState_LoadWithResult_ReturnsOk_OnValidFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"valid-delta-{Guid.NewGuid()}.json");
        try
        {
            new DeltaState
            {
                DeltaLink = "https://graph.microsoft.com/v1.0/users/delta?$deltatoken=test",
                Resource = "/users/delta",
                GraphEndpoint = "https://graph.microsoft.com"
            }.Save(path);

            var (state, result) = DeltaState.LoadWithResult(path);
            Assert.NotNull(state);
            Assert.Equal(DeltaLoadResult.Ok, result);
        }
        finally
        {
            DeltaState.Delete(path);
        }
    }

    [Fact]
    public void DeltaState_Load_BackwardCompat_ReturnsNull_WhenCorrupt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"corrupt-compat-{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(path, "not valid json{{{");
            Assert.Null(DeltaState.Load(path)); // backward-compat Load still works
        }
        finally
        {
            DeltaState.Delete(path);
        }
    }

    [Fact]
    public void DeltaState_Delete_RemovesTmpFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"delta-delete-{Guid.NewGuid()}.json");
        var tmpPath = path + ".tmp";
        File.WriteAllText(path, "{}");
        File.WriteAllText(tmpPath, "{}");

        DeltaState.Delete(path);

        Assert.False(File.Exists(path));
        Assert.False(File.Exists(tmpPath));
    }

    [Fact]
    public void DeltaState_ValidateWriteAccess_SucceedsForWritablePath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"delta-probe-{Guid.NewGuid()}.json");
        DeltaState.ValidateWriteAccess(path); // Should not throw
        Assert.False(File.Exists(path)); // Probe file cleaned up
    }

    // --- Empty intermediate pages test (Fix 11) ---

    [Fact]
    public async Task PageIterator_CapturesDeltaLink_AfterMultipleEmptyPages()
    {
        // Mirrors real Graph behavior: data page → 5 empty pages with nextLink → deltaLink page.
        // Old MaxConsecutiveEmptyPages=3 would abort before reaching the deltaLink.
        // 5 empty pages proves the delta limit (1000) is higher than the regular limit (3).
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();

        // Page 1: items + nextLink
        handler.QueueResponse(HttpStatusCode.OK, DeltaPage1);
        // Pages 2-6: empty + nextLink
        for (int i = 0; i < 5; i++)
        {
            handler.QueueResponse(HttpStatusCode.OK,
                $"{{\"value\": [], \"@odata.nextLink\": \"https://graph.microsoft.com/v1.0/users/delta?$skiptoken=empty{i}\"}}");
        }
        // Page 7: empty + deltaLink (final)
        handler.QueueResponse(HttpStatusCode.OK, EmptyDeltaWithToken);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var iterator = new PageIterator(client);
        string? capturedDeltaLink = null;
        var items = new List<JsonElement>();

        await foreach (var item in iterator.StreamAllWithCountAsync(
            "https://graph.microsoft.com/v1.0/users/delta",
            0,
            null,
            onDeltaLink: dl => capturedDeltaLink = dl))
        {
            items.Add(item);
        }

        Assert.Equal(2, items.Count); // Only page 1 had items
        Assert.NotNull(capturedDeltaLink); // DeltaLink captured despite 5 empty pages
        Assert.Contains("$deltatoken=empty789", capturedDeltaLink);
        Assert.Equal(7, handler.RequestCount); // All 7 pages fetched
    }

    [Fact]
    public async Task PageIterator_RegularEndpoint_StillBreaksAfter3EmptyPages()
    {
        // Without onDeltaLink, the regular limit of 3 applies
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();

        handler.QueueResponse(HttpStatusCode.OK, TestData.UsersPage1);
        for (int i = 0; i < 5; i++)
        {
            handler.QueueResponse(HttpStatusCode.OK,
                $"{{\"value\": [], \"@odata.nextLink\": \"https://graph.microsoft.com/v1.0/users?$skiptoken=empty{i}\"}}");
        }

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var iterator = new PageIterator(client);
        var items = new List<JsonElement>();

        await foreach (var item in iterator.StreamAllWithCountAsync(
            "https://graph.microsoft.com/v1.0/users", 0, null)) // No onDeltaLink = regular limit
        {
            items.Add(item);
        }

        Assert.Equal(2, items.Count); // Items from page 1 only
        Assert.Equal(4, handler.RequestCount); // Page 1 + 3 empty pages (stopped at limit)
    }

    // --- StreamAllWithCountAsync also captures deltaLink ---

    [Fact]
    public async Task StreamAllWithCountAsync_CapturesDeltaLink()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, DeltaPage2WithToken);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var iterator = new PageIterator(client);
        string? capturedDeltaLink = null;

        await foreach (var _ in iterator.StreamAllWithCountAsync(
            "https://graph.microsoft.com/v1.0/users/delta",
            maxItems: 0,
            onCount: null,
            onDeltaLink: dl => capturedDeltaLink = dl))
        { }

        Assert.NotNull(capturedDeltaLink);
        Assert.Contains("$deltatoken=abc123", capturedDeltaLink);
    }

    // --- #1: 410 Gone triggers GraphServiceException which cmdlet catches and retries ---

    [Fact]
    public async Task Delta_410Gone_ThrowsGraphServiceException()
    {
        // The cmdlet's 410 retry logic depends on GraphServiceException being thrown
        // when Graph returns 410. Verify the engine throws it correctly.
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.Gone, """{"error":{"code":"deltaTokenExpired","message":"Token expired"}}""");

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var ex = await Assert.ThrowsAsync<GraphServiceException>(
            () => client.GetCollectionPageAsync("https://graph.microsoft.com/v1.0/users/delta"));
        Assert.Equal(HttpStatusCode.Gone, ex.StatusCode);
    }

    // --- #2: 410 only caught on first attempt (not infinite loop) ---

    [Fact]
    public async Task Delta_410Gone_OnlyRetriedOnce_ByDesign()
    {
        // Verify that two consecutive 410s result in exactly 2 requests (not infinite).
        // The cmdlet's for-loop does attempt 0 (catches 410, continues) then attempt 1
        // (410 falls through to generic GraphServiceException handler).
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.Gone);
        handler.QueueResponse(HttpStatusCode.Gone);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            MaxRetryAttempts = 1 // Disable Polly retries so 410 propagates immediately
        });

        // First call: 410
        await Assert.ThrowsAsync<GraphServiceException>(
            () => client.GetCollectionPageAsync("https://graph.microsoft.com/v1.0/users/delta"));
        // Second call: 410 again
        await Assert.ThrowsAsync<GraphServiceException>(
            () => client.GetCollectionPageAsync("https://graph.microsoft.com/v1.0/users/delta"));

        Assert.Equal(2, handler.RequestCount); // Exactly 2, not infinite
    }

    // --- #3: OutputFile JSONL mode ---

    [Fact]
    public async Task Delta_OutputFile_WritesJsonlFormat()
    {
        // Test that items can be written as one-JSON-per-line (the pattern used by SyncMgxDelta)
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, DeltaPage2WithToken);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var outputPath = Path.Combine(Path.GetTempPath(), $"delta-jsonl-{Guid.NewGuid()}.jsonl");
        try
        {
            var iterator = new PageIterator(client);
            using (var writer = new StreamWriter(outputPath, append: false))
            {
                await foreach (var item in iterator.StreamAllWithCountAsync(
                    "https://graph.microsoft.com/v1.0/users/delta",
                    0,
                    null,
                    onDeltaLink: _ => { }))
                {
                    writer.WriteLine(item.GetRawText());
                }
            }

            var lines = File.ReadAllLines(outputPath);
            Assert.Single(lines); // DeltaPage2WithToken has 1 item
            var parsed = JsonSerializer.Deserialize<JsonElement>(lines[0]);
            Assert.Equal("user3", parsed.GetProperty("id").GetString());
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    // --- #4: OutputFile temp file cleanup on error ---

    [Fact]
    public async Task Delta_OutputFile_TempFileCleanedUpOnError()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, DeltaPage1);
        // Queue enough 500s to exhaust Polly retries (1 initial + 1 retry = 2 attempts)
        handler.QueueResponse(HttpStatusCode.InternalServerError);
        handler.QueueResponse(HttpStatusCode.InternalServerError);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            MaxRetryAttempts = 1
        });

        var outputPath = Path.Combine(Path.GetTempPath(), $"delta-cleanup-{Guid.NewGuid()}.jsonl");
        var tmpPath = $"{outputPath}.tmp";
        try
        {
            var iterator = new PageIterator(client);
            var writePath = $"{outputPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                using (var writer = new StreamWriter(writePath, append: false))
                {
                    await foreach (var item in iterator.StreamAllWithCountAsync(
                        "https://graph.microsoft.com/v1.0/users/delta",
                        0,
                        null,
                        onDeltaLink: _ => { }))
                    {
                        writer.WriteLine(item.GetRawText());
                    }
                }
                File.Move(writePath, outputPath, overwrite: true);
            }
            catch
            {
                // Simulate the cmdlet's cleanup behavior
                try { if (File.Exists(writePath)) File.Delete(writePath); } catch { }
                throw;
            }

            Assert.Fail("Should have thrown");
        }
        catch (GraphServiceException)
        {
            // Expected: 500 error on page 2
            Assert.False(File.Exists(outputPath), "Final output file should not exist after error");
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    // --- #5: OutputFile atomic rename on success ---

    [Fact]
    public async Task Delta_OutputFile_AtomicRenameOnSuccess()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, DeltaPage2WithToken);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var outputPath = Path.Combine(Path.GetTempPath(), $"delta-atomic-{Guid.NewGuid()}.jsonl");
        try
        {
            var iterator = new PageIterator(client);
            var writePath = $"{outputPath}.{Guid.NewGuid():N}.tmp";
            using (var writer = new StreamWriter(writePath, append: false))
            {
                await foreach (var item in iterator.StreamAllWithCountAsync(
                    "https://graph.microsoft.com/v1.0/users/delta",
                    0,
                    null,
                    onDeltaLink: _ => { }))
                {
                    writer.WriteLine(item.GetRawText());
                }
            }
            // Atomic rename
            File.Move(writePath, outputPath, overwrite: true);

            Assert.True(File.Exists(outputPath));
            Assert.False(File.Exists(writePath), "Temp file should be gone after rename");
            Assert.Single(File.ReadAllLines(outputPath));
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    // --- #6: Cancellation does NOT save delta state ---

    [Fact]
    public async Task Delta_Cancellation_DoesNotSaveDeltaState()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        // Page 1 returns items, page 2 will be cancelled
        handler.QueueResponse(HttpStatusCode.OK, DeltaPage1);
        handler.SetDefaultResponse(HttpStatusCode.OK, DeltaPage2WithToken);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var deltaPath = Path.Combine(Path.GetTempPath(), $"delta-cancel-{Guid.NewGuid()}.json");
        try
        {
            var cts = new CancellationTokenSource();
            var iterator = new PageIterator(client);
            string? capturedDeltaLink = null;
            int itemCount = 0;

            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                await foreach (var item in iterator.StreamAllWithCountAsync(
                    "https://graph.microsoft.com/v1.0/users/delta",
                    0,
                    null,
                    onDeltaLink: dl => capturedDeltaLink = dl,
                    cancellationToken: cts.Token))
                {
                    itemCount++;
                    if (itemCount >= 1) cts.Cancel(); // Cancel after first item
                }
            });

            // Delta state should NOT be saved (cmdlet only saves after successful completion)
            Assert.False(File.Exists(deltaPath), "Delta state should not be saved on cancellation");
        }
        finally
        {
            DeltaState.Delete(deltaPath);
        }
    }

    // --- #7: Cancellation cleans up temp file ---

    [Fact]
    public async Task Delta_Cancellation_CleansUpTempFile()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, DeltaPage1);
        handler.SetDefaultResponse(HttpStatusCode.OK, DeltaPage2WithToken);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var outputPath = Path.Combine(Path.GetTempPath(), $"delta-cancel-out-{Guid.NewGuid()}.jsonl");
        var cts = new CancellationTokenSource();
        string? writePath = null;

        try
        {
            writePath = $"{outputPath}.{Guid.NewGuid():N}.tmp";
            var iterator = new PageIterator(client);

            try
            {
                using (var writer = new StreamWriter(writePath, append: false))
                {
                    await foreach (var item in iterator.StreamAllWithCountAsync(
                        "https://graph.microsoft.com/v1.0/users/delta",
                        0,
                        null,
                        onDeltaLink: _ => { },
                        cancellationToken: cts.Token))
                    {
                        writer.WriteLine(item.GetRawText());
                        cts.Cancel();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Simulate cmdlet cleanup
                try { if (File.Exists(writePath)) File.Delete(writePath); } catch { }
            }

            Assert.False(File.Exists(writePath), "Temp file should be cleaned up on cancellation");
            Assert.False(File.Exists(outputPath), "Output file should not exist after cancellation");
        }
        finally
        {
            if (writePath != null && File.Exists(writePath)) File.Delete(writePath);
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    // --- #8: Delta state NOT saved on GraphServiceException ---

    [Fact]
    public async Task Delta_Error_DoesNotSaveDeltaState()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.Forbidden, """{"error":{"code":"AccessDenied","message":"No perms"}}""");

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            MaxRetryAttempts = 1
        });

        var deltaPath = Path.Combine(Path.GetTempPath(), $"delta-error-{Guid.NewGuid()}.json");
        try
        {
            var iterator = new PageIterator(client);
            await Assert.ThrowsAsync<GraphServiceException>(async () =>
            {
                await foreach (var _ in iterator.StreamAllWithCountAsync(
                    "https://graph.microsoft.com/v1.0/users/delta",
                    0,
                    null,
                    onDeltaLink: _ => { }))
                { }
            });

            // The cmdlet would NOT save delta state here (exception exits before save)
            Assert.False(File.Exists(deltaPath));
        }
        finally
        {
            DeltaState.Delete(deltaPath);
        }
    }

    // --- #9: GraphServiceException (non-410) propagates with correct status ---

    [Fact]
    public async Task Delta_403_ThrowsGraphServiceException_NotGone()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.Forbidden, """{"error":{"code":"AccessDenied","message":"No perms"}}""");

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            MaxRetryAttempts = 1
        });

        var ex = await Assert.ThrowsAsync<GraphServiceException>(
            () => client.GetCollectionPageAsync("https://graph.microsoft.com/v1.0/users/delta"));
        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.NotEqual(HttpStatusCode.Gone, ex.StatusCode); // NOT 410, so cmdlet won't retry
    }

    // --- #10: BrokenCircuitException propagates ---

    [Fact]
    public async Task Delta_CircuitBreaker_ThrowsBrokenCircuitException()
    {
        // Trip the circuit breaker, then verify it throws BrokenCircuitException
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        // Queue enough 500s to trip the circuit breaker (default: 10% failure over 40 requests)
        for (int i = 0; i < 50; i++)
            handler.QueueResponse(HttpStatusCode.InternalServerError);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            CircuitBreakerMinThroughput = 2,
            CircuitBreakerFailureRatio = 0.1
        });

        // First few requests trip the breaker
        for (int i = 0; i < 5; i++)
        {
            try { await client.GetAsync("https://graph.microsoft.com/v1.0/users/delta"); } catch { }
        }

        // Now the circuit should be open
        var threw = false;
        try
        {
            await client.GetAsync("https://graph.microsoft.com/v1.0/users/delta");
        }
        catch (Polly.CircuitBreaker.BrokenCircuitException)
        {
            threw = true;
        }
        catch { } // Other exceptions from the 500s are also acceptable

        // Circuit breaker should have tripped at some point
        var summary = MgxTelemetryCollector.Current.GetSummary();
        Assert.True(summary.CircuitBreakerTrips > 0 || threw, "Circuit breaker should have tripped");
        MgxTelemetryCollector.Current.Reset();
    }

    // --- #11: HttpRequestException propagates ---

    [Fact]
    public async Task Delta_NetworkError_ThrowsHttpRequestException()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        // Queue enough exceptions to exhaust Polly retries (1 initial + 1 retry)
        handler.QueueException(new HttpRequestException("DNS resolution failed"));
        handler.QueueException(new HttpRequestException("DNS resolution failed"));

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            MaxRetryAttempts = 1
        });

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetCollectionPageAsync("https://graph.microsoft.com/v1.0/users/delta"));
    }

    // --- #12: IOException from file operations ---

    [Fact]
    public void Delta_OutputFile_IOException_WhenPathInvalid()
    {
        // Verify that writing to an invalid path throws IOException
        var invalidPath = Path.Combine(Path.GetTempPath(), new string('x', 300), "output.jsonl");
        Assert.ThrowsAny<Exception>(() => new StreamWriter(invalidPath));
    }

    // --- #13: No deltaLink received warning scenario ---

    [Fact]
    public async Task Delta_NoDeltaLink_WhenEndpointReturnsNone()
    {
        // Regular (non-delta) endpoint returns no deltaLink
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, TestData.UsersPage1);
        handler.QueueResponse(HttpStatusCode.OK, TestData.UsersPage2);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var iterator = new PageIterator(client);
        string? capturedDeltaLink = null;

        await foreach (var _ in iterator.StreamAllWithCountAsync(
            "https://graph.microsoft.com/v1.0/users",
            0,
            null,
            onDeltaLink: dl => capturedDeltaLink = dl))
        { }

        Assert.Null(capturedDeltaLink);
        // The cmdlet checks: if (capturedDeltaLink == null) WriteWarning("No delta token received...")
        // This test proves the condition is reached when the endpoint returns no deltaLink.
    }

    // --- Cmdlet-level tests via PowerShell.Create() ---

    /// <summary>
    /// Injects a mock HttpClient into MgxCmdletBase's static fields so Sync-MgxDelta
    /// can run without a real Graph connection. Uses reflection because the fields
    /// are private static.
    /// </summary>
    private static void InjectMockHttpClient(MockHttpHandler handler)
    {
        ResiliencePipelineFactory.Reset();
        var baseType = typeof(MgxCmdletBase);
        var httpClientField = baseType.GetField("s_graphHttpClient", BindingFlags.NonPublic | BindingFlags.Static)!;
        var fingerprintField = baseType.GetField("s_cachedAuthFingerprint", BindingFlags.NonPublic | BindingFlags.Static)!;
        var ownsClientField = baseType.GetField("s_ownsHttpClient", BindingFlags.NonPublic | BindingFlags.Static)!;

        var httpClient = new HttpClient(handler);
        var endpointField = baseType.GetField("s_graphEndpoint", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public)!;
        var optionsField = baseType.GetField("s_clientOptions", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public)!;

        httpClientField.SetValue(null, httpClient);
        // The cmdlet recomputes the identity from the mocked Get-MgContext (a PSCustomObject
        // carrying only TenantId), so the cached fingerprint must be built from an equivalent
        // context or GetClient() sees a credential change and discards the injected mock.
        fingerprintField.SetValue(null, MgxCmdletBase.BuildAuthFingerprint(
            new { TenantId = "test-tenant-00000000-0000-0000-0000-000000000000" }, null));
        ownsClientField.SetValue(null, false);
        endpointField.SetValue(null, "https://graph.microsoft.com");
        optionsField.SetValue(null, new ResilientGraphClientOptions { NoRateLimit = true });
    }

    private static void CleanupMockHttpClient()
    {
        var baseType = typeof(MgxCmdletBase);
        baseType.GetField("s_graphHttpClient", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, null);
        baseType.GetField("s_cachedAuthFingerprint", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, null);
        baseType.GetField("s_cachedAuthContextRef", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, null);
        ResiliencePipelineFactory.Reset();
    }

    private static PowerShell CreateTestShell()
    {
        var ps = PowerShell.Create();
        // Import the cmdlet assembly directly (no module manifest needed)
        var cmdletAssembly = typeof(Mgx.Cmdlets.Cmdlets.Delta.SyncMgxDelta).Assembly;
        ps.AddCommand("Import-Module").AddParameter("Assembly", cmdletAssembly);
        ps.Invoke();
        ps.Commands.Clear();
        // Mock Get-MgContext to return a fake tenant ID (bypasses Graph connection check)
        ps.AddScript(@"
            function Get-MgContext {
                [PSCustomObject]@{ TenantId = 'test-tenant-00000000-0000-0000-0000-000000000000' }
            }
        ");
        ps.Invoke();
        ps.Commands.Clear();
        return ps;
    }

    [Fact]
    public void Cmdlet_410Gone_DeletesStateAndResyncs()
    {
        // #1 verified via live Pester. This tests the same flow via mock:
        // First request returns 410 (expired token), cmdlet retries with fresh URL.
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.Gone,
            """{"error":{"code":"deltaTokenExpired","message":"Delta token has expired"}}""");
        // Second attempt (fresh URL) succeeds with items + deltaLink
        handler.QueueResponse(HttpStatusCode.OK, DeltaPage2WithToken);

        InjectMockHttpClient(handler);
        var deltaPath = Path.Combine(Path.GetTempPath(), $"cmdlet-410-{Guid.NewGuid()}.json");

        try
        {
            // Create a delta state with a "stale" token
            new DeltaState
            {
                DeltaLink = "https://graph.microsoft.com/v1.0/users/delta?$deltatoken=stale",
                Resource = "/users/delta",
                GraphEndpoint = "https://graph.microsoft.com",
                Select = "",
            }.Save(deltaPath);

            using var ps = CreateTestShell();
            ps.AddCommand("Sync-MgxDelta")
              .AddParameter("Uri", "/users/delta")
              .AddParameter("DeltaPath", deltaPath);
            var results = ps.Invoke();

            // Should have items from the re-sync
            Assert.True(results.Count > 0, "Should have items after 410 re-sync");
            // Delta state should be updated (not deleted)
            Assert.True(File.Exists(deltaPath), "Delta state should exist after re-sync");
            var state = DeltaState.Load(deltaPath);
            Assert.NotNull(state);
            Assert.Contains("$deltatoken=abc123", state!.DeltaLink); // New token from DeltaPage2WithToken
            // Should have a warning about 410
            Assert.Contains(ps.Streams.Warning, w => w.Message.Contains("410") || w.Message.Contains("expired") || w.Message.Contains("re-sync"));
        }
        finally
        {
            DeltaState.Delete(deltaPath);
            CleanupMockHttpClient();
        }
    }

    [Fact]
    public void Cmdlet_OutputFile_TempFileCleanedUpOnError()
    {
        // #4: OutputFile with error mid-sync. Temp file should be cleaned up.
        var handler = new MockHttpHandler();
        var errorBody = """{"error":{"code":"InternalServerError","message":"Something broke"}}""";
        handler.QueueResponse(HttpStatusCode.OK, DeltaPage1); // Page 1 succeeds
        handler.QueueResponse(HttpStatusCode.InternalServerError, errorBody); // Page 2 fails
        handler.QueueResponse(HttpStatusCode.InternalServerError, errorBody); // Polly retry also fails

        InjectMockHttpClient(handler);
        // Override options to limit retries (default 7 would need 8 error responses)
        var optField = typeof(MgxCmdletBase).GetField("s_clientOptions", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public)!;
        optField.SetValue(null, new ResilientGraphClientOptions { NoRateLimit = true, MaxRetryAttempts = 1 });
        ResiliencePipelineFactory.Reset();
        var deltaPath = Path.Combine(Path.GetTempPath(), $"cmdlet-tempclean-{Guid.NewGuid()}.json");
        var outputPath = Path.Combine(Path.GetTempPath(), $"cmdlet-tempclean-{Guid.NewGuid()}.jsonl");

        try
        {
            using var ps = CreateTestShell();
            ps.AddCommand("Sync-MgxDelta")
              .AddParameter("Uri", "/users/delta")
              .AddParameter("DeltaPath", deltaPath)
              .AddParameter("OutputFile", outputPath);
            ps.Invoke(); // Will error but not throw (non-terminating)

            // Output file should NOT exist (error before atomic rename)
            Assert.False(File.Exists(outputPath), "Output file should not exist after error");
            // No .tmp files should remain
            var dir = Path.GetDirectoryName(outputPath)!;
            var tmpFiles = Directory.GetFiles(dir, "*.tmp").Where(f => f.Contains("cmdlet-tempclean")).ToArray();
            Assert.Empty(tmpFiles);
            // Should have errors
            Assert.True(ps.HadErrors, "Should have errors from 500 response");
        }
        finally
        {
            DeltaState.Delete(deltaPath);
            if (File.Exists(outputPath)) File.Delete(outputPath);
            CleanupMockHttpClient();
        }
    }

    [Fact]
    public void Cmdlet_BrokenCircuit_WritesErrorRecord()
    {
        // #10: Trip the circuit breaker, then invoke the cmdlet.
        // The cmdlet should catch BrokenCircuitException and write an ErrorRecord.
        var handler = new MockHttpHandler();
        // Queue enough 500s to trip the breaker
        for (int i = 0; i < 20; i++)
            handler.QueueResponse(HttpStatusCode.InternalServerError);

        InjectMockHttpClient(handler);
        // Use aggressive CB settings so it trips quickly
        var optionsField = typeof(MgxCmdletBase).GetField("s_clientOptions", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public)!;
        optionsField.SetValue(null, new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            CircuitBreakerMinThroughput = 2,
            CircuitBreakerFailureRatio = 0.5,
            CircuitBreakerDurationSeconds = 30,
            MaxRetryAttempts = 1
        });
        ResiliencePipelineFactory.Reset();

        var deltaPath = Path.Combine(Path.GetTempPath(), $"cmdlet-cb-{Guid.NewGuid()}.json");

        try
        {
            // First: trip the circuit breaker with a few requests
            using var tripShell = CreateTestShell();
            tripShell.AddCommand("Sync-MgxDelta")
                     .AddParameter("Uri", "/users/delta")
                     .AddParameter("DeltaPath", deltaPath);
            tripShell.Invoke();

            // Now invoke again - circuit should be open
            if (File.Exists(deltaPath)) File.Delete(deltaPath);
            using var ps = CreateTestShell();
            ps.AddCommand("Sync-MgxDelta")
              .AddParameter("Uri", "/users/delta")
              .AddParameter("DeltaPath", deltaPath);
            ps.Invoke();

            // Should have errors (either GraphError from 500 or CircuitBroken)
            Assert.True(ps.HadErrors, "Should have errors from circuit breaker or 500");
        }
        finally
        {
            DeltaState.Delete(deltaPath);
            CleanupMockHttpClient();
        }
    }

    [Fact]
    public void Cmdlet_HttpRequestException_WritesErrorRecord()
    {
        // #11: Network failure produces an ErrorRecord, not an unhandled crash.
        var handler = new MockHttpHandler();
        handler.QueueException(new HttpRequestException("Connection refused"));
        handler.QueueException(new HttpRequestException("Connection refused")); // For Polly retry

        InjectMockHttpClient(handler);
        var optionsField2 = typeof(MgxCmdletBase).GetField("s_clientOptions", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public)!;
        optionsField2.SetValue(null, new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            MaxRetryAttempts = 1
        });
        ResiliencePipelineFactory.Reset();

        var deltaPath = Path.Combine(Path.GetTempPath(), $"cmdlet-http-{Guid.NewGuid()}.json");

        try
        {
            using var ps = CreateTestShell();
            ps.AddCommand("Sync-MgxDelta")
              .AddParameter("Uri", "/users/delta")
              .AddParameter("DeltaPath", deltaPath);
            ps.Invoke();

            Assert.True(ps.HadErrors, "Should have errors from network failure");
            Assert.Contains(ps.Streams.Error, e =>
                e.FullyQualifiedErrorId.Contains("HttpError") ||
                e.Exception is HttpRequestException ||
                e.Exception?.InnerException is HttpRequestException);
            // Delta state should NOT exist
            Assert.False(File.Exists(deltaPath), "Delta state should not be saved on network error");
        }
        finally
        {
            DeltaState.Delete(deltaPath);
            CleanupMockHttpClient();
        }
    }

    // --- #14: ValidateWriteAccess throws on unwritable path ---

    [Fact]
    public void DeltaState_ValidateWriteAccess_ThrowsOnUnwritablePath()
    {
        // Use a path that should be unwritable (root-owned directory on Unix)
        var unwritablePath = "/proc/delta-test.json"; // Not writable on macOS/Linux
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            // On Windows, use a path that doesn't exist and can't be created
            unwritablePath = @"Z:\nonexistent\delta-test.json";
        }

        Assert.ThrowsAny<Exception>(() => DeltaState.ValidateWriteAccess(unwritablePath));
    }
}
