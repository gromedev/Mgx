using System.Net;
using System.Text;
using Mgx.Engine.Http;

namespace Mgx.IntegrationTests;

/// <summary>
/// Tests for defensive guards: content size cap (R3-10) and related validations.
/// </summary>
[Collection("Pipeline")]
public class GuardTests
{
    private static readonly ResilientGraphClientOptions NoRateLimitOptions = new()
    {
        NoRateLimit = true,
        MaxRetryAttempts = 1,
        TotalTimeoutSeconds = 30,
        AttemptTimeoutSeconds = 10
    };

    [Fact]
    public async Task ContentSizeGuard_RejectsBodyOver4MB()
    {
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, "{}");

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, NoRateLimitOptions);

        // 4MB + 1 byte
        var oversizedBody = new string('x', ResilientGraphClient.MaxRequestBodyBytes + 1);
        var content = new StringContent(oversizedBody, Encoding.UTF8, "application/json");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SendAsync(HttpMethod.Post, "https://graph.microsoft.com/v1.0/users", content));

        Assert.Contains("4MB limit", ex.Message);
        Assert.Contains("exceeds", ex.Message);
        Assert.Equal(0, handler.RequestCount); // Never sent to server
    }

    [Fact]
    public async Task ContentSizeGuard_AllowsBodyAt4MB()
    {
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, "{}");

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, NoRateLimitOptions);

        // Exactly 4MB
        var maxBody = new string('x', ResilientGraphClient.MaxRequestBodyBytes);
        var content = new StringContent(maxBody, Encoding.UTF8, "application/json");

        var response = await client.SendAsync(HttpMethod.Post, "https://graph.microsoft.com/v1.0/users", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, handler.RequestCount); // Sent to server
    }

    [Fact]
    public async Task ContentSizeGuard_AllowsNullContent()
    {
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, "{}");

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, NoRateLimitOptions);

        var response = await client.SendAsync(HttpMethod.Get, "https://graph.microsoft.com/v1.0/users");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, handler.RequestCount);
    }
}
