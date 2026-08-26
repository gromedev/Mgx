namespace Mgx.Engine.Errors;

/// <summary>
/// What to do about a classified failure. Retry and circuit breaking legitimately
/// disagree - that disagreement lives here, in one visible place, instead of being
/// implied by two lists a hundred lines apart.
/// </summary>
public static class MgxErrorPolicy
{
    /// <summary>
    /// Whether another attempt may help. Throttles retry for every method including POST
    /// (matches the Kiota SDK); server and transport failures retry only when the method is
    /// idempotent, because a 5xx or a dead connection may mean a write was already applied.
    /// </summary>
    public static bool ShouldRetry(MgxErrorClass cls, bool isIdempotent) => cls switch
    {
        MgxErrorClass.Throttle => true,
        MgxErrorClass.TransientServer or MgxErrorClass.TransientTransport => isIdempotent,
        _ => false,
    };

    /// <summary>
    /// Whether the failure counts toward opening the circuit breaker.
    /// 429 and 408 are excluded deliberately: 429 is the service pacing us, not failing -
    /// Retry-After handles it, and counting it would open the circuit exactly when the
    /// correct response is to slow down and keep going. 408 is a client-perceived timeout,
    /// not a server-side failure indicator.
    /// </summary>
    public static bool CountsAsCircuitFailure(in MgxErrorInfo info) =>
        info.Class == MgxErrorClass.TransientServer
        || (info.Class == MgxErrorClass.TransientTransport && info.StatusCode != 408);

    /// <summary>
    /// The download-host retry filter, narrower than <see cref="ShouldRetry"/> on purpose:
    /// no 408 and no cancellation-shaped timeouts, because the content path handles stalls
    /// with its own idle timeout and rebuilds hop 1 on an expired URL. Throttles, 5xx, and
    /// connection failures retry; everything else surfaces.
    /// </summary>
    public static bool ShouldRetryDownload(in MgxErrorInfo info, Exception? exception) =>
        info.Class is MgxErrorClass.Throttle or MgxErrorClass.TransientServer
        || exception is HttpRequestException;
}
