using Mgx.Engine.Errors;

namespace Mgx.IntegrationTests;

public class ErrorClassifierTests
{
    [Theory]
    [InlineData(429, MgxErrorClass.Throttle)]
    [InlineData(500, MgxErrorClass.TransientServer)]
    [InlineData(502, MgxErrorClass.TransientServer)]
    [InlineData(503, MgxErrorClass.TransientServer)]
    [InlineData(504, MgxErrorClass.TransientServer)]
    [InlineData(408, MgxErrorClass.TransientTransport)]
    [InlineData(401, MgxErrorClass.Authentication)]
    [InlineData(403, MgxErrorClass.Authorization)]
    [InlineData(404, MgxErrorClass.NotFound)]
    [InlineData(409, MgxErrorClass.Conflict)]
    [InlineData(412, MgxErrorClass.Conflict)]
    [InlineData(400, MgxErrorClass.InvalidRequest)]
    [InlineData(405, MgxErrorClass.InvalidRequest)]
    [InlineData(413, MgxErrorClass.InvalidRequest)]
    [InlineData(415, MgxErrorClass.InvalidRequest)]
    [InlineData(422, MgxErrorClass.InvalidRequest)]
    [InlineData(501, MgxErrorClass.InvalidRequest)]
    [InlineData(410, MgxErrorClass.Permanent)]   // delta resync belongs to a later phase
    [InlineData(505, MgxErrorClass.Permanent)]   // unknown 5xx: never retried, stays that way
    [InlineData(507, MgxErrorClass.Permanent)]
    [InlineData(418, MgxErrorClass.Permanent)]
    public void Status_maps_to_its_class(int status, MgxErrorClass expected)
        => Assert.Equal(expected, MgxErrorClassifier.Classify(status).Class);

    [Fact]
    public void A_response_carries_status_and_unclamped_retry_after()
    {
        using var response = new HttpResponseMessage((System.Net.HttpStatusCode)429);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
            TimeSpan.FromSeconds(999));
        var info = MgxErrorClassifier.Classify(response);
        Assert.Equal(MgxErrorClass.Throttle, info.Class);
        Assert.Equal(429, info.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(999), info.ServerRetryAfter); // facts unclamped; policy clamps
    }

    [Fact]
    public void Cancellation_is_permanent_regardless_of_exception_shape()
    {
        Assert.Equal(MgxErrorClass.Permanent,
            MgxErrorClassifier.Classify(new TaskCanceledException(), cancellationRequested: true).Class);
        Assert.Equal(MgxErrorClass.Permanent,
            MgxErrorClassifier.Classify(new Polly.Timeout.TimeoutRejectedException(), cancellationRequested: true).Class);
    }
}
