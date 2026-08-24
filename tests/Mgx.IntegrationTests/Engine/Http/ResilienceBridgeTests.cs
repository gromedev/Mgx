using System.Net;
using Mgx.Cmdlets.Cmdlets.Configuration;
using Mgx.Engine.Http;
using Microsoft.Kiota.Http.HttpClientLibrary.Middleware;
using Microsoft.Kiota.Http.HttpClientLibrary.Middleware.Options;
using Xunit;

namespace Mgx.IntegrationTests.Engine.Http;

/// <summary>
/// The Enable-MgxResilience bridge wraps the SDK's handler chain, and that chain already
/// contains Kiota's own RetryHandler. It answers 429 and 503 itself, so without intervention
/// the outer pipeline never sees a throttle: the pacer does not slow down and telemetry books
/// a throttled session as zero retries.
///
/// These run against the real Kiota handler rather than a stand-in, because a stand-in would
/// only prove that a mock agrees with the assumption being tested.
/// </summary>
[Collection("Pipeline")]
public class ResilienceBridgeTests
{
    private const string RetryOptionKey =
        "Microsoft.Kiota.Http.HttpClientLibrary.Middleware.Options.RetryHandlerOption";

    /// <summary>Kiota's RetryHandler only retries a request it can rewind, so give it a body.</summary>
    private static HttpRequestMessage BufferedPost() =>
        new(HttpMethod.Post, "https://graph.microsoft.com/v1.0/users")
        {
            Content = new StringContent("{}")
        };

    private static HttpClient BuildBridge(
        ThrottlingMockHandler wire,
        Func<IReadOnlyDictionary<string, object?>?>? factory,
        out ResilientDelegatingHandler bridge)
    {
        // The real SDK shape: our handler on the outside, Kiota's retry handler inside it,
        // the network at the bottom.
        var kiota = new RetryHandler(new RetryHandlerOption { MaxRetry = 3, Delay = 0 })
        {
            InnerHandler = wire
        };

        var (pipeline, _) = ResiliencePipelineFactory.GetOrCreate(
            new ResilientGraphClientOptions { NoRateLimit = true, MaxRetryAttempts = 3 });

        bridge = new ResilientDelegatingHandler(pipeline, null)
        {
            AdditionalRequestOptionsFactory = factory,
            InnerHandler = kiota
        };

        return new HttpClient(bridge);
    }

    [Fact]
    public async Task Bridge_WithoutOverride_InnerRetryHandlerHidesTheThrottle()
    {
        // Pins the defect the override exists to fix. If this ever goes green on its own,
        // the SDK stopped retrying internally and the override is no longer load-bearing.
        MgxTelemetryCollector.Current.Reset();
        var wire = new ThrottlingMockHandler(throttleFirst: 1);

        using var client = BuildBridge(wire, factory: null, out _);
        var response = await client.SendAsync(BufferedPost());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, wire.Requests);           // the retry happened...
        var snapshot = MgxTelemetryCollector.Current.GetSummary();
        Assert.Equal(0, snapshot.ThrottleRetries); // ...but it happened where we cannot see it

        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public async Task Bridge_WithOverride_ThrottleReachesTheOuterPipeline()
    {
        MgxTelemetryCollector.Current.Reset();
        var wire = new ThrottlingMockHandler(throttleFirst: 1);

        using var client = BuildBridge(
            wire,
            () => new Dictionary<string, object?> { [RetryOptionKey] = new RetryHandlerOption { MaxRetry = 0 } },
            out _);

        var response = await client.SendAsync(BufferedPost());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, wire.Requests);
        var snapshot = MgxTelemetryCollector.Current.GetSummary();
        Assert.True(snapshot.ThrottleRetries > 0,
            $"the 429 must reach the outer pipeline; ThrottleRetries was {snapshot.ThrottleRetries}");

        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public async Task Bridge_ResolvesOptionsLazily_AndOnlyOnce()
    {
        // The option's type belongs to the SDK and is not loaded until the SDK has sent
        // something through its own chain. Building it eagerly found nothing and left the
        // inner handler armed, which is why the factory is called on first send, not at
        // construction.
        MgxTelemetryCollector.Current.Reset();
        var calls = 0;
        var wire = new ThrottlingMockHandler(throttleFirst: 0);

        using var client = BuildBridge(
            wire,
            () =>
            {
                calls++;
                return new Dictionary<string, object?> { [RetryOptionKey] = new RetryHandlerOption { MaxRetry = 0 } };
            },
            out _);

        Assert.Equal(0, calls); // not at construction

        await client.SendAsync(BufferedPost());
        Assert.Equal(1, calls);

        await client.SendAsync(BufferedPost());
        Assert.Equal(1, calls); // cached, including a null result

        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public async Task Bridge_WithTheCmdletsOwnOption_ThrottleReachesTheOuterPipeline()
    {
        // Covers the half a hand-written dictionary cannot: the cmdlet resolves the SDK's option
        // type reflectively, and nothing checks its spelling at compile time. This feeds what the
        // cmdlet actually produced to the real handler.
        _ = typeof(RetryHandlerOption); // the SDK has loaded this before the wrap sends anything

        var built = EnableMgxResilience.BuildInnerRetryOverride();
        Assert.NotNull(built);
        Assert.True(built!.ContainsKey(RetryOptionKey),
            $"keyed as [{string.Join(", ", built.Keys)}], which is not what Kiota reads");

        MgxTelemetryCollector.Current.Reset();
        var wire = new ThrottlingMockHandler(throttleFirst: 1);

        using var client = BuildBridge(wire, () => built, out _);
        var response = await client.SendAsync(BufferedPost());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = MgxTelemetryCollector.Current.GetSummary();
        Assert.True(summary.ThrottleRetries > 0,
            $"the cmdlet's own option must disarm the inner handler; ThrottleRetries was {summary.ThrottleRetries}");

        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public async Task Bridge_FactoryThatThrows_LeavesTheRequestAlone()
    {
        // The factory resolves on a request thread, after the cmdlet that supplied it has
        // finished its pipeline. A late WriteWarning throws there, and before the guard that
        // exception failed the request the option was only meant to annotate - turning a
        // best-effort optimization into an outage.
        MgxTelemetryCollector.Current.Reset();
        var wire = new ThrottlingMockHandler(throttleFirst: 0);

        using var client = BuildBridge(
            wire,
            () => throw new InvalidOperationException("no pipeline to write to"),
            out _);

        var response = await client.SendAsync(BufferedPost());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, wire.Requests);

        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public async Task Bridge_WithOverride_NoLongerRetriesA503OnAWrite()
    {
        // Disarming the SDK's retry handler is not only about 429: Kiota also retries 503 and
        // 504, and it retries them on POST. Mgx's pipeline deliberately will not - a 5xx on a
        // non-idempotent request may mean the write was partially applied, and retrying it
        // risks doing the work twice.
        //
        // ShouldRetry cannot decline 429 on its own - the handler ORs it with its own status
        // check - so disarming it takes the 503 and 504 retries with it. This pins the
        // consequence: a 503 on a write now reaches the caller instead of being retried out of
        // sight. That is the safer answer for a write, but it is a change, and it should fail
        // here rather than surprise someone.
        MgxTelemetryCollector.Current.Reset();
        var wire = new ThrottlingMockHandler(throttleFirst: 1, code: HttpStatusCode.ServiceUnavailable);

        using var client = BuildBridge(
            wire,
            () => new Dictionary<string, object?> { [RetryOptionKey] = new RetryHandlerOption { MaxRetry = 0 } },
            out _);

        var response = await client.SendAsync(BufferedPost());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, wire.Requests);
    }

    [Fact]
    public async Task Bridge_WithoutOverride_TheSdkRetriesA503OnAWrite()
    {
        // The other half of the pair: left armed, Kiota answers the 503 itself and the write
        // is repeated. This is what the override above stops.
        MgxTelemetryCollector.Current.Reset();
        var wire = new ThrottlingMockHandler(throttleFirst: 1, code: HttpStatusCode.ServiceUnavailable);

        using var client = BuildBridge(wire, factory: null, out _);
        var response = await client.SendAsync(BufferedPost());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, wire.Requests);
    }

    private sealed class ThrottlingMockHandler(int throttleFirst, HttpStatusCode code = HttpStatusCode.TooManyRequests) : HttpMessageHandler
    {
        private int _seen;
        public int Requests => _seen;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var n = Interlocked.Increment(ref _seen);
            if (n <= throttleFirst)
            {
                var throttled = new HttpResponseMessage(code)
                {
                    // The real socket handler always sets this, and Kiota's retry loop rebuilds
                    // the request from it. A mock that leaves it null makes the SDK look like it
                    // never retries, which is the opposite of what is being tested here.
                    RequestMessage = request
                };
                throttled.Headers.TryAddWithoutValidation("Retry-After", "0");
                return Task.FromResult(throttled);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("{\"value\":[]}")
            });
        }
    }

    [Fact]
    public async Task Bridge_stamps_one_correlation_id_per_logical_request()
    {
        ResiliencePipelineFactory.Reset();
        var wire = new MockHttpHandler();
        wire.QueueFailuresThenSuccess(1, HttpStatusCode.ServiceUnavailable, "{}");

        var (pipeline, _) = ResiliencePipelineFactory.GetOrCreate(
            new ResilientGraphClientOptions { NoRateLimit = true, MaxRetryAttempts = 3 });
        using var handler = new ResilientDelegatingHandler(pipeline, null) { InnerHandler = wire };
        using var client = new HttpClient(handler);

        var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/users"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var captured = wire.CapturedRequests;
        Assert.Equal(2, captured.Count);
        var first = Assert.Single(Assert.Contains("client-request-id", captured[0].Headers));
        var second = Assert.Single(Assert.Contains("client-request-id", captured[1].Headers));
        Assert.Equal(first, second);
        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public async Task Bridge_keeps_the_sdk_correlation_id_when_one_is_already_set()
    {
        ResiliencePipelineFactory.Reset();
        var wire = new MockHttpHandler();
        wire.QueueResponse(HttpStatusCode.OK, "{}");

        var (pipeline, _) = ResiliencePipelineFactory.GetOrCreate(
            new ResilientGraphClientOptions { NoRateLimit = true, MaxRetryAttempts = 3 });
        using var handler = new ResilientDelegatingHandler(pipeline, null) { InnerHandler = wire };
        using var client = new HttpClient(handler);

        var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/users");
        request.Headers.TryAddWithoutValidation("client-request-id", "sdk-set-id");
        await client.SendAsync(request);

        var sent = Assert.Single(wire.CapturedRequests);
        Assert.Equal("sdk-set-id", Assert.Single(Assert.Contains("client-request-id", sent.Headers)));
        ResiliencePipelineFactory.Reset();
    }
}
