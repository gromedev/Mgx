using System.Net;
using System.Text;
using Mgx.Engine.Http;

namespace Mgx.IntegrationTests;

/// <summary>
/// Caller headers reach the wire as given: content headers land on the content collection
/// instead of being silently dropped, a caller's correlation id is not doubled by mgx's
/// own, and the merge is case-insensitive like HTTP header names are.
/// (Corpus: GraphSDK-2328, missing required headers.)
/// </summary>
[Collection("Pipeline")]
public class HeaderFidelityTests
{
    private static readonly ResilientGraphClientOptions Options = new()
    {
        NoRateLimit = true,
        MaxRetryAttempts = 3,
        TotalTimeoutSeconds = 30,
        AttemptTimeoutSeconds = 10
    };

    private static (MockHttpHandler wire, ResilientGraphClient client) NewClient()
    {
        ResiliencePipelineFactory.Reset();
        var wire = new MockHttpHandler();
        var client = new ResilientGraphClient(new HttpClient(wire), Options);
        return (wire, client);
    }

    [Fact]
    public async Task A_caller_content_type_replaces_the_default_instead_of_vanishing()
    {
        var (wire, client) = NewClient();
        using (client)
        {
            wire.QueueResponse(HttpStatusCode.OK, "{}");
            using var response = await client.SendAsync(HttpMethod.Put,
                "https://graph.microsoft.com/v1.0/drives/d/items/i/content",
                new StringContent("hello", Encoding.UTF8, "application/json"),
                new Dictionary<string, string> { ["Content-Type"] = "text/plain" },
                CancellationToken.None);

            var sent = Assert.Single(wire.CapturedRequests);
            var contentType = Assert.Contains("Content-Type", sent.ContentHeaders);
            Assert.Equal("text/plain", Assert.Single(contentType));
        }
        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public async Task A_content_header_survives_every_retry_attempt()
    {
        var (wire, client) = NewClient();
        using (client)
        {
            wire.QueueFailuresThenSuccess(1, HttpStatusCode.ServiceUnavailable, "{}");
            using var response = await client.SendAsync(HttpMethod.Put,
                "https://graph.microsoft.com/v1.0/drives/d/items/i/content",
                new StringContent("hello", Encoding.UTF8, "application/json"),
                new Dictionary<string, string> { ["Content-Type"] = "text/plain" },
                CancellationToken.None);

            Assert.Equal(2, wire.CapturedRequests.Count);
            foreach (var sent in wire.CapturedRequests)
            {
                var contentType = Assert.Contains("Content-Type", sent.ContentHeaders);
                Assert.Equal("text/plain", Assert.Single(contentType));
            }
        }
        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public async Task A_header_no_collection_accepts_warns_instead_of_vanishing_silently()
    {
        var (wire, client) = NewClient();
        using (client)
        {
            wire.QueueResponse(HttpStatusCode.OK, TestData.SingleUser);
            // A GET has no content, so a content header has nowhere to go.
            using var response = await client.GetAsync(
                "https://graph.microsoft.com/v1.0/users/u1",
                CancellationToken.None,
                new Dictionary<string, string> { ["Content-Type"] = "text/plain" });

            var warnings = new List<string>();
            client.WarningWriter = warnings.Add;
            client.DrainWarningMessages();
            Assert.Contains(warnings, w => w.Contains("Content-Type"));
        }
        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public async Task A_caller_correlation_id_is_not_doubled()
    {
        var (wire, client) = NewClient();
        using (client)
        {
            wire.QueueResponse(HttpStatusCode.OK, TestData.SingleUser);
            using var response = await client.GetAsync(
                "https://graph.microsoft.com/v1.0/users/u1",
                CancellationToken.None,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["client-request-id"] = "caller-supplied-id"
                });

            var sent = Assert.Single(wire.CapturedRequests);
            var ids = Assert.Contains("client-request-id", sent.Headers);
            Assert.Equal("caller-supplied-id", Assert.Single(ids));
        }
        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public void Case_variant_duplicate_keys_merge_to_one_header()
    {
        var headers = TestCmdlet.BuildHeaders(null, new System.Collections.Hashtable
        {
            ["If-Match"] = "\"etag-a\"",
        });
        Assert.NotNull(headers);
        Assert.True(headers!.ContainsKey("if-match"));
        Assert.True(headers.ContainsKey("IF-MATCH"));
    }

    /// <summary>BuildRequestHeaders is protected static; surfaced for assertion.</summary>
    private sealed class TestCmdlet : Mgx.Cmdlets.Base.MgxCmdletBase
    {
        public static Dictionary<string, string>? BuildHeaders(
            string? consistencyLevel, System.Collections.Hashtable? extra)
            => BuildRequestHeaders(consistencyLevel, extra);
    }
}
