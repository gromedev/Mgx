using System.Net;
using System.Text;
using System.Text.Json;
using Mgx.Engine.Http;
using Mgx.Engine.Pagination;

namespace Mgx.IntegrationTests;

/// <summary>
/// Collection sizes that sit on a page seam: exactly full, one over, and the 999/1000/1001
/// band around Graph's largest page. An off-by-one in the follow-the-nextLink loop drops the
/// last page or the last item, and only these sizes expose it.
/// (Corpus: M365DSC-7274, silent truncation.)
/// </summary>
[Collection("Pipeline")]
public class BoundarySizeTests
{
    /// <summary>One page of <paramref name="count"/> items, with a nextLink unless it is last.</summary>
    private static string Page(int firstIndex, int count, int pageNumber, bool isLast)
    {
        var json = new StringBuilder("""{"value":[""");
        for (var i = 0; i < count; i++)
        {
            if (i > 0) json.Append(',');
            json.Append($$"""{"id":"user{{firstIndex + i}}"}""");
        }
        json.Append(']');
        if (!isLast)
        {
            json.Append(""","@odata.nextLink":"https://graph.microsoft.com/v1.0/users?$skiptoken=page""");
            json.Append(pageNumber + 1).Append('"');
        }
        json.Append('}');
        return json.ToString();
    }

    /// <summary>
    /// <paramref name="trailingEmptyPage"/> is the shape Graph actually returns when the
    /// collection divides evenly: the last full page still carries a nextLink, and following
    /// it yields an empty page. Without it a full page is simply the end, which is a
    /// single-request enumeration that crosses no seam at all.
    /// </summary>
    [Theory]
    [InlineData(100, 100, false)]   // exactly one full page, ended by its own absent nextLink
    [InlineData(100, 100, true)]    // the same size, ended by an empty page behind a nextLink
    [InlineData(101, 100, false)]   // one item spills into a second page
    [InlineData(999, 999, false)]   // exactly Graph's largest page
    [InlineData(999, 999, true)]    // and the same, with the empty page Graph appends
    [InlineData(1000, 999, false)]  // one item past it
    [InlineData(1001, 999, false)]  // two items past it
    public async Task Every_item_comes_back_at_a_page_boundary_size(int total, int pageSize, bool trailingEmptyPage)
    {
        var handler = new MockHttpHandler();
        var expectedPages = 0;
        for (var sent = 0; sent < total; sent += pageSize)
        {
            var count = Math.Min(pageSize, total - sent);
            handler.QueueResponse(HttpStatusCode.OK,
                Page(sent + 1, count, expectedPages + 1, isLast: sent + count == total && !trailingEmptyPage));
            expectedPages++;
        }
        if (trailingEmptyPage)
        {
            handler.QueueResponse(HttpStatusCode.OK, Page(total + 1, 0, expectedPages + 1, isLast: true));
            expectedPages++;
        }

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var iterator = new PageIterator(client);

        var items = new List<JsonElement>();
        await foreach (var item in iterator.StreamAllWithCountAsync(
            $"https://graph.microsoft.com/v1.0/users?$top={pageSize}", maxItems: 0, onCount: null))
        {
            items.Add(item);
        }

        Assert.Equal(total, items.Count);
        Assert.Equal(expectedPages, handler.RequestCount);
        Assert.Equal("user1", items[0].GetProperty("id").GetString());
        Assert.Equal($"user{total}", items[^1].GetProperty("id").GetString());
        // MockHttpHandler answers in queue order regardless of URL, so the counts above hold
        // even for an iterator that re-sent the initial URL. Read the URLs to pin the chain.
        Assert.All(handler.Requests.Skip(1).Select((r, i) => (r, i)),
            x => Assert.Equal($"https://graph.microsoft.com/v1.0/users?$skiptoken=page{x.i + 2}",
                x.r.RequestUri!.ToString()));
    }
}
