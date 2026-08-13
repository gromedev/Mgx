using System.Net;
using Mgx.Engine.Http;

namespace Mgx.IntegrationTests;

[Collection("Pipeline")]
public class ConcurrencyTests
{
    [Fact]
    public async Task ConcurrentSendAsync_NoExceptions()
    {
        // Verify 50 concurrent requests through the shared pipeline complete without errors.
        // Tests thread safety of Polly pipeline, ResilienceContext pooling, and rate limiter.
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(
            httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var tasks = Enumerable.Range(0, 50)
            .Select(_ => client.GetAsync("https://graph.microsoft.com/v1.0/users/user1"))
            .ToArray();

        var responses = await Task.WhenAll(tasks);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(50, handler.RequestCount);
    }

    [Fact]
    public async Task ConcurrentSendAsync_WithPipelineReset_NoCrash()
    {
        // Simulate tenant switch: Reset() during in-flight requests.
        // client1 holds the old pipeline reference; client2 gets a fresh one.
        // Both should complete without ObjectDisposedException or NullReferenceException.
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        var options = new ResilientGraphClientOptions { NoRateLimit = true };

        // client1 captures the current pipeline
        using var client1 = new ResilientGraphClient(httpClient, options);
        var batch1 = Enumerable.Range(0, 20)
            .Select(_ => client1.GetAsync("https://graph.microsoft.com/v1.0/users/user1"))
            .ToArray();

        // Reset while batch1 might still be in-flight.
        // client1's pipeline reference is unaffected (already captured).
        ResiliencePipelineFactory.Reset();

        // client2 gets a fresh pipeline (s_pipeline was nulled by Reset)
        using var client2 = new ResilientGraphClient(httpClient, options);
        var batch2 = Enumerable.Range(0, 20)
            .Select(_ => client2.GetAsync("https://graph.microsoft.com/v1.0/users/user1"))
            .ToArray();

        var results1 = await Task.WhenAll(batch1);
        var results2 = await Task.WhenAll(batch2);

        Assert.All(results1, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.All(results2, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(40, handler.RequestCount);
    }

    [Fact]
    public async Task ConcurrentVerboseWriter_NoCrossContamination()
    {
        // Two clients share the same pipeline (same options reference).
        // Each client's verbose messages must go to its own buffer, not leak to the other.
        // This tests the fix for Finding 8 (stale VerboseWriterKey in pooled ResilienceContext).
        ResiliencePipelineFactory.Reset();
        var options = new ResilientGraphClientOptions { NoRateLimit = true };

        // Separate handlers so each client gets its own 429->200 sequence
        var handlerA = new MockHttpHandler();
        handlerA.QueueResponse((HttpStatusCode)429, null, new() { ["Retry-After"] = "0" });
        handlerA.QueueResponse(HttpStatusCode.OK, TestData.SingleUser);

        var handlerB = new MockHttpHandler();
        handlerB.QueueResponse((HttpStatusCode)429, null, new() { ["Retry-After"] = "0" });
        handlerB.QueueResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClientA = new HttpClient(handlerA);
        using var httpClientB = new HttpClient(handlerB);

        var messagesA = new List<string>();
        var messagesB = new List<string>();

        using var clientA = new ResilientGraphClient(httpClientA, options);
        clientA.VerboseWriter = msg => { lock (messagesA) { messagesA.Add(msg); } };

        using var clientB = new ResilientGraphClient(httpClientB, options);
        clientB.VerboseWriter = msg => { lock (messagesB) { messagesB.Add(msg); } };

        // Both clients make requests concurrently (sharing the same Polly pipeline)
        var taskA = clientA.GetAsync("https://graph.microsoft.com/v1.0/users/user1");
        var taskB = clientB.GetAsync("https://graph.microsoft.com/v1.0/users/user2");
        await Task.WhenAll(taskA, taskB);

        clientA.DrainVerboseMessages();
        clientB.DrainVerboseMessages();

        // Each client should have exactly 1 retry message (from its own 429)
        Assert.Single(messagesA);
        Assert.Single(messagesB);
    }

    [Fact]
    public async Task VerboseWriter_NullWriter_DiscardsSilently()
    {
        // With no VerboseWriter set, DrainVerboseMessages should discard buffered messages
        // without throwing (e.g., no NullReferenceException).
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.QueueResponse((HttpStatusCode)429, null, new() { ["Retry-After"] = "0" });
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(
            httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        // No VerboseWriter set (null)
        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");

        // DrainVerboseMessages should not throw; just discards buffered messages
        client.DrainVerboseMessages();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.RequestCount); // 1 retry + 1 success
    }

    [Fact]
    public void ConcurrentDispose_InterlockedPattern_OnlyDisposesOnce()
    {
        // H3: Simulates the StopProcessing + EndProcessing race in MgxCmdletBase.
        // MgxCmdletBase.Dispose() uses Interlocked.CompareExchange to ensure the CTS
        // and client are only disposed once, even when StopProcessing (pipeline-stopping
        // thread) and EndProcessing (pipeline thread) call Dispose() concurrently.
        //
        // This test verifies the pattern: N threads race to dispose, exactly one succeeds.
        // If the Interlocked guard is removed, the CTS would be disposed twice, causing
        // ObjectDisposedException on the second Cancel() call.
        var disposeCount = 0;
        var disposed = 0; // mirrors MgxCmdletBase._disposed
        var cts = new CancellationTokenSource();
        var barrier = new Barrier(participantCount: 10);

        var threads = Enumerable.Range(0, 10).Select(_ => new Thread(() =>
        {
            barrier.SignalAndWait(); // all threads start simultaneously
            // Mirror MgxCmdletBase.Dispose() exactly:
            if (Interlocked.CompareExchange(ref disposed, 1, 0) == 0)
            {
                cts.Cancel();
                cts.Dispose();
                Interlocked.Increment(ref disposeCount);
            }
        })).ToArray();

        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        Assert.Equal(1, disposeCount);
        Assert.Equal(1, disposed);
    }

    [Fact]
    public void ConcurrentDispose_WithoutGuard_DoubleDisposeThrows()
    {
        // H3: Proves WHY the Interlocked guard is necessary.
        // Calling Cancel() then Dispose() then Cancel() again on the same CTS
        // throws ObjectDisposedException. This is deterministic (no race needed)
        // and validates that the guard in MgxCmdletBase.Dispose() is load-bearing.
        var cts = new CancellationTokenSource();
        cts.Cancel();
        cts.Dispose();

        // Second Cancel() on a disposed CTS throws deterministically
        Assert.Throws<ObjectDisposedException>(() => cts.Cancel());
    }

    [Fact]
    public async Task ConcurrentDispose_CtsCancel_DuringInFlightRequest()
    {
        // H3: Verifies that cancelling a CTS while a request is in-flight
        // (simulating StopProcessing during ProcessRecord) propagates cancellation
        // cleanly without ObjectDisposedException or corrupted state.
        ResiliencePipelineFactory.Reset();

        var requestStarted = new TaskCompletionSource<bool>();
        var responseReady = new TaskCompletionSource<bool>();
        var handler = new BlockingMockHandler(requestStarted, responseReady);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(
            httpClient,
            new ResilientGraphClientOptions
            {
                NoRateLimit = true,
                MaxRetryAttempts = 1,
                TotalTimeoutSeconds = 10,
                AttemptTimeoutSeconds = 10
            });

        using var cts = new CancellationTokenSource();

        // Start a request that blocks
        var requestTask = client.GetAsync(
            "https://graph.microsoft.com/v1.0/users/user1",
            cancellationToken: cts.Token);

        // Wait for handler to receive the request
        await requestStarted.Task;

        // Cancel (simulates StopProcessing calling _cts.Cancel())
        cts.Cancel();

        // Unblock the handler so it can observe cancellation
        responseReady.SetResult(true);

        // Request should complete (either cancelled or OK, depending on timing).
        // The key assertion: no unhandled exception, no crash.
        try
        {
            var response = await requestTask;
            // Handler completed before cancellation propagated: valid outcome
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        catch (OperationCanceledException)
        {
            // Cancellation propagated: valid outcome
        }
    }

    [Fact]
    public async Task ConcurrentGetOrCreate_WithReset_NoExceptions()
    {
        // H4: Simulates concurrent runspaces calling GetOrCreate while another
        // runspace triggers Reset() (tenant switch). The lock(s_lock) in both
        // GetOrCreate and Reset must prevent corrupted pipeline/rate limiter state.
        ResiliencePipelineFactory.Reset();
        var options = new ResilientGraphClientOptions { NoRateLimit = true };

        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var barrier = new Barrier(participantCount: 20);

        var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(() =>
        {
            barrier.SignalAndWait();
            try
            {
                if (i % 4 == 0)
                {
                    // Every 4th thread triggers a Reset (tenant switch)
                    ResiliencePipelineFactory.Reset();
                }
                else
                {
                    // Other threads call GetOrCreate (normal cmdlet initialization)
                    var (pipeline, limiter) = ResiliencePipelineFactory.GetOrCreate(options);
                    Assert.NotNull(pipeline);
                    // limiter is null because NoRateLimit = true, which is expected
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.Empty(exceptions);
    }

    [Fact]
    public async Task ConcurrentGetOrCreate_DifferentOptions_RebuildsPipeline()
    {
        // H4: When Set-MgxOption changes options (creating a new options object),
        // concurrent runspaces calling GetOrCreate must each get a valid pipeline,
        // even if another thread triggers a rebuild via different options reference.
        ResiliencePipelineFactory.Reset();

        var optionsA = new ResilientGraphClientOptions { NoRateLimit = true };
        var optionsB = new ResilientGraphClientOptions { NoRateLimit = true, MaxRetryAttempts = 3 };

        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var barrier = new Barrier(participantCount: 20);

        var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(() =>
        {
            barrier.SignalAndWait();
            try
            {
                // Alternate between two different option objects to force pipeline rebuilds
                var opts = i % 2 == 0 ? optionsA : optionsB;
                var (pipeline, _) = ResiliencePipelineFactory.GetOrCreate(opts);
                Assert.NotNull(pipeline);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.Empty(exceptions);
    }

    [Fact]
    public async Task ConcurrentGetOrCreate_PipelineUsableDuringReset()
    {
        // H4: A pipeline obtained before Reset() must remain usable for in-flight requests.
        // This is the concurrent runspace scenario: runspace A gets a pipeline, runspace B
        // triggers Reset (tenant switch), runspace A's in-flight requests must still complete.
        ResiliencePipelineFactory.Reset();
        var options = new ResilientGraphClientOptions { NoRateLimit = true };
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);

        // Get pipeline before reset
        var (pipeline, _) = ResiliencePipelineFactory.GetOrCreate(options);

        // Create client with the captured pipeline
        using var client = new ResilientGraphClient(httpClient, pipeline, null);

        // Reset (simulates tenant switch from another runspace)
        ResiliencePipelineFactory.Reset();

        // In-flight requests through the old pipeline must still work
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => client.GetAsync("https://graph.microsoft.com/v1.0/users/user1"))
            .ToArray();

        var responses = await Task.WhenAll(tasks);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(10, handler.RequestCount);
    }

    [Fact]
    public async Task OptionsSwap_FactoryRebuildsAndCachesConcurrently()
    {
        // H4: Verifies that swapping options causes the factory to rebuild the pipeline,
        // and concurrent readers all get the same cached instance. The factory detects
        // reference changes via ReferenceEquals inside lock(s_lock) and rebuilds.
        ResiliencePipelineFactory.Reset();

        var optionsV1 = new ResilientGraphClientOptions { NoRateLimit = true, MaxRetryAttempts = 7 };
        var optionsV2 = new ResilientGraphClientOptions { NoRateLimit = true, MaxRetryAttempts = 3 };

        // Get pipeline with V1 options
        var (pipelineV1, _) = ResiliencePipelineFactory.GetOrCreate(optionsV1);

        // Swap to V2 options (simulates Set-MgxOption writing volatile field)
        var (pipelineV2, _) = ResiliencePipelineFactory.GetOrCreate(optionsV2);

        // Factory must have rebuilt: different options reference means different pipeline
        Assert.NotSame(pipelineV1, pipelineV2);

        // Concurrent readers after swap must all get V2 pipeline
        var pipelines = new System.Collections.Concurrent.ConcurrentBag<object>();
        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
        {
            var (p, _) = ResiliencePipelineFactory.GetOrCreate(optionsV2);
            pipelines.Add(p);
        })).ToArray();

        await Task.WhenAll(tasks);

        // All concurrent readers must get the same (cached) V2 pipeline
        Assert.All(pipelines, p => Assert.Same(pipelineV2, p));
    }

    /// <summary>
    /// Handler that blocks until signaled. Used to simulate in-flight requests
    /// during HttpClient disposal.
    /// </summary>
    private sealed class BlockingMockHandler(
        TaskCompletionSource<bool> started,
        TaskCompletionSource<bool> ready) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            started.SetResult(true);
            await ready.Task;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(TestData.SingleUser)
            };
        }
    }
}
