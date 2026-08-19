using Mgx.Engine.Http;

namespace Mgx.IntegrationTests.Engine.Http;

/// <summary>
/// WaitAsync exempts the Batch bucket before touching state - GraphBatchClient runs its own
/// item-level AIMD - so an adapted cap recorded for Batch is enforced by nothing. Worse, the
/// expiry check lives inside WaitAsync, so such a cap is also cleared by nothing and
/// Get-MgxTelemetry reports "batch: capped 2/50 rps (last 429 3600s ago)" forever.
///
/// Nothing pinned that: deleting the guard left the whole suite green.
/// </summary>
[Collection("Pipeline")]
public class BatchPacingStateTests
{
    [Fact]
    public void A_batch_throttle_does_not_create_a_cap_that_nothing_can_clear()
    {
        // The suite disables the pacer globally (PacerTestDefaults), so it must be re-enabled
        // here or RecordThrottle returns before touching any state and this passes vacuously.
        AdaptiveRequestPacer.DisabledForTests = false;
        AdaptiveRequestPacer.Reset();
        try
        {
            AdaptiveRequestPacer.RecordThrottle(WorkloadBucket.Batch, null);

            var state = AdaptiveRequestPacer.DescribeState();
            Assert.True(
                state == null || !state.Contains("batch: capped", StringComparison.OrdinalIgnoreCase),
                $"Batch must not carry an adapted cap - it is never gated and never expired. Got: {state}");
        }
        finally { AdaptiveRequestPacer.Reset(); AdaptiveRequestPacer.DisabledForTests = true; }
    }

    [Fact]
    public void A_directory_throttle_still_creates_one()
    {
        AdaptiveRequestPacer.DisabledForTests = false;
        AdaptiveRequestPacer.Reset();
        try
        {
            AdaptiveRequestPacer.RecordThrottle(WorkloadBucket.Directory, null);

            var state = AdaptiveRequestPacer.DescribeState();
            Assert.NotNull(state);
            Assert.Contains("directory: capped", state, StringComparison.OrdinalIgnoreCase);
        }
        finally { AdaptiveRequestPacer.Reset(); AdaptiveRequestPacer.DisabledForTests = true; }
    }
}
