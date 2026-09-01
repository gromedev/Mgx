using System.Net;
using Mgx.Engine.Http;
using Mgx.Engine.Models;

namespace Mgx.IntegrationTests;

/// <summary>
/// The partial-result contract of a chunked batch does not depend on how many chunks are in
/// flight at once. With -BatchChunkConcurrency above 1 the chunks run in parallel, and a chunk
/// whose own POST fails used to take the whole run down with it: Task.WhenAll rethrew and the
/// results of the chunks that had already been applied on the server were discarded with the
/// array holding them.
/// </summary>
[Collection("Pipeline")]
public class ParallelChunkPartialResultTests
{
    private const string Rejection =
        "{\"error\":{\"code\":\"BadRequest\",\"message\":\"batch rejected\"}}";

    private static string BatchResponse(int count, int status) =>
        "{ \"responses\": [" + string.Join(",", Enumerable.Range(1, count).Select(i =>
            $"{{ \"id\": \"{i}\", \"status\": {status} }}")) + "] }";

    /// <summary>
    /// Rejects one chunk by its CONTENT, not by arrival order, so sequential and parallel runs
    /// see the same wire behavior.
    /// <para>
    /// Nothing is answered until the whole opening wave is on the wire, so no chunk of it is
    /// stopped before it has sent: left to the scheduler the refusal can be recorded while a
    /// chunk that holds a permit has not reached the handler yet, and that chunk then turns
    /// back correctly at a wire count the test reads as the thing it exists to catch.
    /// </para>
    /// <para>
    /// The rejected chunk is then answered first, so the permit it holds is released only after
    /// the run has recorded the refusal. The rest of the wave is held until the run says it has
    /// stopped sending, so that permit is the only one a queued chunk can be admitted on and
    /// the refusal is recorded before the last chunk asks whether to send. A POST arriving from
    /// outside the wave releases them as well: that is the thing the guard exists to prevent
    /// and the thing the counts have to be able to see, so a run that sends one is counted
    /// rather than held here behind it.
    /// </para>
    /// </summary>
    private sealed class RejectOneChunkHandler : HttpMessageHandler
    {
        private readonly string _marker;
        private readonly int _wave;
        private readonly TaskCompletionSource _inFlight =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _settled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _posts;
        private int _arrived;
        private int _landing;
        public int Posts => Volatile.Read(ref _posts);

        /// <param name="marker">Body text carried by the one chunk to reject.</param>
        /// <param name="wave">Chunks other than that one which hold a permit from the start.
        /// Zero for a sequential run, which holds one chunk at a time and needs no gate.</param>
        public RejectOneChunkHandler(string marker, int wave)
        {
            _marker = marker;
            _wave = wave;
            if (wave == 0) _settled.TrySetResult();
        }

        /// <summary>Armed on the run: the refusal is recorded and nothing further may send.</summary>
        public void RunStopped() => _settled.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _posts);
            if (Interlocked.Increment(ref _arrived) >= _wave + 1) _inFlight.TrySetResult();
            await Task.Yield();
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            await _inFlight.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            if (body.Contains(_marker, StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    RequestMessage = request,
                    Content = new StringContent(Rejection, System.Text.Encoding.UTF8, "application/json")
                };
            }

            if (Interlocked.Increment(ref _landing) > _wave) _settled.TrySetResult();
            await _settled.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(BatchResponse(20, 204),
                    System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    /// <summary>
    /// Four chunks, so at concurrency 3 one of them has to wait for a permit and the guard that
    /// decides whether a waiting chunk still goes out is actually consulted. At 60 operations
    /// every chunk held a permit from the start and the guard was never reached.
    /// </summary>
    private static async Task<(BatchExecutionResult Result, int Posts)> Run(int concurrency, int wave)
    {
        ResiliencePipelineFactory.Reset();
        // "u21" is the first operation of the second chunk and appears in no other chunk.
        var wire = new RejectOneChunkHandler("/users/u21\"", wave);
        using var httpClient = new HttpClient(wire);
        using var client = new ResilientGraphClient(httpClient,
            new ResilientGraphClientOptions { NoRateLimit = true, MaxRetryAttempts = 1 });
        var batchClient = new GraphBatchClient(client, batchChunkConcurrency: concurrency)
        {
            SendingStopped = wire.RunStopped
        };

        var ops = Enumerable.Range(1, 80)
            .Select(i => new BatchOperation($"/users/u{i}", "PATCH", null))
            .ToList();

        var result = await batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken.None);
        return (result, wire.Posts);
    }

    // Sequentially the run stops at the refusal: two POSTs, two chunks never sent. In parallel
    // three chunks are on the wire and the fourth is still queued when the refusal is recorded,
    // so it never goes out either - one chunk refused, one stopped, two applied.
    [Theory]
    [InlineData(1, 0, 2, 20, 40)]
    [InlineData(3, 2, 3, 40, 20)]
    public async Task A_failed_chunk_keeps_the_results_of_the_chunks_that_landed(
        int concurrency, int wave, int expectedPosts, int expectedLanded, int expectedNotSent)
    {
        var (result, posts) = await Run(concurrency, wave);

        Assert.NotNull(result.ChunkFailure);
        Assert.IsType<GraphServiceException>(result.ChunkFailure);

        // Every operation is accounted for by position, so a caller can line the results up
        // against its own input list.
        Assert.Equal(80, result.Results.Count);
        Assert.Equal(expectedPosts, posts);

        var landed = result.Results.Count(r => r.Response.Status == 204);
        var refused = result.Results.Count(r => r.Response.Status == 400);
        var notSent = result.Results.Count(r => r.Response.Status == GraphBatchClient.NotSentStatus);

        // Exact counts: an inequality here passes just as well when a chunk that should have
        // been stopped went out anyway, which is the thing worth knowing.
        Assert.Equal(expectedLanded, landed);
        Assert.Equal(20, refused);
        Assert.Equal(expectedNotSent, notSent);
        Assert.Equal(notSent, result.NotSent.Count);
    }

    /// <summary>
    /// Answers nothing until as many chunks are on the wire as the run can hold at once. The
    /// two failures then reach the run at the same time rather than one after the other, and a
    /// chunk that was going to send is not stopped before it has - left to the scheduler a
    /// chunk can still be waiting to start when the refusals land, and it is then correctly
    /// reported as never sent, which is a different outcome from the one asked about here.
    /// </summary>
    /// <param name="inFlight">Chunks the run holds at once, so all of them and no more.</param>
    private sealed class TwoChunksRefusedTogetherHandler(int inFlight) : HttpMessageHandler
    {
        private int _arrived;
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Yield();
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var refusal = body.Contains("/users/u1\"", StringComparison.Ordinal) ? "chunk A"
                : body.Contains("/users/u21\"", StringComparison.Ordinal) ? "chunk B"
                : null;
            if (Interlocked.Increment(ref _arrived) == inFlight) _all.TrySetResult();
            await _all.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            if (refusal == null)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(BatchResponse(20, 204),
                        System.Text.Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                RequestMessage = request,
                Content = new StringContent(
                    $"{{\"error\":{{\"code\":\"BadRequest\",\"message\":\"{refusal} rejected\"}}}}",
                    System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    [Fact]
    public async Task A_second_chunk_failing_at_the_same_time_is_still_reported()
    {
        ResiliencePipelineFactory.Reset();
        using var httpClient = new HttpClient(new TwoChunksRefusedTogetherHandler(inFlight: 3));
        using var client = new ResilientGraphClient(httpClient,
            new ResilientGraphClientOptions { NoRateLimit = true, MaxRetryAttempts = 1 });
        var messages = new List<string>();
        var batchClient = new GraphBatchClient(client, batchChunkConcurrency: 3)
        {
            VerboseWriter = messages.Add
        };

        var ops = Enumerable.Range(1, 60)
            .Select(i => new BatchOperation($"/users/u{i}", "PATCH", null))
            .ToList();

        var result = await batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken.None);
        batchClient.DrainVerboseMessages();

        Assert.NotNull(result.ChunkFailure);
        // One of the two is the failure the caller is handed. The other lost a race it had no
        // stake in, and used to disappear with no record that it had happened at all.
        var reported = result.ChunkFailure!.Message.Contains("chunk A", StringComparison.Ordinal)
            ? "chunk A" : "chunk B";
        var lost = reported == "chunk A" ? "chunk B" : "chunk A";
        Assert.Contains(messages, m => m.Contains(lost, StringComparison.Ordinal));

        // Both refused chunks report their own items as attempted, not as never sent.
        for (int i = 0; i < 40; i++)
            Assert.Equal(400, result.Results[i].Response.Status);
        for (int i = 40; i < 60; i++)
            Assert.Equal(204, result.Results[i].Response.Status);
    }

    /// <summary>
    /// With chunks in flight together there is no "earlier" chunk to blame: the two that were
    /// refused ran alongside each other, and the one that never went out was behind both.
    /// </summary>
    [Fact]
    public void An_operation_that_was_never_sent_is_not_blamed_on_an_earlier_chunk()
    {
        using (MgxTransportScope.Inject(new TwoChunksRefusedTogetherHandler(inFlight: 2),
            options: new ResilientGraphClientOptions
            {
                NoRateLimit = true,
                MaxRetryAttempts = 1,
                BatchChunkConcurrency = 2
            }))
        {
            using var ps = System.Management.Automation.PowerShell.Create();
            ps.AddCommand("Import-Module")
              .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Batch.InvokeMgxBatchRequest).Assembly);
            ps.Invoke();
            ps.Commands.Clear();
            ps.AddScript("function Get-MgContext { [PSCustomObject]@{ TenantId = 'test-tenant-00000000-0000-0000-0000-000000000000' } }");
            ps.Invoke();
            ps.Commands.Clear();

            ps.AddCommand("Invoke-MgxBatchRequest")
              .AddParameter("Uri", Enumerable.Range(1, 60).Select(i => $"/users/u{i}").ToArray())
              .AddParameter("Method", "PATCH");
            ps.Invoke();

            // Two chunks hold both permits until both are refused, so the third can only reach
            // the semaphore after a refusal has been recorded: it never goes out.
            var notSent = ps.Streams.Error
                .Where(e => e.FullyQualifiedErrorId.StartsWith("BatchItemNotSent", StringComparison.Ordinal))
                .ToList();
            Assert.Equal(20, notSent.Count);
            Assert.All(notSent, e => Assert.DoesNotContain("earlier", e.Exception.Message,
                StringComparison.OrdinalIgnoreCase));
            Assert.All(notSent, e => Assert.Contains("was not sent", e.Exception.Message,
                StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// One chunk's call fails with no HTTP response at all - the shape a stalled $batch body,
    /// an open circuit or a dead connection arrives in.
    /// <para>
    /// Nothing is answered until as many chunks are on the wire as the run can carry at once, so
    /// the one that fails does so with the others already sent. Left to the scheduler a chunk can
    /// still be waiting to start when the failure lands - it is then correctly stopped, which is
    /// a different outcome, asked about by a different test.
    /// </para>
    /// </summary>
    private sealed class OneChunkThrowsHandler(int inFlight) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;
        private int _posts;
        public int Posts => Volatile.Read(ref _posts);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _posts);
            if (Interlocked.Increment(ref _arrived) == inFlight) _all.TrySetResult();
            await Task.Yield();
            await _all.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (body.Contains("/users/u21\"", StringComparison.Ordinal))
                throw new HttpRequestException("connection reset");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(BatchResponse(20, 204),
                    System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    /// <summary>
    /// A chunk can fail without a status to fail with. Every one of those shapes used to travel
    /// out of the task and take the results array with it, so the chunks the server had already
    /// applied came back as nothing.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task A_chunk_that_fails_without_a_response_keeps_the_other_chunks(int concurrency)
    {
        ResiliencePipelineFactory.Reset();
        // Sixty operations are three chunks, so at concurrency 3 the run can hold all three at
        // once and the handler answers none of them until it has all three. The failure is then
        // reached with nothing left to stop, which is the case this asserts on.
        var wire = new OneChunkThrowsHandler(Math.Min(concurrency, 3));
        using var httpClient = new HttpClient(wire);
        using var client = new ResilientGraphClient(httpClient,
            new ResilientGraphClientOptions { NoRateLimit = true, MaxRetryAttempts = 1 });
        var batchClient = new GraphBatchClient(client, batchChunkConcurrency: concurrency);

        var ops = Enumerable.Range(1, 60)
            .Select(i => new BatchOperation($"/users/u{i}", "PATCH", null))
            .ToList();

        var result = await batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken.None);

        Assert.IsType<HttpRequestException>(result.ChunkFailure);
        Assert.Equal(60, result.Results.Count);

        // The first chunk was applied on the server whichever way the second one died.
        for (int i = 0; i < 20; i++)
            Assert.Equal(204, result.Results[i].Response.Status);

        // The failed chunk's own items went out and may have been applied. Without a status of
        // their own they read as attempted-and-unknown, never as unattempted.
        for (int i = 20; i < 40; i++)
            Assert.Equal(503, result.Results[i].Response.Status);

        // Sequentially the third chunk never goes out; in parallel it was already in flight.
        Assert.Equal(concurrency == 1 ? 20 : 0, result.NotSent.Count);
    }

    /// <summary>
    /// Three chunks, all in flight at once. One is refused; one is throttled and would retry a
    /// second later; one is applied. The throttled chunk's retry is the only POST that could
    /// still go out after the refusal is recorded, so the wire count says whether anything did.
    /// <para>
    /// The order the question needs is gated, not raced. Nothing is answered until all three
    /// chunks are on the wire, so none of them is stopped before it has sent and the count of
    /// three means three chunks that really went out. The refusal then goes back first, and the
    /// throttle only once the run has recorded it - so the throttled chunk meets the stop at
    /// its next attempt, instead of a one-second Retry-After having to outlast the refusal on a
    /// loaded machine to reach the same place.
    /// </para>
    /// </summary>
    private sealed class RefuseOneThrottleOneHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _allInFlight =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stopped =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;
        private int _posts;
        public int Posts => Volatile.Read(ref _posts);

        /// <summary>Armed on the run: the refusal is recorded and nothing further may send.</summary>
        public void RunStopped() => _stopped.TrySetResult();

        private static string Throttled(int count) =>
            "{ \"responses\": [" + string.Join(",", Enumerable.Range(1, count).Select(i =>
                $"{{ \"id\": \"{i}\", \"status\": 429, \"headers\": {{ \"Retry-After\": \"1\" }} }}")) + "] }";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _posts);
            if (Interlocked.Increment(ref _arrived) == 3) _allInFlight.TrySetResult();
            await Task.Yield();
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            // Answered only once the caller has started the others, so all three are genuinely
            // in flight - a handler that answers inline serializes them and the guard never has
            // two chunks to arbitrate.
            await _allInFlight.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

            if (body.Contains("/users/u21\"", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    RequestMessage = request,
                    Content = new StringContent(Rejection, System.Text.Encoding.UTF8, "application/json")
                };
            }
            if (body.Contains("/users/u1\"", StringComparison.Ordinal))
            {
                await _stopped.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(Throttled(20), System.Text.Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(BatchResponse(20, 204),
                    System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    [Fact]
    public async Task Nothing_further_is_posted_once_a_chunk_has_been_refused()
    {
        ResiliencePipelineFactory.Reset();
        var wire = new RefuseOneThrottleOneHandler();
        using var httpClient = new HttpClient(wire);
        using var client = new ResilientGraphClient(httpClient,
            new ResilientGraphClientOptions { NoRateLimit = true, MaxRetryAttempts = 1 });
        var batchClient = new GraphBatchClient(client, batchChunkConcurrency: 3)
        {
            SendingStopped = wire.RunStopped
        };

        var ops = Enumerable.Range(1, 60)
            .Select(i => new BatchOperation($"/users/u{i}", "PATCH", null))
            .ToList();

        var result = await batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken.None);

        Assert.NotNull(result.ChunkFailure);
        // One POST per chunk and not one more: no per-chunk retry after the refusal, and no
        // follow-up batch either. Each of those would be a write the caller is told nothing about.
        Assert.Equal(3, wire.Posts);
        Assert.Equal(0, result.Telemetry.BatchLevelRetries);

        // The throttled chunk stops where it is, holding what the server last said about it.
        for (int i = 0; i < 20; i++)
            Assert.Equal(429, result.Results[i].Response.Status);
        for (int i = 20; i < 40; i++)
            Assert.Equal(400, result.Results[i].Response.Status);
        for (int i = 40; i < 60; i++)
            Assert.Equal(204, result.Results[i].Response.Status);
        Assert.Empty(result.NotSent);
    }

    /// <summary>
    /// Throttles one chunk for a minute, and refuses another only once the throttled chunk is
    /// into the wait between its attempts - the run says when, rather than the refusal being
    /// held back long enough that it usually is. The refusal then arrives with the backoff
    /// running, which is the only state the question of whether the wait ends with it can be
    /// asked from. Nothing is answered until all three chunks are on the wire, so none of them
    /// is stopped before it has sent.
    /// </summary>
    private sealed class RefusesDuringARetryWaitHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _inFlight =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _waiting =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;
        private int _posts;
        public int Posts => Volatile.Read(ref _posts);

        /// <summary>Armed on the run: the throttled chunk is in the wait between two attempts.</summary>
        public void RetryWaitStarted() => _waiting.TrySetResult();

        private static string Throttled(int count) =>
            "{ \"responses\": [" + string.Join(",", Enumerable.Range(1, count).Select(i =>
                $"{{ \"id\": \"{i}\", \"status\": 429, \"headers\": {{ \"Retry-After\": \"60\" }} }}")) + "] }";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _posts);
            if (Interlocked.Increment(ref _arrived) == 3) _inFlight.TrySetResult();
            await Task.Yield();
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            await _inFlight.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            if (body.Contains("/users/u1\"", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(Throttled(20),
                        System.Text.Encoding.UTF8, "application/json")
                };
            }
            if (body.Contains("/users/u21\"", StringComparison.Ordinal))
            {
                await _waiting.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    RequestMessage = request,
                    Content = new StringContent(Rejection, System.Text.Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(BatchResponse(20, 204),
                    System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    /// <summary>
    /// The run stops sending the moment a chunk is refused, and a chunk already waiting out a
    /// Retry-After has nothing left to wait for. Consulted only before the backoff started, the
    /// stop left the call blocked for the rest of the delay - here a minute, and up to the
    /// two-minute cap plus jitter against a server that asks for longer.
    /// </summary>
    [Fact]
    public async Task A_refusal_ends_a_retry_wait_that_is_already_running()
    {
        ResiliencePipelineFactory.Reset();
        var wire = new RefusesDuringARetryWaitHandler();
        using var httpClient = new HttpClient(wire);
        using var client = new ResilientGraphClient(httpClient,
            new ResilientGraphClientOptions { NoRateLimit = true, MaxRetryAttempts = 1 });
        var batchClient = new GraphBatchClient(client, batchChunkConcurrency: 3)
        {
            RetryWaitStarted = wire.RetryWaitStarted
        };

        var ops = Enumerable.Range(1, 60)
            .Select(i => new BatchOperation($"/users/u{i}", "PATCH", null))
            .ToList();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken.None);
        sw.Stop();

        Assert.NotNull(result.ChunkFailure);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15),
            $"the call waited out {sw.Elapsed.TotalSeconds:F1}s of a Retry-After it had stopped honoring");
        // Nothing more went out: the wait ended because the run stopped, not because it expired.
        Assert.Equal(3, wire.Posts);
        for (int i = 0; i < 20; i++)
            Assert.Equal(429, result.Results[i].Response.Status);
        for (int i = 20; i < 40; i++)
            Assert.Equal(400, result.Results[i].Response.Status);
        for (int i = 40; i < 60; i++)
            Assert.Equal(204, result.Results[i].Response.Status);
    }

    /// <summary>
    /// The chunk that was rejected went out on the wire, so its operations carry the refusal.
    /// Reporting them as never sent would send a caller back to re-submit writes that may
    /// already have been applied.
    /// </summary>
    [Fact]
    public async Task The_rejected_chunks_operations_carry_the_refusal_not_a_not_sent_status()
    {
        var (result, _) = await Run(3, wave: 2);

        Assert.DoesNotContain(result.NotSent, op => op.Url == "/users/u21");
        for (int i = 20; i < 40; i++)
            Assert.Equal(400, result.Results[i].Response.Status);
        for (int i = 0; i < 20; i++)
            Assert.Equal(204, result.Results[i].Response.Status);
    }

    /// <summary>
    /// The verbose summary is the run's own account of what it did. A retry counted when it was
    /// decided on rather than when it went out is a request that never left: the refusal ends
    /// the backoff, the chunk breaks at its next attempt, and the run reports twenty item
    /// retries over a wire that carried one POST per chunk.
    /// </summary>
    [Fact]
    public async Task Retries_a_refusal_stopped_are_not_counted_as_retries_that_went_out()
    {
        ResiliencePipelineFactory.Reset();
        var wire = new RefusesDuringARetryWaitHandler();
        using var httpClient = new HttpClient(wire);
        using var client = new ResilientGraphClient(httpClient,
            new ResilientGraphClientOptions { NoRateLimit = true, MaxRetryAttempts = 1 });
        var batchClient = new GraphBatchClient(client, batchChunkConcurrency: 3)
        {
            RetryWaitStarted = wire.RetryWaitStarted
        };

        var ops = Enumerable.Range(1, 60)
            .Select(i => new BatchOperation($"/users/u{i}", "PATCH", null))
            .ToList();

        var result = await batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken.None);

        Assert.NotNull(result.ChunkFailure);
        // One POST per chunk and no fourth: the twenty the throttled chunk was going to resend
        // never went out.
        Assert.Equal(3, wire.Posts);
        Assert.Equal(0, result.Telemetry.ItemRetries);
        // The throttles and the wait are not in question - those twenty 429s arrived, and the
        // chunk did spend time on the backoff before the refusal ended it.
        Assert.Equal(20, result.Telemetry.ThrottleEncounters);
    }

    /// <summary>
    /// Throttles one chunk's ENVELOPE - a 429 on the $batch POST itself, with a Retry-After the
    /// transport honors - and refuses a second chunk once that throttle has landed, while a
    /// third is still on the wire. The throttled chunk is inside the transport's own retry, a
    /// layer below the loop that reads the stop, and the wire count says whether its writes
    /// went out again after the run had stopped sending.
    /// <para>
    /// The third chunk is answered only once the run has recorded the refusal, so it is on the
    /// wire at that moment by the run's own account of when the moment was. The caller holds
    /// the throttled chunk's second attempt at the head of the attempt until the same point,
    /// so that attempt meets the stop rather than racing a five-second Retry-After against the
    /// round trip that carries the refusal. Nothing at all is answered until all three chunks
    /// are on the wire, so none of them is stopped before it has sent.
    /// </para>
    /// </summary>
    private sealed class ThrottlesTheEnvelopeThenRefusesHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _inFlight =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _throttled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stopped =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;
        private int _posts;
        private int _throttledChunkPosts;
        public int Posts => Volatile.Read(ref _posts);
        public int ThrottledChunkPosts => Volatile.Read(ref _throttledChunkPosts);

        /// <summary>Armed on the run: the refusal is recorded and nothing further may send.</summary>
        public void RunStopped() => _stopped.TrySetResult();

        /// <summary>Completes with that same point, for a caller ordering an attempt against it.</summary>
        public Task Stopped => _stopped.Task;

        private static HttpResponseMessage Answered(HttpRequestMessage request) =>
            new(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(BatchResponse(20, 204),
                    System.Text.Encoding.UTF8, "application/json")
            };

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _posts);
            if (Interlocked.Increment(ref _arrived) == 3) _inFlight.TrySetResult();
            await Task.Yield();
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            await _inFlight.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

            if (body.Contains("/users/u1\"", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _throttledChunkPosts) == 1)
                {
                    var throttle = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                    {
                        RequestMessage = request,
                        Content = new StringContent(Rejection, System.Text.Encoding.UTF8, "application/json")
                    };
                    throttle.Headers.RetryAfter =
                        new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
                    _throttled.TrySetResult();
                    return throttle;
                }
                // A second POST of this chunk is the run sending after it stopped. Answered
                // rather than refused, so the writes it applied show up in the result and the
                // test fails on what happened rather than on how it was reported.
                return Answered(request);
            }

            if (body.Contains("/users/u21\"", StringComparison.Ordinal))
            {
                await _throttled.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    RequestMessage = request,
                    Content = new StringContent(Rejection, System.Text.Encoding.UTF8, "application/json")
                };
            }

            // Still on the wire when the refusal above is recorded: the server is about to
            // answer all twenty of its writes, and that answer is the record of what it applied.
            await _stopped.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            return Answered(request);
        }
    }

    /// <summary>
    /// A chunk inside the transport's own throttle retry when a sibling is refused. The stop
    /// reaches the decision to send again, and not the attempt on the wire: nothing further
    /// goes out, a chunk that was mid-request keeps the answers the server gave it, and the
    /// call comes back rather than waiting out a Retry-After the run has stopped honoring.
    /// </summary>
    [Fact]
    public async Task A_chunk_inside_the_transports_retry_stops_with_the_run()
    {
        ResiliencePipelineFactory.Reset();
        var wire = new ThrottlesTheEnvelopeThenRefusesHandler();
        using var httpClient = new HttpClient(wire);
        using var client = new ResilientGraphClient(httpClient,
            new ResilientGraphClientOptions { NoRateLimit = true, MaxRetryAttempts = 1 });
        // The second attempt of the throttled chunk is the POST in question. Held at the head of
        // the attempt until the refusal has been recorded, so whether it goes out is decided by
        // the stop and not by which of the two finished first.
        client.AttemptEntryGate = async attempt =>
        {
            if (attempt < 2) return;
            await wire.Stopped.WaitAsync(TimeSpan.FromSeconds(30));
        };
        var batchClient = new GraphBatchClient(client, batchChunkConcurrency: 3)
        {
            SendingStopped = wire.RunStopped
        };

        var ops = Enumerable.Range(1, 60)
            .Select(i => new BatchOperation($"/users/u{i}", "PATCH", null))
            .ToList();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken.None);
        sw.Stop();

        Assert.NotNull(result.ChunkFailure);

        // Nothing further went out. The throttled chunk's second POST is the only one that
        // could, and it carries twenty writes the caller would be told nothing about.
        Assert.Equal(1, wire.ThrottledChunkPosts);
        Assert.Equal(3, wire.Posts);

        // The chunk that was on the wire keeps what the server said about each of its items.
        // Ending its request to stop the run would trade twenty known outcomes for twenty
        // writes that may or may not have been applied.
        for (int i = 40; i < 60; i++)
            Assert.Equal(204, result.Results[i].Response.Status);

        // The throttled chunk carries the status its envelope was refused with, the same one it
        // would carry had the transport run out of retries instead of being stopped.
        for (int i = 0; i < 20; i++)
            Assert.Equal(429, result.Results[i].Response.Status);
        for (int i = 20; i < 40; i++)
            Assert.Equal(400, result.Results[i].Response.Status);
        Assert.Empty(result.NotSent);

        // The Retry-After was five seconds. Waiting it out holds the call open for a delay the
        // run has already decided not to act on.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3),
            $"the call waited out {sw.Elapsed.TotalSeconds:F1}s of a Retry-After it had stopped honoring");
    }

    /// <summary>
    /// Refuses whichever POST holds the one permit that was free, and answers every later one.
    /// The refusal waits for the other chunks to be queued at the limiter - counted there, not
    /// allowed time to get there - so a later POST is a chunk that waited out its permit and
    /// sent anyway, not one that reached the queue after the refusal had already been recorded.
    /// </summary>
    private sealed class RefusesTheFirstPostHandler(
        System.Threading.RateLimiting.TokenBucketRateLimiter limiter, int queued) : HttpMessageHandler
    {
        private int _posts;
        public int Posts => Volatile.Read(ref _posts);

        private int Queued => (int)limiter.GetStatistics()!.CurrentQueuedCount;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _posts) == 1)
            {
                var waited = System.Diagnostics.Stopwatch.StartNew();
                while (Queued < queued)
                {
                    if (waited.Elapsed > TimeSpan.FromSeconds(30))
                        throw new TimeoutException(
                            $"only {Queued} of {queued} chunks reached the permit queue");
                    await Task.Delay(5, cancellationToken);
                }
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    RequestMessage = request,
                    Content = new StringContent(Rejection, System.Text.Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(BatchResponse(20, 204),
                    System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    /// <summary>
    /// A rate limiter of one permit a second, which the tuning help recommends verbatim for
    /// OneNote and Planner, and chunk parallelism above one. Two chunks are parked waiting for a
    /// permit when the third is refused; the stop they were given has to reach the wait they are
    /// in, or each POSTs its twenty writes as its permit comes up - seconds after the run
    /// recorded that it had stopped sending, and with nothing that reports on them.
    /// </summary>
    [Fact]
    public async Task Chunks_waiting_for_a_rate_limit_permit_do_not_post_after_a_refusal()
    {
        ResiliencePipelineFactory.Reset();
        // The limiter the run's requests queue at, held so the handler can count who is in that
        // queue rather than guess how long they take to reach it.
        var (pipeline, limiter) = ResiliencePipelineFactory.GetOrCreate(new ResilientGraphClientOptions
        {
            RateLimitBurst = 1,
            RateLimitPerSecond = 1,
            RateLimitQueueLimit = 100,
            MaxRetryAttempts = 1
        });
        var wire = new RefusesTheFirstPostHandler(limiter!, queued: 2);
        using var httpClient = new HttpClient(wire);
        using var client = new ResilientGraphClient(httpClient, pipeline, limiter);
        var batchClient = new GraphBatchClient(client, batchChunkConcurrency: 3);

        var ops = Enumerable.Range(1, 60)
            .Select(i => new BatchOperation($"/users/u{i}", "PATCH", null))
            .ToList();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken.None);
        sw.Stop();

        Assert.NotNull(result.ChunkFailure);
        // One permit was free at the start and the chunk that took it was refused. The next two
        // permits fall due a second and two seconds later; neither may carry a POST.
        Assert.Equal(1, wire.Posts);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3),
            $"the run held on for {sw.Elapsed.TotalSeconds:F1}s of permits it had stopped needing");

        // Forty operations never reached the server, and that is what they are reported as. A
        // status here instead would say the writes were attempted and may have been applied,
        // which is the one thing that would stop a caller resubmitting them.
        Assert.Equal(40, result.NotSent.Count);
        var refused = result.Results.Count(r => r.Response.Status == 400);
        var neverSent = result.Results.Count(r => r.Response.Status == GraphBatchClient.NotSentStatus);
        Assert.Equal(20, refused);
        Assert.Equal(40, neverSent);
        Assert.DoesNotContain(result.Results, r => r.Response.Status == 503);

        // Retries counted in front of a POST that never went out are requests the summary
        // claims left when nothing did.
        Assert.Equal(0, result.Telemetry.ItemRetries);
    }
}
