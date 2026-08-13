using System.Net;
using Mgx.Engine.Http;
using Mgx.Engine.Pagination;

namespace Mgx.IntegrationTests;

/// <summary>
/// Verifies that ConcurrentFanOut.BulkWriteAsync sends correct Content-Type headers
/// for write methods. Graph API requires Content-Type: application/json on POST/PATCH/PUT
/// even when the body is empty. DELETE should NOT send a body.
///
/// These tests cover the engine-level behavior. The cmdlet layer (InvokeMgxRequest)
/// is responsible for passing "{}" as serializedBody for bodyless POST/PATCH/PUT,
/// tested separately via Pester in tests/enduser-tests/.
/// </summary>
[Collection("Pipeline")]
public class ContentTypeTests
{
    private static readonly ResilientGraphClientOptions NoRateLimitOptions = new()
    {
        NoRateLimit = true,
        MaxRetryAttempts = 1,
        TotalTimeoutSeconds = 30,
        AttemptTimeoutSeconds = 10
    };

    [Fact]
    public async Task BulkWrite_POST_WithBody_SendsContentType()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.Created, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, NoRateLimitOptions);
        var fanOut = new ConcurrentFanOut(client, maxConcurrency: 1);

        var ops = new List<(string id, string url)>
        {
            ("op1", "https://graph.microsoft.com/v1.0/users")
        };

        await fanOut.BulkWriteAsync(HttpMethod.Post, ops, """{"displayName":"Test"}""");

        Assert.Equal(1, handler.RequestCount);
        var request = handler.Requests[0];
        Assert.NotNull(request.Content);
        Assert.Equal("application/json", request.Content!.Headers.ContentType?.MediaType);
        var body = await request.Content.ReadAsStringAsync();
        Assert.Contains("displayName", body);

        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public async Task BulkWrite_POST_EmptyJsonBody_SendsContentType()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.NoContent);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, NoRateLimitOptions);
        var fanOut = new ConcurrentFanOut(client, maxConcurrency: 1);

        var ops = new List<(string id, string url)>
        {
            ("op1", "https://graph.microsoft.com/v1.0/directory/deletedItems/abc/permanentDelete")
        };

        // Empty JSON body "{}" simulates bodyless POST from cmdlet layer
        await fanOut.BulkWriteAsync(HttpMethod.Post, ops, "{}");

        Assert.Equal(1, handler.RequestCount);
        var request = handler.Requests[0];
        Assert.NotNull(request.Content);
        Assert.Equal("application/json", request.Content!.Headers.ContentType?.MediaType);
        var body = await request.Content.ReadAsStringAsync();
        Assert.Equal("{}", body);

        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public async Task BulkWrite_POST_NullBody_SendsNoContent()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.NoContent);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, NoRateLimitOptions);
        var fanOut = new ConcurrentFanOut(client, maxConcurrency: 1);

        var ops = new List<(string id, string url)>
        {
            ("op1", "https://graph.microsoft.com/v1.0/some/action")
        };

        // null body: no HttpContent created (pre-fix behavior for reference)
        await fanOut.BulkWriteAsync(HttpMethod.Post, ops, serializedBody: null);

        Assert.Equal(1, handler.RequestCount);
        var request = handler.Requests[0];
        Assert.Null(request.Content);

        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public async Task BulkWrite_DELETE_NullBody_SendsNoContent()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.NoContent);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, NoRateLimitOptions);
        var fanOut = new ConcurrentFanOut(client, maxConcurrency: 1);

        var ops = new List<(string id, string url)>
        {
            ("op1", "https://graph.microsoft.com/v1.0/users/abc")
        };

        await fanOut.BulkWriteAsync(HttpMethod.Delete, ops, serializedBody: null);

        Assert.Equal(1, handler.RequestCount);
        var request = handler.Requests[0];
        Assert.Null(request.Content);

        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public async Task BulkWrite_PATCH_EmptyJsonBody_SendsContentType()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.NoContent);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, NoRateLimitOptions);
        var fanOut = new ConcurrentFanOut(client, maxConcurrency: 1);

        var ops = new List<(string id, string url)>
        {
            ("op1", "https://graph.microsoft.com/v1.0/users/abc")
        };

        await fanOut.BulkWriteAsync(HttpMethod.Patch, ops, "{}");

        Assert.Equal(1, handler.RequestCount);
        var request = handler.Requests[0];
        Assert.NotNull(request.Content);
        Assert.Equal("application/json", request.Content!.Headers.ContentType?.MediaType);

        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public async Task BulkWrite_PUT_EmptyJsonBody_SendsContentType()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.NoContent);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, NoRateLimitOptions);
        var fanOut = new ConcurrentFanOut(client, maxConcurrency: 1);

        var ops = new List<(string id, string url)>
        {
            ("op1", "https://graph.microsoft.com/v1.0/users/abc")
        };

        await fanOut.BulkWriteAsync(HttpMethod.Put, ops, "{}");

        Assert.Equal(1, handler.RequestCount);
        var request = handler.Requests[0];
        Assert.NotNull(request.Content);
        Assert.Equal("application/json", request.Content!.Headers.ContentType?.MediaType);

        ResiliencePipelineFactory.Reset();
    }

    // ═══════════════════════════════════════════════════════════════
    // R3-10: Content size validation before retry buffering
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SendAsync_RejectsOversizedBody()
    {
        var handler = new MockHttpHandler();
        var pipeline = ResiliencePipelineFactory.GetOrCreate(NoRateLimitOptions);
        using var client = new ResilientGraphClient(new HttpClient(handler), pipeline.Pipeline, pipeline.RateLimiter);

        var bigContent = new ByteArrayContent(new byte[5 * 1024 * 1024]); // 5MB
        bigContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SendAsync(HttpMethod.Post, "/users", bigContent));
        Assert.Contains("exceeds", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.RequestCount); // rejected BEFORE HTTP

        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public async Task SendAsync_AllowsNormalSizedBody()
    {
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, "{}");
        var pipeline = ResiliencePipelineFactory.GetOrCreate(NoRateLimitOptions);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com") };
        using var client = new ResilientGraphClient(httpClient, pipeline.Pipeline, pipeline.RateLimiter);

        var normalContent = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        var response = await client.SendAsync(HttpMethod.Post, "/users", normalContent);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(1, handler.RequestCount); // request went through

        ResiliencePipelineFactory.Reset();
    }
}
