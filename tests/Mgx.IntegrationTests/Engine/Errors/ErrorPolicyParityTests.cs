using System.Net.Sockets;
using Mgx.Engine.Errors;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Mgx.IntegrationTests;

/// <summary>
/// Pins classifier + policy to the decisions the retry predicate, the circuit-breaker
/// predicate, the batch item loop, and the download pipeline each made on their own
/// before consolidation, across every status. The oracles below are transcriptions of
/// v2.1.2's inline code. Scope, stated honestly: the status tables are held exactly;
/// the batch theory drives the real IsRetryable; the pipeline predicates themselves are
/// lambdas exercised by the behavior suites (RetryTests, CircuitBreakerTests). Three
/// deliberate widenings in the exception dimension are pinned as INTENDED below:
/// SocketException, IOException (reachable as HttpIOException), and a bare
/// OperationCanceledException now classify as transport failures and retry/count where
/// v2.1.2 fell through to no-retry.
/// </summary>
public class ErrorPolicyParityTests
{
    // ResiliencePipelineFactory.cs retry predicate, as it stood.
    private static bool OracleRetry(int status, bool isIdempotent)
    {
        if (status == 429) return true;
        if (!isIdempotent) return false;
        return status is 500 or 502 or 503 or 504 or 408;
    }

    // ResiliencePipelineFactory.cs circuit-breaker predicate, as it stood.
    private static bool OracleCircuit(int status) => status is 500 or 502 or 503 or 504;

    // GraphBatchClient.IsRetryable, as it stood.
    private static bool OracleBatch(int status, string method)
    {
        if (status == 429) return true;
        if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)) return false;
        return status is 408 or 500 or 502 or 503 or 504;
    }

    // GraphContentClient download pipeline ShouldHandle, as it stood.
    private static bool OracleDownload(int status) => status is 429 or 500 or 502 or 503 or 504;

    public static TheoryData<int> AllStatuses()
    {
        var d = new TheoryData<int>();
        for (var s = 100; s < 600; s++) d.Add(s);
        return d;
    }

    [Theory]
    [MemberData(nameof(AllStatuses))]
    public void Retry_decision_matches_the_old_predicate_for_every_status(int status)
    {
        var cls = MgxErrorClassifier.Classify(status).Class;
        Assert.Equal(OracleRetry(status, isIdempotent: true), MgxErrorPolicy.ShouldRetry(cls, isIdempotent: true));
        Assert.Equal(OracleRetry(status, isIdempotent: false), MgxErrorPolicy.ShouldRetry(cls, isIdempotent: false));
    }

    [Theory]
    [MemberData(nameof(AllStatuses))]
    public void Circuit_decision_matches_the_old_predicate_for_every_status(int status)
    {
        var info = MgxErrorClassifier.Classify(status);
        Assert.Equal(OracleCircuit(status), MgxErrorPolicy.CountsAsCircuitFailure(info));
    }

    private static readonly System.Reflection.MethodInfo s_realBatchIsRetryable =
        typeof(Mgx.Engine.Http.GraphBatchClient).GetMethod("IsRetryable",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

    [Theory]
    [MemberData(nameof(AllStatuses))]
    public void Batch_decision_matches_the_old_predicate_for_every_status(int status)
    {
        // The REAL private predicate, not a re-implementation of its glue - so a drift in
        // GraphBatchClient.IsRetryable itself (say, its case-insensitive POST check) fails
        // here rather than staying green behind a faithful copy.
        foreach (var method in new[] { "GET", "POST", "PATCH", "PUT", "DELETE", "post" })
        {
            var actual = (bool)s_realBatchIsRetryable.Invoke(null, [status, method])!;
            Assert.Equal(OracleBatch(status, method), actual);
        }
    }

    [Theory]
    [MemberData(nameof(AllStatuses))]
    public void Download_decision_matches_the_old_predicate_for_every_status(int status)
    {
        var info = MgxErrorClassifier.Classify(status);
        Assert.Equal(OracleDownload(status), MgxErrorPolicy.ShouldRetryDownload(info, exception: null));
    }

    [Fact]
    public void Exception_decisions_match_the_old_predicates()
    {
        // Retry predicate: HttpRequestException retried (idempotent only, via the gate);
        // TaskCanceled/TimeoutRejected retried unless the caller cancelled; everything else not.
        static bool Retry(Exception ex, bool cancelled, bool idempotent)
            => MgxErrorPolicy.ShouldRetry(MgxErrorClassifier.Classify(ex, cancelled).Class, idempotent);

        Assert.True(Retry(new HttpRequestException(), cancelled: false, idempotent: true));
        Assert.False(Retry(new HttpRequestException(), cancelled: false, idempotent: false));
        Assert.True(Retry(new TaskCanceledException(), cancelled: false, idempotent: true));
        Assert.False(Retry(new TaskCanceledException(), cancelled: true, idempotent: true));
        Assert.True(Retry(new TimeoutRejectedException(), cancelled: false, idempotent: true));
        Assert.False(Retry(new TimeoutRejectedException(), cancelled: true, idempotent: true));
        Assert.False(Retry(new BrokenCircuitException(), cancelled: false, idempotent: true));
        Assert.False(Retry(new InvalidOperationException(), cancelled: false, idempotent: true));

        // Circuit predicate: transport failures count, except after cancellation.
        static bool Circuit(Exception ex, bool cancelled)
            => MgxErrorPolicy.CountsAsCircuitFailure(MgxErrorClassifier.Classify(ex, cancelled));

        Assert.True(Circuit(new HttpRequestException(), cancelled: false));
        Assert.True(Circuit(new TaskCanceledException(), cancelled: false));
        Assert.False(Circuit(new TaskCanceledException(), cancelled: true));
        Assert.True(Circuit(new TimeoutRejectedException(), cancelled: false));
        Assert.False(Circuit(new BrokenCircuitException(), cancelled: false));

        // Download pipeline: HttpRequestException only.
        static bool Download(Exception ex)
            => MgxErrorPolicy.ShouldRetryDownload(MgxErrorClassifier.Classify(ex, false), ex);

        Assert.True(Download(new HttpRequestException()));
        Assert.False(Download(new TaskCanceledException()));
        Assert.False(Download(new TimeoutRejectedException()));
        Assert.False(Download(new SocketException()));
    }

    [Fact]
    public void The_intended_widenings_are_pinned_as_intended()
    {
        // v2.1.2's inline predicates fell through to no-retry for these; classification
        // makes them transport failures, which retries (idempotent) and counts toward the
        // breaker. Deliberate - a "response ended prematurely" HttpIOException is exactly
        // as transient as the HttpRequestException that usually wraps it. If this test
        // surprises you, the change log entry to read is 2.1.3's classifier note.
        static (bool Retry, bool Circuit) Decide(Exception ex)
        {
            var info = MgxErrorClassifier.Classify(ex, cancellationRequested: false);
            return (MgxErrorPolicy.ShouldRetry(info.Class, isIdempotent: true),
                    MgxErrorPolicy.CountsAsCircuitFailure(info));
        }

        Assert.Equal((true, true), Decide(new SocketException()));
        Assert.Equal((true, true), Decide(new IOException()));
        Assert.Equal((true, true), Decide(new OperationCanceledException()));

        // And the narrowing: a per-attempt timeout after the caller cancelled no longer
        // retries (2.1.2's fix, preserved through classification).
        var cancelled = MgxErrorClassifier.Classify(new TimeoutRejectedException(), cancellationRequested: true);
        Assert.False(MgxErrorPolicy.ShouldRetry(cancelled.Class, isIdempotent: true));
        Assert.False(MgxErrorPolicy.CountsAsCircuitFailure(cancelled));
    }

    [Fact]
    public void The_breaker_reads_cancellation_from_the_exceptions_own_token()
    {
        // The CB call site derives cancellationRequested from the exception's token -
        // matching v2.1.2's Handle<TaskCanceledException>(e => !e.CancellationToken...) -
        // while the retry predicate reads the context token. Pin the distinction: a TCE
        // carrying a cancelled token must not count, one with a default token must.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var carried = new TaskCanceledException(null, null, cts.Token);
        Assert.False(MgxErrorPolicy.CountsAsCircuitFailure(
            MgxErrorClassifier.Classify(carried, carried.CancellationToken.IsCancellationRequested)));

        var bare = new TaskCanceledException();
        Assert.True(MgxErrorPolicy.CountsAsCircuitFailure(
            MgxErrorClassifier.Classify(bare, bare.CancellationToken.IsCancellationRequested)));
    }
}
