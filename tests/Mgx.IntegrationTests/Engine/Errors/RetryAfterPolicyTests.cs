using System.Net.Http.Headers;
using Mgx.Engine.Errors;

namespace Mgx.IntegrationTests;

public class RetryAfterPolicyTests
{
    private static readonly TimeSpan Cap = TimeSpan.FromSeconds(120);

    [Fact]
    public void Delta_form_is_honored_and_clamped()
    {
        Assert.Equal(TimeSpan.FromSeconds(30),
            RetryAfterPolicy.Resolve(new RetryConditionHeaderValue(TimeSpan.FromSeconds(30)), Cap));
        Assert.Equal(Cap,
            RetryAfterPolicy.Resolve(new RetryConditionHeaderValue(TimeSpan.FromSeconds(600)), Cap));
        Assert.Equal(TimeSpan.Zero,
            RetryAfterPolicy.Resolve(new RetryConditionHeaderValue(TimeSpan.Zero), Cap));
    }

    [Fact]
    public void Date_form_is_honored_in_the_future_and_ignored_in_the_past()
    {
        var future = RetryAfterPolicy.Resolve(
            new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(40)), Cap);
        Assert.NotNull(future);
        Assert.InRange(future!.Value.TotalSeconds, 35, 41);

        Assert.Null(RetryAfterPolicy.Resolve(
            new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(-40)), Cap));

        var farFuture = RetryAfterPolicy.Resolve(
            new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddHours(2)), Cap);
        Assert.Equal(Cap, farFuture);
    }

    [Fact]
    public void Absent_header_defers_to_backoff()
        => Assert.Null(RetryAfterPolicy.Resolve(null, Cap));

    [Fact]
    public void Batch_seconds_form_rounds_up_floors_at_zero_and_clamps()
    {
        Assert.True(RetryAfterPolicy.TryResolveSeconds("30", 120, out var req, out var clamped));
        Assert.Equal(30, req);
        Assert.Equal(30, clamped);

        Assert.True(RetryAfterPolicy.TryResolveSeconds("600", 120, out req, out clamped));
        Assert.Equal(600, req);
        Assert.Equal(120, clamped);

        // A past HTTP date computes negative; the sleep floors at zero.
        var past = DateTimeOffset.UtcNow.AddSeconds(-30).ToString("R");
        Assert.True(RetryAfterPolicy.TryResolveSeconds(past, 120, out req, out clamped));
        Assert.True(req <= 0);
        Assert.Equal(0, clamped);

        Assert.False(RetryAfterPolicy.TryResolveSeconds("not-a-value", 120, out _, out _));
        Assert.False(RetryAfterPolicy.TryResolveSeconds(null, 120, out _, out _));
    }
}
