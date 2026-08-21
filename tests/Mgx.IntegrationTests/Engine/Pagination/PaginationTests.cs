using System.Net;
using System.Text.Json;
using Mgx.Engine.Http;
using Mgx.Engine.Pagination;

namespace Mgx.IntegrationTests;

[Collection("Pipeline")]
public class PaginationTests
{
    [Fact]
    public async Task PageIterator_FollowsNextLink()
    {
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, TestData.UsersPage1);
        handler.QueueResponse(HttpStatusCode.OK, TestData.UsersPage2);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var iterator = new PageIterator(client);

        var items = new List<JsonElement>();
        await foreach (var item in iterator.StreamAllWithCountAsync("https://graph.microsoft.com/v1.0/users?$top=2", 0, null))
        {
            items.Add(item);
        }

        Assert.Equal(3, items.Count); // 2 from page 1 + 1 from page 2
        Assert.Equal("user1", items[0].GetProperty("id").GetString());
        Assert.Equal("user3", items[2].GetProperty("id").GetString());
        Assert.Equal(2, handler.RequestCount); // 2 pages fetched
    }

    [Fact]
    public async Task PageIterator_RespectsMaxItems()
    {
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, TestData.UsersPage1);
        // Page 2 should NOT be fetched since maxItems=1

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var iterator = new PageIterator(client);

        var items = new List<JsonElement>();
        await foreach (var item in iterator.StreamAllWithCountAsync("https://graph.microsoft.com/v1.0/users", 1, null))
        {
            items.Add(item);
        }

        Assert.Single(items);
        Assert.Equal(1, handler.RequestCount); // Only 1 page fetched
    }

    [Fact]
    public async Task PageIterator_CapturesODataCount()
    {
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, """
        {
            "value": [{"id": "user1"}],
            "@odata.count": 42
        }
        """);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var iterator = new PageIterator(client);

        long? capturedCount = null;
        await foreach (var item in iterator.StreamAllWithCountAsync(
            "https://graph.microsoft.com/v1.0/users",
            maxItems: 0,
            onCount: c => capturedCount = c))
        {
            // consume
        }

        Assert.Equal(42L, capturedCount);
    }

    [Fact]
    public async Task PageIterator_HandlesEmptyCollection()
    {
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, TestData.EmptyCollection);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var iterator = new PageIterator(client);

        var items = new List<JsonElement>();
        await foreach (var item in iterator.StreamAllWithCountAsync("https://graph.microsoft.com/v1.0/users", 0, null))
        {
            items.Add(item);
        }

        Assert.Empty(items);
    }

    [Fact]
    public async Task PageIterator_RetriesOnFailureMidPagination()
    {
        var handler = new MockHttpHandler();
        // Page 1 succeeds
        handler.QueueResponse(HttpStatusCode.OK, TestData.UsersPage1);
        // Page 2 fails with 429 then succeeds
        handler.QueueResponse((HttpStatusCode)429, null, new() { ["Retry-After"] = "0" });
        handler.QueueResponse(HttpStatusCode.OK, TestData.UsersPage2);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var iterator = new PageIterator(client);

        var items = new List<JsonElement>();
        await foreach (var item in iterator.StreamAllWithCountAsync("https://graph.microsoft.com/v1.0/users?$top=2", 0, null))
        {
            items.Add(item);
        }

        Assert.Equal(3, items.Count); // All items despite mid-pagination 429
        Assert.Equal(3, handler.RequestCount); // page1 + 429 + page2
    }

    [Fact]
    public async Task PageIterator_PassesHeadersOnAllPages()
    {
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, TestData.UsersPage1);
        handler.QueueResponse(HttpStatusCode.OK, TestData.UsersPage2);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var iterator = new PageIterator(client);

        var headers = new Dictionary<string, string> { ["ConsistencyLevel"] = "eventual" };
        await foreach (var item in iterator.StreamAllWithCountAsync(
            "https://graph.microsoft.com/v1.0/users",
            0,
            null,
            headers: headers))
        {
            // consume
        }

        // Both page requests should have the ConsistencyLevel header
        Assert.Equal(2, handler.RequestCount);
        foreach (var req in handler.Requests)
        {
            Assert.True(req.Headers.Contains("ConsistencyLevel"));
            Assert.Equal("eventual", req.Headers.GetValues("ConsistencyLevel").First());
        }
    }

    [Fact]
    public async Task PageIterator_RejectedNextLink_Throws_RatherThanTruncatingSilently()
    {
        // A nextLink that fails validation is indistinguishable, at the loop level, from "no
        // more pages" - both leave the validator returning null. Ending the iteration quietly
        // hands the caller a partial collection and exit code 0, which is the failure mode the
        // validator exists to prevent: against a response that is actually hostile, silence
        // lets the sender truncate the result set and never be noticed.
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, """
        {
            "value": [
                {"id": "user1", "displayName": "User One"},
                {"id": "user2", "displayName": "User Two"}
            ],
            "@odata.nextLink": "https://evil.example.com/v1.0/users?$skiptoken=page2"
        }
        """);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var iterator = new PageIterator(client);

        var items = new List<JsonElement>();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var item in iterator.StreamAllWithCountAsync("https://graph.microsoft.com/v1.0/users", 0, null))
                items.Add(item);
        });

        Assert.Contains("nextLink", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, items.Count); // the valid page is still delivered before the failure
    }
}
