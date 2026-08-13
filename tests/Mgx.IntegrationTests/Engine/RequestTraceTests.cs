using System.Net;
using System.Text;
using Mgx.Engine.Http;
using Mgx.IntegrationTests.Fakes;

namespace Mgx.IntegrationTests.Engine;

/// <summary>
/// -Debug tracing. Messages are buffered like verbose/warning output, so nothing reaches the
/// writer until the pipeline thread drains them.
/// </summary>
[Collection("Pipeline")]
public class RequestTraceTests
{
    private static ResilientGraphClientOptions TestOptions() => new()
    {
        MaxRetryAttempts = 3,
        NoRateLimit = true,
        CircuitBreakerMinThroughput = 1000,
        AttemptTimeoutSeconds = 10,
        TotalTimeoutSeconds = 60
    };

    private static CancellationToken Ct => CancellationToken.None;

    private static (ResilientGraphClient Client, HttpClient Http, List<string> Debug) NewClient(
        StubHttpMessageHandler handler, bool debugEnabled)
    {
        ResiliencePipelineFactory.Reset();
        MgxTelemetryCollector.Current.Reset();

        var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com") };
        var messages = new List<string>();
        var client = new ResilientGraphClient(http, TestOptions())
        {
            DebugEnabled = debugEnabled,
            DebugWriter = messages.Add
        };
        return (client, http, messages);
    }

    private static StringContent JsonBody(string json) =>
        new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Traces_the_request_body_that_goes_on_the_wire()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(HttpStatusCode.OK, """{"value":[{"id":"a"}]}""");
        var (client, http, debug) = NewClient(handler, debugEnabled: true);
        using var _ = http;

        using var response = await client.SendAsync(HttpMethod.Post, "https://graph.microsoft.com/beta/directoryObjects/getByIds",
            JsonBody("""{"ids":["56ae142c"]}"""), null, Ct);
        client.DrainDebugMessages();

        var request = Assert.Single(debug, m => m.StartsWith("[Mgx] Request", StringComparison.Ordinal));
        Assert.Contains("POST https://graph.microsoft.com/beta/directoryObjects/getByIds", request);
        Assert.Contains("""{"ids":["56ae142c"]}""", request);
        Assert.Contains("client-request-id:", request);
    }

    [Fact]
    public async Task Traces_the_response_and_leaves_the_body_readable()
    {
        const string payload = """{"value":[{"id":"a"}]}""";
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, payload);
        var (client, http, debug) = NewClient(handler, debugEnabled: true);
        using var _ = http;

        using var response = await client.SendAsync(HttpMethod.Get, "https://graph.microsoft.com/v1.0/users",
            cancellationToken: Ct);
        client.DrainDebugMessages();

        var trace = Assert.Single(debug, m => m.StartsWith("[Mgx] Response", StringComparison.Ordinal));
        Assert.Contains("200 OK", trace);
        Assert.Contains(payload, trace);
        // Buffering for the trace must not consume the body the caller still has to read
        Assert.Equal(payload, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Redacts_credentials_from_the_traced_body()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, "{}");
        var (client, http, debug) = NewClient(handler, debugEnabled: true);
        using var _ = http;

        using var response = await client.SendAsync(HttpMethod.Post, "https://graph.microsoft.com/v1.0/applications",
            JsonBody("""{"displayName":"App","passwordCredential":{"secretText":"hunter2"}}"""), null, Ct);
        client.DrainDebugMessages();

        var request = Assert.Single(debug, m => m.StartsWith("[Mgx] Request", StringComparison.Ordinal));
        Assert.DoesNotContain("hunter2", request);
        Assert.Contains("<redacted>", request);
        Assert.Contains("Authorization: Bearer <redacted>", request);
        Assert.Contains("App", request);
    }

    [Fact]
    public async Task Truncates_a_large_body()
    {
        var big = new string('x', GraphRequestTracer.MaxBodyChars + 500);
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, $$"""{"note":"{{big}}"}""");
        var (client, http, debug) = NewClient(handler, debugEnabled: true);
        using var _ = http;

        using var response = await client.SendAsync(HttpMethod.Get, "https://graph.microsoft.com/v1.0/users",
            cancellationToken: Ct);
        client.DrainDebugMessages();

        var trace = Assert.Single(debug, m => m.StartsWith("[Mgx] Response", StringComparison.Ordinal));
        Assert.Contains("[truncated,", trace);
    }

    [Fact]
    public async Task Traces_every_retry_attempt()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueStatus((HttpStatusCode)429, retryAfterSeconds: 0)
            .EnqueueJson(HttpStatusCode.OK, "{}");
        var (client, http, debug) = NewClient(handler, debugEnabled: true);
        using var _ = http;

        using var response = await client.SendAsync(HttpMethod.Get, "https://graph.microsoft.com/v1.0/users",
            cancellationToken: Ct);
        client.DrainDebugMessages();

        Assert.Contains(debug, m => m.Contains("Request (attempt 2)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Emits_nothing_when_debug_is_off()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, """{"id":"a"}""");
        var (client, http, debug) = NewClient(handler, debugEnabled: false);
        using var _ = http;

        using var response = await client.SendAsync(HttpMethod.Get, "https://graph.microsoft.com/v1.0/users",
            cancellationToken: Ct);
        client.DrainDebugMessages();

        Assert.Empty(debug);
    }
}
