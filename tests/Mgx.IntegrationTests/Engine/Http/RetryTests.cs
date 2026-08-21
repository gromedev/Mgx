using System.Diagnostics;
using System.Net;
using System.Text;
using Mgx.Engine.Http;
using Mgx.IntegrationTests.Fakes;

namespace Mgx.IntegrationTests;

[Collection("Pipeline")]
public class RetryTests
{
    [Fact]
    public async Task Retry_On429_RetriesAndSucceeds()
    {
        var handler = new MockHttpHandler();
        handler.QueueFailuresThenSuccess(
            failCount: 2,
            failStatus: (HttpStatusCode)429,
            successBody: TestData.SingleUser,
            failHeaders: new() { ["Retry-After"] = "1" });

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, handler.RequestCount); // 2 failures + 1 success
    }

    [Fact]
    public async Task Retry_On503_RetriesAndSucceeds()
    {
        var handler = new MockHttpHandler();
        handler.QueueFailuresThenSuccess(
            failCount: 3,
            failStatus: HttpStatusCode.ServiceUnavailable,
            successBody: TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(4, handler.RequestCount); // 3 failures + 1 success
    }

    [Fact]
    public async Task Retry_On504_RetriesAndSucceeds()
    {
        var handler = new MockHttpHandler();
        handler.QueueFailuresThenSuccess(
            failCount: 1,
            failStatus: HttpStatusCode.GatewayTimeout,
            successBody: TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Retry_RespectsRetryAfterHeader()
    {
        var handler = new MockHttpHandler();
        handler.QueueResponse((HttpStatusCode)429, null, new() { ["Retry-After"] = "2" });
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var sw = Stopwatch.StartNew();
        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");
        sw.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.RequestCount);
        // Should have waited at least 2 seconds for Retry-After
        Assert.True(sw.ElapsedMilliseconds >= 1800, $"Expected >= 1800ms delay, got {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Retry_UsesExponentialBackoff()
    {
        var handler = new MockHttpHandler();
        // 3 failures, no Retry-After → should use exponential backoff
        handler.QueueFailuresThenSuccess(
            failCount: 3,
            failStatus: HttpStatusCode.ServiceUnavailable,
            successBody: TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var sw = Stopwatch.StartNew();
        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");
        sw.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(4, handler.RequestCount);
        // With base delay of 1s and 3 retries, exponential backoff should take at least:
        // ~1s + ~2s + ~4s = ~7s (with jitter it could be less, but should be > 2s)
        Assert.True(sw.ElapsedMilliseconds >= 2000, $"Expected >= 2000ms total delay, got {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Retry_DoesNotRetryOn400()
    {
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.BadRequest, """{"error":{"message":"Bad request"}}""");

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/bad");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1, handler.RequestCount); // No retry on 400
    }

    [Fact]
    public async Task Retry_DoesNotRetryOn404()
    {
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.NotFound);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/notfound");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, handler.RequestCount); // No retry on 404
    }

    [Fact]
    public async Task Retry_DoesNotRetryOn403()
    {
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.Forbidden);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/forbidden");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(1, handler.RequestCount); // No retry on 403
    }

    [Fact]
    public async Task Retry_MaxRetryExhausted_ReturnsLastFailure()
    {
        var handler = new MockHttpHandler();
        // Queue more failures than max retries (7)
        for (int i = 0; i < 10; i++)
            handler.QueueResponse((HttpStatusCode)429, null, new() { ["Retry-After"] = "0" });

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/throttled");

        Assert.Equal((HttpStatusCode)429, response.StatusCode);
        // Should have made 1 initial + 7 retries = 8 requests
        Assert.Equal(8, handler.RequestCount);
    }

    [Fact]
    public async Task Retry_PostDoesNotRetryOn503()
    {
        // POST is non-idempotent: a 503 may mean the request was partially processed.
        // The retry guard should prevent retrying POST on 503 (only 429 is retried for POST).
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.ServiceUnavailable);
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var response = await client.SendAsync(
            HttpMethod.Post, "https://graph.microsoft.com/v1.0/users",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        // POST should NOT retry on 503: only the initial request, no retry
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Retry_PostRetriesOn429()
    {
        // POST on 429 IS safe to retry (matches Kiota SDK behavior).
        var handler = new MockHttpHandler();
        handler.QueueResponse((HttpStatusCode)429, null, new() { ["Retry-After"] = "0" });
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var response = await client.SendAsync(
            HttpMethod.Post, "https://graph.microsoft.com/v1.0/users",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.RequestCount); // 1 failure + 1 success
    }

    [Fact]
    public async Task Retry_GetRetriesOn500()
    {
        // GET on 500 IS safe to retry (idempotent method).
        var handler = new MockHttpHandler();
        handler.QueueFailuresThenSuccess(
            failCount: 1,
            failStatus: HttpStatusCode.InternalServerError,
            successBody: TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Retry_GetRetriesOn502()
    {
        // GET on 502 IS safe to retry (idempotent method).
        var handler = new MockHttpHandler();
        handler.QueueFailuresThenSuccess(
            failCount: 1,
            failStatus: HttpStatusCode.BadGateway,
            successBody: TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Retry_PostDoesNotRetryOn500()
    {
        // POST should NOT retry on 500 (non-idempotent)
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.InternalServerError);
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var response = await client.SendAsync(
            HttpMethod.Post, "https://graph.microsoft.com/v1.0/users",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Retry_PostDoesNotRetryOn502()
    {
        // POST should NOT retry on 502 (non-idempotent)
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.BadGateway);
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var response = await client.SendAsync(
            HttpMethod.Post, "https://graph.microsoft.com/v1.0/users",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Retry_PreservesAllContentHeadersOnRetry()
    {
        // Verify that ALL content headers (not just ContentType) are preserved on retry
        var handler = new MockHttpHandler();
        handler.QueueResponse((HttpStatusCode)429, null, new() { ["Retry-After"] = "0" });
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        content.Headers.TryAddWithoutValidation("Content-Encoding", "identity");
        content.Headers.TryAddWithoutValidation("Content-Language", "en-US");

        var response = await client.SendAsync(
            HttpMethod.Put, "https://graph.microsoft.com/v1.0/users/user1",
            content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.RequestCount);

        // The retried request (second request) should have all content headers
        var retriedRequest = handler.Requests[1];
        Assert.NotNull(retriedRequest.Content);
        Assert.Contains("identity", retriedRequest.Content!.Headers.ContentEncoding);
        Assert.Contains("en-US", retriedRequest.Content.Headers.ContentLanguage);
        Assert.Equal("application/json", retriedRequest.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Retry_ReplaysTheRequestBodyOnRetry()
    {
        // Content is buffered before the pipeline; without that the second attempt
        // sends an already-consumed stream and Graph sees an empty body. Bodies are
        // captured at send time (the stub) because HttpClient disposes the request
        // once the call completes.
        const string payload = """{"displayName":"Test User"}""";
        var bodies = new List<string>();
        var handler = new StubHttpMessageHandler()
            .EnqueueRepeated(2, request =>
            {
                bodies.Add(request.Content?.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult() ?? "");
                var throttled = new HttpResponseMessage((HttpStatusCode)429)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                };
                throttled.Headers.Add("Retry-After", "0");
                return throttled;
            })
            .Enqueue(request =>
            {
                bodies.Add(request.Content?.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult() ?? "");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                };
            });

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(
            HttpMethod.Patch, "https://graph.microsoft.com/v1.0/users/abc", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, bodies.Count);
        Assert.All(bodies, body => Assert.Equal(payload, body));
    }

    [Fact]
    public async Task Headers_ArePassedThrough()
    {
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var headers = new Dictionary<string, string> { ["ConsistencyLevel"] = "eventual" };
        await client.GetAsync("https://graph.microsoft.com/v1.0/users", default, headers);

        var sentRequest = handler.Requests[0];
        Assert.True(sentRequest.Headers.Contains("ConsistencyLevel"));
        Assert.Equal("eventual", sentRequest.Headers.GetValues("ConsistencyLevel").First());
    }

    [Fact]
    public async Task Retry_ClampsRetryAfterToMaxRetryAfterSeconds()
    {
        // Server sends Retry-After: 120 (way above our test MaxRetryAfterSeconds=30).
        // The pipeline should clamp the delay to MaxRetryAfterSeconds.
        var handler = new MockHttpHandler();
        handler.QueueResponse((HttpStatusCode)429, null, new() { ["Retry-After"] = "120" });
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            MaxRetryAfterSeconds = 30
        });

        var sw = Stopwatch.StartNew();
        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");
        sw.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.RequestCount);
        // Clamped to 30s: actual delay should be around 30s, not 120s.
        // Allow generous lower bound (25s) for timer precision, but must be well under 120s.
        Assert.True(sw.ElapsedMilliseconds < 60_000,
            $"Expected delay clamped to ~30s, but took {sw.ElapsedMilliseconds}ms (server requested 120s)");
        Assert.True(sw.ElapsedMilliseconds >= 25_000,
            $"Expected delay of ~30s (clamped from 120s), but only waited {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Retry_VerboseWriter_FiresOnRetry()
    {
        var handler = new MockHttpHandler();
        handler.QueueResponse((HttpStatusCode)429, null, new() { ["Retry-After"] = "0" });
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var messages = new List<string>();
        client.VerboseWriter = msg => messages.Add(msg);

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");
        client.DrainVerboseMessages(); // Messages are buffered; drain to VerboseWriter

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(messages);
        Assert.Contains("Retry attempt", messages[0]);
        Assert.Contains("429", messages[0]);
    }

    [Fact]
    public async Task Retry_VerboseWriter_LogsClampedRetryAfter()
    {
        // Verify the verbose message mentions clamping when server requests > MaxRetryAfterSeconds
        var handler = new MockHttpHandler();
        handler.QueueResponse((HttpStatusCode)429, null, new() { ["Retry-After"] = "120" });
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            MaxRetryAfterSeconds = 30
        });

        var messages = new List<string>();
        client.VerboseWriter = msg => messages.Add(msg);

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");
        client.DrainVerboseMessages(); // Messages are buffered; drain to VerboseWriter

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(messages);
        Assert.Contains("clamped to", messages[0]);
        Assert.Contains("120", messages[0]); // server requested
        Assert.Contains("30", messages[0]);  // clamped to
    }

    [Fact]
    public async Task Retry_DoesNotRetryOnUserCancellation()
    {
        // Simulate Ctrl+C: user cancels mid-request. The handler cancels the CTS
        // during SendAsync (as HttpClient would when the user token fires), then
        // throws TaskCanceledException. The retry predicate should detect the
        // cancelled context token and NOT retry.
        var cts = new CancellationTokenSource();
        var handler = new CancellingMockHandler(cts);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        // TaskCanceledException extends OperationCanceledException; Polly may wrap either way
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetAsync("https://graph.microsoft.com/v1.0/users/user1", cts.Token));

        Assert.Equal(1, handler.RequestCount); // No retry: only the initial request
    }

    /// <summary>
    /// Handler that simulates user cancellation mid-request.
    /// Cancels the CTS during SendAsync and throws TaskCanceledException with the token.
    /// </summary>
    private sealed class CancellingMockHandler(CancellationTokenSource cts) : HttpMessageHandler
    {
        private int _callCount;
        public int RequestCount => _callCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _callCount);
            cts.Cancel();
            return Task.FromException<HttpResponseMessage>(
                new TaskCanceledException("User cancelled", null, cts.Token));
        }
    }

    [Fact]
    public async Task Retry_RetriesOnNonUserTaskCanceledException()
    {
        // TaskCanceledException from HttpClient timeout (not user cancellation)
        // should be retried since the user didn't cancel.
        var handler = new MockHttpHandler();
        handler.QueueException(new TaskCanceledException("Request timed out"));
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.RequestCount); // 1 timeout + 1 success
    }

    [Fact]
    public async Task Retry_GetRetriesOn408()
    {
        // HTTP 408 Request Timeout is transient and should be retried for idempotent methods.
        var handler = new MockHttpHandler();
        handler.QueueFailuresThenSuccess(
            failCount: 1,
            failStatus: HttpStatusCode.RequestTimeout,
            successBody: TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.RequestCount); // 1 failure + 1 success
    }

    [Fact]
    public async Task Retry_DeleteRetriesOn408()
    {
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.RequestTimeout);
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var response = await client.SendAsync(
            HttpMethod.Delete, "https://graph.microsoft.com/v1.0/users/user1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Retry_PatchRetriesOn408()
    {
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.RequestTimeout);
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var response = await client.SendAsync(
            HttpMethod.Patch, "https://graph.microsoft.com/v1.0/users/user1",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Retry_PostDoesNotRetryOn408()
    {
        // POST is non-idempotent: 408 may mean the request was partially processed.
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.RequestTimeout);
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var response = await client.SendAsync(
            HttpMethod.Post, "https://graph.microsoft.com/v1.0/users",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.RequestTimeout, response.StatusCode);
        Assert.Equal(1, handler.RequestCount); // No retry for POST on 408
    }

    [Fact]
    public async Task Retry_RetriesOnAttemptTimeout_ForIdempotentMethods()
    {
        // Simulate: inner handler takes longer than AttemptTimeoutSeconds (e.g., SDK's
        // RetryHandler honoring a long Retry-After). Polly's attempt timeout fires and
        // throws TimeoutRejectedException. This should be retried for idempotent methods.
        var handler = new SlowThenFastMockHandler(
            slowDelayMs: 5000, // First attempt: takes 5s (exceeds 2s attempt timeout)
            fastResponse: new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(TestData.SingleUser, System.Text.Encoding.UTF8, "application/json")
            });

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            AttemptTimeoutSeconds = 2,   // Short timeout to trigger TimeoutRejectedException
            TotalTimeoutSeconds = 30     // Plenty of time for retry
        });

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.RequestCount); // 1 timeout + 1 success
    }

    [Fact]
    public async Task Retry_DoesNotRetryAttemptTimeout_ForPost()
    {
        // POST is non-idempotent: TimeoutRejectedException should NOT be retried.
        var handler = new SlowThenFastMockHandler(
            slowDelayMs: 5000,
            fastResponse: new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(TestData.SingleUser, System.Text.Encoding.UTF8, "application/json")
            });

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            AttemptTimeoutSeconds = 2,
            TotalTimeoutSeconds = 30
        });

        // POST should fail on timeout without retry
        await Assert.ThrowsAsync<Polly.Timeout.TimeoutRejectedException>(
            () => client.SendAsync(
                HttpMethod.Post, "https://graph.microsoft.com/v1.0/users",
                new StringContent("{}", System.Text.Encoding.UTF8, "application/json")));

        Assert.Equal(1, handler.RequestCount); // No retry for POST
    }

    /// <summary>
    /// Handler that delays on the first request (simulating SDK RetryHandler honoring
    /// a long Retry-After) and responds fast on subsequent requests.
    /// </summary>
    private sealed class SlowThenFastMockHandler(int slowDelayMs, HttpResponseMessage fastResponse) : HttpMessageHandler
    {
        private int _callCount;
        public int RequestCount => Volatile.Read(ref _callCount);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var attempt = Interlocked.Increment(ref _callCount);
            if (attempt == 1)
            {
                // Simulate slow inner handler (SDK RetryHandler waiting on Retry-After)
                await Task.Delay(slowDelayMs, ct);
                // If we reach here, timeout didn't fire (shouldn't happen in test)
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            return fastResponse;
        }
    }

    [Fact]
    public async Task Retry_VerboseWriter_MessagesBufferedUntilDrain()
    {
        // Verify that messages are NOT in the writer before DrainVerboseMessages,
        // and ARE in the writer after DrainVerboseMessages.
        var handler = new MockHttpHandler();
        handler.QueueResponse((HttpStatusCode)429, null, new() { ["Retry-After"] = "0" });
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var messages = new List<string>();
        client.VerboseWriter = msg => messages.Add(msg);

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");

        // Before drain: messages should be empty (buffered in ConcurrentQueue)
        Assert.Empty(messages);

        client.DrainVerboseMessages();

        // After drain: messages should have the retry info
        Assert.Single(messages);
        Assert.Contains("Retry attempt", messages[0]);
    }

    [Fact]
    public async Task ResilientDelegatingHandler_AlwaysClonesRequest()
    {
        // Verify the handler sends a CLONE, not the original request.
        // This is critical for the Enable-MgxResilience SDK bridge path where
        // HttpClient marks the original request's _sendStatus = AlreadySent
        // before the handler runs. Sending the original would throw.
        var capturedRequests = new List<HttpRequestMessage>();
        var capturingHandler = new CapturingMockHandler(capturedRequests);

        var (pipeline, _) = ResiliencePipelineFactory.GetOrCreate(
            new ResilientGraphClientOptions { NoRateLimit = true });

        var handler = new ResilientDelegatingHandler(pipeline, null)
        {
            InnerHandler = capturingHandler
        };

        using var client = new HttpClient(handler);
        var original = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/users");
        original.Headers.TryAddWithoutValidation("X-Test", "original");

        await client.SendAsync(original);

        Assert.Single(capturedRequests);
        var sent = capturedRequests[0];

        // The sent request must NOT be the same instance as the original
        Assert.False(ReferenceEquals(original, sent),
            "Handler must clone the request, not send the original. " +
            "The SDK bridge path marks the original as AlreadySent.");

        // But it must carry the same headers
        Assert.True(sent.Headers.Contains("X-Test"));

        ResiliencePipelineFactory.Reset();
    }

    private sealed class CapturingMockHandler(List<HttpRequestMessage> captured) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            captured.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
