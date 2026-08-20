using System.Net;
using System.Text.Json;
using Mgx.Engine.Http;
using Mgx.Engine.Pagination;

namespace Mgx.IntegrationTests;

[Collection("Pipeline")]
public class SsrfValidationTests
{
    [Fact]
    public async Task PageIterator_RejectsNextLink_DifferentHost()
    {
        var handler = new MockHttpHandler();
        // Page 1 has a nextLink pointing to an attacker's server
        handler.QueueResponse(HttpStatusCode.OK, """
        {
            "value": [{"id": "user1"}],
            "@odata.nextLink": "https://evil.com/steal-token?skiptoken=abc"
        }
        """);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var iterator = new PageIterator(client);

        var items = new List<JsonElement>();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var item in iterator.StreamAllWithCountAsync("https://graph.microsoft.com/v1.0/users", 0, null))
            {
                items.Add(item);
            }
        });

        // Page 1 is still delivered and the malicious nextLink is never fetched - but the
        // caller is told the collection is incomplete rather than left to assume it is whole.
        Assert.Single(items);
        Assert.Equal(1, handler.RequestCount); // Only initial request, no second page fetch
    }

    /// <summary>
    /// A refused deltaLink is dropped, not thrown: by the time it arrives every item has been
    /// delivered, so the enumeration completes and only the token fails to advance. The drop
    /// must not be silent - without a warning the caller reports "no delta token received"
    /// and misdiagnoses a refused token as an endpoint that does not support delta.
    /// </summary>
    [Fact]
    public async Task PageIterator_RejectedDeltaLink_CompletesWithAWarningInsteadOfSavingIt()
    {
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, """
        {
            "value": [{"id": "user1"}, {"id": "user2"}],
            "@odata.deltaLink": "https://evil.com/steal-token?$deltatoken=abc"
        }
        """);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var iterator = new PageIterator(client);

        string? capturedDeltaLink = null;
        var items = new List<JsonElement>();
        await foreach (var item in iterator.StreamAllWithCountAsync(
            "https://graph.microsoft.com/v1.0/users/delta", 0, null,
            onDeltaLink: dl => capturedDeltaLink = dl))
        {
            items.Add(item);
        }

        Assert.Equal(2, items.Count);            // the enumeration completed
        Assert.Null(capturedDeltaLink);          // the poisoned token was never surfaced
        Assert.Equal(1, handler.RequestCount);   // and never fetched

        var warnings = new List<string>();
        client.WarningWriter = warnings.Add;
        client.DrainWarningMessages();
        Assert.Contains(warnings, w => w.Contains("deltaLink") && w.Contains("failed validation"));
    }

    [Fact]
    public async Task PageIterator_RejectsNextLink_HttpScheme()
    {
        var handler = new MockHttpHandler();
        // nextLink uses HTTP (not HTTPS) - token would be sent in plaintext
        handler.QueueResponse(HttpStatusCode.OK, """
        {
            "value": [{"id": "user1"}],
            "@odata.nextLink": "http://graph.microsoft.com/v1.0/users?$skiptoken=page2"
        }
        """);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var iterator = new PageIterator(client);

        var items = new List<JsonElement>();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var item in iterator.StreamAllWithCountAsync("https://graph.microsoft.com/v1.0/users", 0, null))
            {
                items.Add(item);
            }
        });

        Assert.Single(items);                   // page 1 was still delivered
        Assert.Equal(1, handler.RequestCount);  // and the refused link was never fetched
    }

    [Fact]
    public async Task PageIterator_RejectsNextLink_DifferentPort()
    {
        var handler = new MockHttpHandler();
        // nextLink uses a different port (port-based redirect attack)
        handler.QueueResponse(HttpStatusCode.OK, """
        {
            "value": [{"id": "user1"}],
            "@odata.nextLink": "https://graph.microsoft.com:8443/v1.0/users?$skiptoken=page2"
        }
        """);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var iterator = new PageIterator(client);

        var items = new List<JsonElement>();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var item in iterator.StreamAllWithCountAsync("https://graph.microsoft.com/v1.0/users", 0, null))
            {
                items.Add(item);
            }
        });

        Assert.Single(items);                   // page 1 was still delivered
        Assert.Equal(1, handler.RequestCount);  // and the refused link was never fetched
    }

    [Fact]
    public async Task PageIterator_AcceptsNextLink_SameHost()
    {
        var handler = new MockHttpHandler();
        // Valid nextLink to same host
        handler.QueueResponse(HttpStatusCode.OK, """
        {
            "value": [{"id": "user1"}],
            "@odata.nextLink": "https://graph.microsoft.com/v1.0/users?$skiptoken=page2"
        }
        """);
        handler.QueueResponse(HttpStatusCode.OK, """
        {
            "value": [{"id": "user2"}]
        }
        """);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var iterator = new PageIterator(client);

        var items = new List<JsonElement>();
        await foreach (var item in iterator.StreamAllWithCountAsync("https://graph.microsoft.com/v1.0/users", 0, null))
        {
            items.Add(item);
        }

        Assert.Equal(2, items.Count);
        Assert.Equal(2, handler.RequestCount); // Both pages fetched
    }

    [Fact]
    public async Task ConcurrentFanOut_RejectsNextLink_DifferentHost()
    {
        var handler = new MockHttpHandler();
        // First URL's response has a nextLink to attacker's server
        handler.QueueResponse(HttpStatusCode.OK, """
        {
            "value": [{"id": "member1"}],
            "@odata.nextLink": "https://evil.com/steal?skiptoken=abc"
        }
        """);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var fanOut = new ConcurrentFanOut(client);

        var result = await fanOut.FetchAllAsync(
            ["https://graph.microsoft.com/v1.0/groups/g1/members"]);

        // The refused link is reported against its own URL instead of silently yielding a
        // partial member list. Other URLs in the same fan-out are unaffected.
        Assert.False(result.Results.ContainsKey("https://graph.microsoft.com/v1.0/groups/g1/members"));
        Assert.True(result.Errors.ContainsKey("https://graph.microsoft.com/v1.0/groups/g1/members"));
        Assert.IsType<InvalidOperationException>(result.Errors["https://graph.microsoft.com/v1.0/groups/g1/members"]);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task ConcurrentFanOut_RejectsNextLink_HttpScheme()
    {
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, """
        {
            "value": [{"id": "member1"}],
            "@odata.nextLink": "http://graph.microsoft.com/v1.0/groups/g1/members?$skiptoken=page2"
        }
        """);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var fanOut = new ConcurrentFanOut(client);

        var result = await fanOut.FetchAllAsync(
            ["https://graph.microsoft.com/v1.0/groups/g1/members"]);

        Assert.False(result.Results.ContainsKey("https://graph.microsoft.com/v1.0/groups/g1/members"));
        Assert.IsType<InvalidOperationException>(result.Errors["https://graph.microsoft.com/v1.0/groups/g1/members"]);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task ConcurrentFanOut_RejectsNextLink_DifferentPort()
    {
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, """
        {
            "value": [{"id": "member1"}],
            "@odata.nextLink": "https://graph.microsoft.com:8443/v1.0/groups/g1/members?$skiptoken=page2"
        }
        """);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var fanOut = new ConcurrentFanOut(client);

        var result = await fanOut.FetchAllAsync(
            ["https://graph.microsoft.com/v1.0/groups/g1/members"]);

        Assert.False(result.Results.ContainsKey("https://graph.microsoft.com/v1.0/groups/g1/members"));
        Assert.IsType<InvalidOperationException>(result.Errors["https://graph.microsoft.com/v1.0/groups/g1/members"]);
        Assert.Equal(1, handler.RequestCount);
    }

    // ═══════════════════════════════════════════════════════════════
    // R3-14: Path-prefix validation on NextLinkValidator
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Validate_RejectsCrossResourceNextLink()
    {
        // Same host but different resource path — checkpoint tampering attack
        var result = NextLinkValidator.Validate(
            "https://graph.microsoft.com/v1.0/me/messages?$skip=10",
            new Uri("https://graph.microsoft.com"),
            expectedPathPrefix: "/v1.0/users");
        Assert.Null(result); // /me/messages does not start with /v1.0/users
    }

    [Fact]
    public void Validate_AcceptsSameResourceNextLink()
    {
        var result = NextLinkValidator.Validate(
            "https://graph.microsoft.com/v1.0/users?$skiptoken=abc",
            new Uri("https://graph.microsoft.com"),
            expectedPathPrefix: "/v1.0/users");
        Assert.Equal("https://graph.microsoft.com/v1.0/users?$skiptoken=abc", result);
    }

    [Fact]
    public void Validate_BackwardCompatible_NoPrefixAcceptsAnyPath()
    {
        // Existing callers that don't pass expectedPathPrefix must still work
        var result = NextLinkValidator.Validate(
            "https://graph.microsoft.com/v1.0/me/messages?$skip=10",
            new Uri("https://graph.microsoft.com"));
        Assert.NotNull(result);
    }

    [Fact]
    public void Validate_PathPrefixIsCaseInsensitive()
    {
        var result = NextLinkValidator.Validate(
            "https://graph.microsoft.com/V1.0/Users?$skiptoken=abc",
            new Uri("https://graph.microsoft.com"),
            expectedPathPrefix: "/v1.0/users");
        Assert.NotNull(result);
    }

    [Fact]
    public void Validate_RejectsHostThatOnlySharesAPrefix()
    {
        // graph.microsoft.com.evil.example.com must not pass a naive prefix check
        var result = NextLinkValidator.Validate(
            "https://graph.microsoft.com.evil.example.com/v1.0/users",
            new Uri("https://graph.microsoft.com"));
        Assert.Null(result);
    }

    [Fact]
    public void Validate_RejectsNullNextLinkAndNullExpectedHost()
    {
        Assert.Null(NextLinkValidator.Validate(null, new Uri("https://graph.microsoft.com")));
        Assert.Null(NextLinkValidator.Validate("https://graph.microsoft.com/v1.0/users", null));
    }

    [Fact]
    public async Task PageIterator_RejectsNextLink_MalformedUrl()
    {
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, """
        {
            "value": [{"id": "user1"}],
            "@odata.nextLink": "not-a-url"
        }
        """);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var iterator = new PageIterator(client);

        var items = new List<JsonElement>();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var item in iterator.StreamAllWithCountAsync("https://graph.microsoft.com/v1.0/users", 0, null))
            {
                items.Add(item);
            }
        });

        Assert.Single(items);                   // page 1 was still delivered
        Assert.Equal(1, handler.RequestCount);  // and the refused link was never fetched
    }
}
