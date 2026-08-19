using System.Net;
using System.Text.Json;
using Mgx.Engine.Http;
using Mgx.Engine.Pagination;

namespace Mgx.IntegrationTests;

[Collection("Pipeline")]
public class CheckpointTests : IDisposable
{
    private readonly string _tempDir;

    public CheckpointTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"mgx-checkpoint-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string TempFile(string name = "checkpoint.json") => Path.Combine(_tempDir, name);

    // ── Save / Load ────────────────────────────────────────────────────

    [Fact]
    public void SaveAndLoad_RoundTrip()
    {
        var path = TempFile();
        var cp = new PaginationCheckpoint
        {
            Resource = "https://graph.microsoft.com/v1.0/users",
            NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=abc",
            ItemsCollected = 42
        };
        cp.Save(path);

        var loaded = PaginationCheckpoint.Load(path);

        Assert.NotNull(loaded);
        Assert.Equal(cp.Resource, loaded.Resource);
        Assert.Equal(cp.NextLink, loaded.NextLink);
        Assert.Equal(cp.ItemsCollected, loaded.ItemsCollected);
    }

    [Fact]
    public void SaveAndLoad_PageItemsAlreadyWritten_RoundTrips()
    {
        var path = TempFile();
        var cp = new PaginationCheckpoint
        {
            Resource = "https://graph.microsoft.com/v1.0/users",
            NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=abc",
            ItemsCollected = 150,
            PageItemsAlreadyWritten = 50
        };
        cp.Save(path);

        var loaded = PaginationCheckpoint.Load(path);

        Assert.NotNull(loaded);
        Assert.Equal(150, loaded.ItemsCollected);
        Assert.Equal(50, loaded.PageItemsAlreadyWritten);
    }

    [Fact]
    public void Load_OldFormatCheckpoint_DefaultsToZero()
    {
        // Simulate old checkpoint format without pageItemsAlreadyWritten field
        var path = TempFile();
        File.WriteAllText(path, """
        {
            "resource": "https://graph.microsoft.com/v1.0/users",
            "nextLink": "https://graph.microsoft.com/v1.0/users?$skiptoken=abc",
            "itemsCollected": 100,
            "timestamp": "2026-03-17T00:00:00+00:00"
        }
        """);

        var loaded = PaginationCheckpoint.Load(path);

        Assert.NotNull(loaded);
        Assert.Equal(100, loaded.ItemsCollected);
        Assert.Equal(0, loaded.PageItemsAlreadyWritten); // defaults to 0
    }

    [Fact]
    public void Load_ReturnsNull_ForCorruptFile()
    {
        var path = TempFile();
        File.WriteAllText(path, "this is not json {{{");

        var result = PaginationCheckpoint.Load(path);

        Assert.Null(result);
    }

    [Fact]
    public void Load_ReturnsNull_ForMissingFile()
    {
        var result = PaginationCheckpoint.Load(TempFile("nonexistent.json"));
        Assert.Null(result);
    }

    [Fact]
    public void Save_AtomicWrite_NoTmpFileRemains()
    {
        var path = TempFile();
        new PaginationCheckpoint { Resource = "test", ItemsCollected = 1 }.Save(path);

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"), ".tmp file should not remain after successful save");
    }

    [Fact]
    public void Save_ProducesValidJson()
    {
        var path = TempFile();
        new PaginationCheckpoint
        {
            Resource = "https://graph.microsoft.com/v1.0/users",
            NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=xyz",
            ItemsCollected = 100,
            PageItemsAlreadyWritten = 25
        }.Save(path);

        var json = File.ReadAllText(path);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("https://graph.microsoft.com/v1.0/users", doc.RootElement.GetProperty("resource").GetString());
        Assert.Equal(100, doc.RootElement.GetProperty("itemsCollected").GetInt32());
        Assert.Equal(25, doc.RootElement.GetProperty("pageItemsAlreadyWritten").GetInt32());
    }

    [Fact]
    public void Delete_RemovesBothFiles()
    {
        var path = TempFile();
        File.WriteAllText(path, "checkpoint");
        File.WriteAllText(path + ".tmp", "temp");

        PaginationCheckpoint.Delete(path);

        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Delete_DoesNotThrow_ForMissingFile()
    {
        PaginationCheckpoint.Delete(TempFile("nonexistent.json"));
        // Should not throw
    }

    [Fact]
    public async Task ConcurrentSave_SamePath_NoCorruption()
    {
        // RD-H7: Two threads saving to the same checkpoint path should not crash
        var path = TempFile();
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var tasks = Enumerable.Range(0, 10).Select(i => Task.Run(() =>
        {
            try
            {
                for (int j = 0; j < 50; j++)
                {
                    new PaginationCheckpoint
                    {
                        Resource = "https://graph.microsoft.com/v1.0/users",
                        NextLink = $"https://graph.microsoft.com/v1.0/users?$skiptoken=token{i}_{j}",
                        ItemsCollected = i * 100 + j,
                        PageItemsAlreadyWritten = j
                    }.Save(path);
                }
            }
            catch (Exception ex) { exceptions.Add(ex); }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.Empty(exceptions);

        // Final checkpoint should be valid JSON
        var loaded = PaginationCheckpoint.Load(path);
        Assert.NotNull(loaded);
        Assert.Equal("https://graph.microsoft.com/v1.0/users", loaded.Resource);
    }

    // ── Resume pagination ──────────────────────────────────────────────

    [Fact]
    public async Task PageIterator_ResumesFromCheckpoint()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();

        // Page 2 is what the checkpoint points to
        handler.SetDefaultResponse(HttpStatusCode.OK, TestData.UsersPage2);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var iterator = new PageIterator(client);

        // Consumer constructs ResumeState from checkpoint data
        var resume = new ResumeState(
            NextLink: "https://graph.microsoft.com/v1.0/users?$skiptoken=page2",
            SkipOnFirstPage: 0,
            ItemsAlreadyCollected: 2);

        var items = new List<JsonElement>();
        await foreach (var item in iterator.StreamAllWithCountAsync(
            "https://graph.microsoft.com/v1.0/users",
            0,
            null,
            resume: resume))
        {
            items.Add(item);
        }

        // Should have fetched only page 2 (1 item: "User Three"), not page 1
        Assert.Single(items);

        // Verify request was to page2 URL, not the initial URL
        var requests = handler.Requests;
        Assert.All(requests, r =>
            Assert.Contains("skiptoken", r.RequestUri!.ToString()));

        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public async Task PageIterator_SkipsItemsOnFirstPage()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();

        // Page 1 has 2 items + nextLink, page 2 has 1 item
        handler.QueueResponse(HttpStatusCode.OK, TestData.UsersPage1);
        handler.QueueResponse(HttpStatusCode.OK, TestData.UsersPage2);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var iterator = new PageIterator(client);

        // Skip first item on first page (simulating mid-page resume where 1 item was already written)
        var resume = new ResumeState(
            NextLink: "https://graph.microsoft.com/v1.0/users",
            SkipOnFirstPage: 1,
            ItemsAlreadyCollected: 1);

        var items = new List<JsonElement>();
        await foreach (var item in iterator.StreamAllWithCountAsync(
            "https://graph.microsoft.com/v1.0/users",
            0,
            null,
            resume: resume))
        {
            items.Add(item);
        }

        // Should have skipped 1 item from page 1, yielded 1 from page 1 + 1 from page 2
        Assert.Equal(2, items.Count);
        // First yielded item should be user2 (user1 was skipped)
        Assert.Equal("user2", items[0].GetProperty("id").GetString());
        Assert.Equal("user3", items[1].GetProperty("id").GetString());

        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public async Task PageIterator_OnPageComplete_FiresWithNextLink()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();

        // Two pages
        handler.QueueResponse(HttpStatusCode.OK, TestData.UsersPage1);
        handler.QueueResponse(HttpStatusCode.OK, TestData.UsersPage2);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var iterator = new PageIterator(client);

        var pageCompletions = new List<PageCompletedInfo>();

        var items = new List<JsonElement>();
        await foreach (var item in iterator.StreamAllWithCountAsync(
            "https://graph.microsoft.com/v1.0/users",
            0,
            null,
            onPageComplete: info => pageCompletions.Add(info)))
        {
            items.Add(item);
        }

        Assert.Equal(3, items.Count);

        // Two page completions: after page 1 (has nextLink) and after page 2 (no nextLink)
        Assert.Equal(2, pageCompletions.Count);
        Assert.NotNull(pageCompletions[0].NextPageUrl); // page 1 has nextLink
        Assert.Null(pageCompletions[1].NextPageUrl);     // page 2 is the last page

        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public async Task PageIterator_ResumeItemCount_EnforcesMaxItems()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();

        // Page with 2 items
        handler.SetDefaultResponse(HttpStatusCode.OK, TestData.UsersPage1);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var iterator = new PageIterator(client);

        // Resume with 2 items already collected, maxItems = 3
        // Should only yield 1 more item (to reach 3 total)
        var resume = new ResumeState(
            NextLink: "https://graph.microsoft.com/v1.0/users",
            SkipOnFirstPage: 0,
            ItemsAlreadyCollected: 2);

        var items = new List<JsonElement>();
        await foreach (var item in iterator.StreamAllWithCountAsync(
            "https://graph.microsoft.com/v1.0/users",
            3,
            null,
            resume: resume))
        {
            items.Add(item);
        }

        // Only 1 item yielded (totalYielded starts at 2, maxItems=3, so yield 1)
        Assert.Single(items);

        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public async Task PageIterator_SavesCheckpoint_BetweenPages()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();

        // Two pages: first has nextLink, second doesn't
        handler.QueueResponse(HttpStatusCode.OK, TestData.UsersPage1);
        handler.QueueResponse(HttpStatusCode.OK, TestData.UsersPage2);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var iterator = new PageIterator(client);

        // Consumer saves checkpoint in onPageComplete callback
        var cpPath = TempFile();
        var items = new List<JsonElement>();
        int itemCount = 0;
        await foreach (var item in iterator.StreamAllWithCountAsync(
            "https://graph.microsoft.com/v1.0/users",
            0,
            null,
            onPageComplete: info =>
            {
                if (info.NextPageUrl != null)
                {
                    new PaginationCheckpoint
                    {
                        Resource = "https://graph.microsoft.com/v1.0/users",
                        NextLink = info.NextPageUrl,
                        ItemsCollected = itemCount
                    }.Save(cpPath);
                }
            }))
        {
            items.Add(item);
            itemCount++;
        }

        // All 3 items from both pages
        Assert.Equal(3, items.Count);

        // Checkpoint should have been saved after page 1 (with nextLink to page 2)
        // but consumer deletes it on completion
        // In this test, the consumer doesn't delete it, so check the saved state
        var checkpoint = PaginationCheckpoint.Load(cpPath);
        Assert.NotNull(checkpoint);
        Assert.Contains("skiptoken", checkpoint.NextLink!);
        Assert.Equal(2, checkpoint.ItemsCollected); // 2 items from page 1

        ResiliencePipelineFactory.Reset();
    }
}
