using System.Net;
using System.Net.Http.Headers;
using Mgx.Engine.Http;

namespace Mgx.IntegrationTests.Engine.Http;

/// <summary>
/// Retry-After has two legal forms - delta-seconds and an HTTP-date. The content path honoured
/// only Delta, so a download host choosing the date form fell back to plain exponential backoff
/// with nothing to say so. These pin the parsing both ways.
/// </summary>
public class RetryAfterDateFormTests
{
    // The production resolver itself. This used to be a local copy of it, annotated "mirrors
    // GraphContentClient's DelayGenerator" - so every test below asserted that a copy of the
    // code behaved like itself, and deleting the HTTP-date branch from the product left all
    // 567 tests green.
    private static TimeSpan? Resolve(RetryConditionHeaderValue? retryAfter) =>
        GraphContentClient.ResolveRetryDelay(retryAfter);

    [Fact]
    public void Delta_form_is_honoured()
    {
        var d = Resolve(new RetryConditionHeaderValue(TimeSpan.FromSeconds(7)));
        Assert.NotNull(d);
        Assert.Equal(7, d!.Value.TotalSeconds, 0);
    }

    [Fact]
    public void Date_form_is_honoured()
    {
        var d = Resolve(new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(30)));
        Assert.NotNull(d);
        Assert.InRange(d!.Value.TotalSeconds, 20, 31);
    }

    [Fact]
    public void A_past_date_yields_no_delay_rather_than_a_negative_one()
    {
        Assert.Null(Resolve(new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(-60))));
    }

    [Fact]
    public void A_far_future_date_is_capped_at_two_minutes()
    {
        var d = Resolve(new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddHours(6)));
        Assert.NotNull(d);
        Assert.Equal(120, d!.Value.TotalSeconds, 0);
    }

    [Fact]
    public void A_huge_delta_is_capped_too()
    {
        var d = Resolve(new RetryConditionHeaderValue(TimeSpan.FromHours(6)));
        Assert.Equal(120, d!.Value.TotalSeconds, 0);
    }

    [Fact]
    public void No_header_yields_no_delay()
    {
        Assert.Null(Resolve(null));
    }
}
