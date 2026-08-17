using System.Diagnostics;
using Mgx.Engine.Http;

namespace Mgx.IntegrationTests.Engine;

/// <summary>
/// The shared AIMD math (AdaptivePacing): rates are halved whenever Graph returns 429, and
/// the reduced rate persists across calls. Without a way back up, one throttling episode
/// slowed everything for the lifetime of the process, so recovery is the point of these
/// tests. Used by both GraphBatchClient (per-item pacing) and AdaptiveRequestPacer.
/// </summary>
public class AdaptivePacingTests
{
    [Theory]
    [InlineData(20, 10)]
    [InlineData(10, 5)]
    [InlineData(5, 2)]
    public void ReduceRate_halves_on_throttling(int rate, int expected)
    {
        Assert.Equal(expected, AdaptivePacing.ReduceRate(rate));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(2)]
    [InlineData(1)]
    public void ReduceRate_never_falls_below_the_floor(int rate)
    {
        // Halving without a floor would eventually reach zero, which disables pacing
        // entirely - the opposite of what a throttled tenant needs.
        Assert.True(AdaptivePacing.ReduceRate(rate) >= AdaptivePacing.MinAdaptiveRate);
    }

    [Fact]
    public void RecoverRate_climbs_additively_not_by_doubling()
    {
        // Additive increase against multiplicative decrease: the rate approaches the
        // throttling threshold instead of leaping back onto it.
        Assert.Equal(12, AdaptivePacing.RecoverRate(10, 20));
    }

    [Fact]
    public void RecoverRate_always_makes_progress_at_small_configured_rates()
    {
        // configuredRate / 10 rounds to zero below 10 items/sec, which would stall recovery.
        Assert.Equal(3, AdaptivePacing.RecoverRate(2, 5));
    }

    [Fact]
    public void RecoverRate_stops_at_the_configured_rate()
    {
        Assert.Equal(20, AdaptivePacing.RecoverRate(19, 20));
        Assert.Equal(20, AdaptivePacing.RecoverRate(20, 20));
    }

    [Fact]
    public void Reduced_rate_climbs_all_the_way_back_to_the_configured_rate()
    {
        // The regression this whole change is about: from halved, repeated clean chunks
        // must terminate at the configured rate rather than converging below it.
        const int configured = 20;
        var rate = AdaptivePacing.ReduceRate(configured);
        Assert.True(rate < configured);

        for (var i = 0; i < 100 && rate < configured; i++)
            rate = AdaptivePacing.RecoverRate(rate, configured);

        Assert.Equal(configured, rate);
    }

    // Both timestamps are synthesized rather than anchored to Stopwatch.GetTimestamp().
    private static long Ticks(double seconds) => (long)(seconds * Stopwatch.Frequency);

    private static long RecoveryWindowTicks =>
        Ticks(AdaptivePacing.AdaptiveRecoveryWindow.TotalSeconds);

    [Fact]
    public void Adapted_rate_expires_after_a_long_quiet_period()
    {
        const long throttledAt = 1;

        Assert.True(AdaptivePacing.AdaptedRateHasExpired(
            throttledAt, throttledAt + RecoveryWindowTicks + Ticks(60)));
    }

    [Fact]
    public void Adapted_rate_survives_a_recent_throttle()
    {
        const long throttledAt = 1;

        Assert.False(AdaptivePacing.AdaptedRateHasExpired(throttledAt, throttledAt + Ticks(5)));
    }

    [Fact]
    public void Adapted_rate_survives_right_up_to_the_window_boundary()
    {
        const long throttledAt = 1;

        Assert.False(AdaptivePacing.AdaptedRateHasExpired(
            throttledAt, throttledAt + RecoveryWindowTicks));
        Assert.True(AdaptivePacing.AdaptedRateHasExpired(
            throttledAt, throttledAt + RecoveryWindowTicks + 1));
    }

    [Fact]
    public void Never_expires_when_no_throttle_was_ever_recorded()
    {
        // Zero means "no 429 seen yet", not "throttled at boot".
        Assert.False(AdaptivePacing.AdaptedRateHasExpired(0, RecoveryWindowTicks * 10));
    }

    // --- workload classifier ---
    // Drive markers anywhere in the path win (a user's OneDrive lives under /users/{id}/drive),
    // then non-directory service markers (a group calendar is Exchange-backed), then the first
    // segment decides directory membership. Other is the safe default. The same classifier
    // picks the -Latest token form: Drive => token=latest, everything else $deltatoken=latest.

    [Theory]
    [InlineData("/users")]
    [InlineData("/v1.0/users?$top=5")]
    [InlineData("https://graph.microsoft.com/v1.0/groups/abc/members")]
    [InlineData("/beta/servicePrincipals")]
    [InlineData("/groups/delta?$deltatoken=latest")]
    [InlineData("/me")]
    [InlineData("/me/memberOf")]
    [InlineData("/organization")]
    [InlineData("/directoryObjects/getByIds")]
    public void Classify_directory_workloads(string uri) =>
        Assert.Equal(WorkloadBucket.Directory, AdaptivePacing.Classify(uri));

    [Theory]
    [InlineData("/me/drive/root/delta")]
    [InlineData("https://graph.microsoft.com/v1.0/drives/b!x/items/01ABC/content")]
    [InlineData("/users/user@contoso.com/drive/root/children")]
    [InlineData("/sites/contoso.sharepoint.com,guid/lists/Documents/items/delta")]
    [InlineData("/groups/abc/drive/root/delta?token=latest")]
    [InlineData("/shares/u!abc123/driveItem")]
    public void Classify_drive_workloads(string uri) =>
        Assert.Equal(WorkloadBucket.Drive, AdaptivePacing.Classify(uri));

    [Theory]
    [InlineData("/users/user@contoso.com/messages")]         // Exchange, despite /users root
    [InlineData("/groups/abc/events")]                        // group calendar is Exchange-backed
    [InlineData("/me/photo/$value")]
    [InlineData("/teams/abc/channels/xyz/messages/delta")]
    [InlineData("/deviceManagement/managedDevices")]
    [InlineData("/$batch")]
    [InlineData("/mailFolders('inbox')/messages/delta")]
    [InlineData("")]
    [InlineData("not a uri at all")]
    public void Classify_everything_else_lands_in_other(string uri) =>
        Assert.Equal(WorkloadBucket.Other, AdaptivePacing.Classify(uri));

    [Fact]
    public void Classify_null_is_other() =>
        Assert.Equal(WorkloadBucket.Other, AdaptivePacing.Classify(null));
}
