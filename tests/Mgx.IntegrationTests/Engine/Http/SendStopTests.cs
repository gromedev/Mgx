using System.Net;
using Mgx.Engine.Http;

namespace Mgx.IntegrationTests;

/// <summary>
/// A caller that has stopped sending - a batch chunk refused while its siblings are still going -
/// hands the transport a stop token. What that stop is allowed to reach is the whole contract:
/// the decision to send again, never the attempt already on the wire, whose answer is the only
/// record of what the server applied. Driven here against the transport directly, so the states
/// the guard arbitrates are named rather than inferred from a chunked batch.
/// </summary>
[Collection("Pipeline")]
public class SendStopTests
{
    private static readonly ResilientGraphClientOptions Options = new()
    {
        NoRateLimit = true,
        MaxRetryAttempts = 2,
        TotalTimeoutSeconds = 120,
        AttemptTimeoutSeconds = 60
    };

    /// <summary>
    /// Holds the first POST open until the test says so, and answers it with a status the
    /// pipeline would ordinarily retry. The stop is set while that POST is on the wire.
    /// </summary>
    private sealed class HoldsThePostOpenHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _posts;

        public Task Entered => _entered.Task;
        public void Release() => _release.TrySetResult();
        public int Posts => Volatile.Read(ref _posts);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _posts);
            _entered.TrySetResult();
            await _release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                RequestMessage = request,
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    /// <summary>
    /// The attempt is on the wire when the stop arrives. Ending it there would trade the answer
    /// the server is about to give for a write that may or may not have been applied - the very
    /// thing the caller stopped in order to keep. The stop reaches the next attempt instead,
    /// which never goes out.
    /// </summary>
    [Fact]
    public async Task A_stop_that_lands_on_an_attempt_in_flight_leaves_it_alone()
    {
        ResiliencePipelineFactory.Reset();
        var wire = new HoldsThePostOpenHandler();
        using var httpClient = new HttpClient(wire);
        using var client = new ResilientGraphClient(httpClient, Options);
        using var stop = new CancellationTokenSource();

        var call = client.PostAsync("https://graph.microsoft.com/v1.0/$batch",
            new StringContent("{}"), CancellationToken.None,
            paceGate: false, stopRetries: stop.Token);

        await wire.Entered.WaitAsync(TimeSpan.FromSeconds(30));
        stop.Cancel();
        wire.Release();

        using var response = await call.WaitAsync(TimeSpan.FromSeconds(30));

        // The answer to the attempt that was flying survives the stop, in full.
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        // A 429 on a POST is the one thing this pipeline retries, and it did not: the stop
        // reached the decision to send again.
        Assert.Equal(1, wire.Posts);
    }

    /// <summary>
    /// Answers the first POST with a 429 asking for a minute, so the request is unambiguously
    /// inside its backoff when the stop arrives.
    /// </summary>
    private sealed class ThrottlesForAMinuteHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _answered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _posts;

        public Task Answered => _answered.Task;
        public int Posts => Volatile.Read(ref _posts);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _posts);
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                RequestMessage = request,
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            };
            response.Headers.RetryAfter =
                new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
            _answered.TrySetResult();
            return Task.FromResult(response);
        }
    }

    /// <summary>
    /// Between attempts there is nothing on the wire to protect, and a backoff the caller has
    /// stopped honoring is time spent holding the run open for a request it will not send. The
    /// wait ends with the stop, and what the server last said is what the request reports - a
    /// stopped retry is not a request that got no answer.
    /// </summary>
    [Fact]
    public async Task A_stop_between_attempts_ends_the_wait_and_keeps_the_last_status()
    {
        ResiliencePipelineFactory.Reset();
        var wire = new ThrottlesForAMinuteHandler();
        using var httpClient = new HttpClient(wire);
        using var client = new ResilientGraphClient(httpClient, Options);
        using var stop = new CancellationTokenSource();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var call = client.PostAsync("https://graph.microsoft.com/v1.0/$batch",
            new StringContent("{}"), CancellationToken.None,
            paceGate: false, stopRetries: stop.Token);

        await wire.Answered.WaitAsync(TimeSpan.FromSeconds(30));
        // Long enough that the retry decision is behind us and the backoff is running: the
        // question is whether a wait already under way ends, not whether one is refused.
        await Task.Delay(500);
        stop.Cancel();

        var failure = await Assert.ThrowsAsync<HttpRequestException>(
            () => call.WaitAsync(TimeSpan.FromSeconds(30)));
        sw.Stop();

        Assert.Equal(ResilientGraphClient.RetriesStoppedMessage, failure.Message);
        // The 429 is what the server last said. A stopped retry that reported no status at all
        // would have the caller's items clamped to a generic failure instead.
        Assert.Equal(HttpStatusCode.TooManyRequests, failure.StatusCode);
        Assert.Equal(1, wire.Posts);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30),
            $"the call waited out {sw.Elapsed.TotalSeconds:F1}s of a Retry-After it had stopped honoring");
    }

    /// <summary>
    /// Throttles the first POST with a Retry-After of zero so the retry follows at once, and
    /// answers anything after it. Counts every request it is handed without consulting the
    /// token: a transport has the request before a cancel reaches it, so a send counted here
    /// went out whatever the token said a moment later.
    /// </summary>
    private sealed class CountsEverySendHandler : HttpMessageHandler
    {
        private int _sends;

        public int Sends => Volatile.Read(ref _sends);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var n = Interlocked.Increment(ref _sends);
            var response = new HttpResponseMessage(
                n == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            };
            if (n == 1)
                response.Headers.RetryAfter =
                    new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return Task.FromResult(response);
        }
    }

    /// <summary>
    /// The stop arrives after the next attempt has been decided on but before it claims the
    /// request - the stretch in which the attempt is being built. Nothing is on the wire to
    /// protect there, so the attempt ends where it stands. Reading the stop and sending anyway
    /// would leave the caller told that no further attempt was sent over one the server has:
    /// the stop sets the state and cancels the link as two steps, and a request handed to the
    /// transport in between is already gone when the token lands.
    /// </summary>
    [Fact]
    public async Task A_stop_that_lands_before_the_next_attempt_claims_it_keeps_it_off_the_wire()
    {
        ResiliencePipelineFactory.Reset();
        var wire = new CountsEverySendHandler();
        using var httpClient = new HttpClient(wire);
        using var client = new ResilientGraphClient(httpClient, Options);
        using var stop = new CancellationTokenSource();

        var reachedSecondAttempt =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.AttemptEntryGate = async attempt =>
        {
            if (attempt < 2) return;
            reachedSecondAttempt.TrySetResult();
            await release.Task;
        };

        var call = client.PostAsync("https://graph.microsoft.com/v1.0/$batch",
            new StringContent("{}"), CancellationToken.None,
            paceGate: false, stopRetries: stop.Token);

        await reachedSecondAttempt.Task.WaitAsync(TimeSpan.FromSeconds(30));
        // Cancel runs the registration on this thread, so both halves of the stop - the state
        // it takes and the link it cancels - are behind us before the attempt is let go. The
        // window is entered on purpose rather than waited for.
        stop.Cancel();
        release.SetResult();

        var outcome = await Record.ExceptionAsync(async () =>
        {
            using var response = await call.WaitAsync(TimeSpan.FromSeconds(30));
        });

        // The attempt the stop overtook never reached the transport.
        Assert.Equal(1, wire.Sends);
        var failure = Assert.IsType<HttpRequestException>(outcome);
        // ...so the sentence the caller is given about it is true.
        Assert.Equal(ResilientGraphClient.RetriesStoppedMessage, failure.Message);
        Assert.Equal(HttpStatusCode.TooManyRequests, failure.StatusCode);
    }
}
