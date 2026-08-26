using System.Net.Http.Headers;

namespace Mgx.Engine.Errors;

/// <summary>
/// One reading of Retry-After. The header has two forms - delta-seconds and an HTTP date -
/// and before this existed the pipeline, the batch client, the content client, and the
/// pacer each parsed it themselves, two of them with their own caps.
/// </summary>
public static class RetryAfterPolicy
{
    /// <summary>The server's requested delay, unclamped: delta-seconds verbatim, or
    /// date minus now (which may be negative for a past date). Null when absent.</summary>
    public static TimeSpan? ServerRequested(RetryConditionHeaderValue? retryAfter)
        => retryAfter?.Delta
           ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);

    /// <summary>
    /// The delay to actually sleep: the server's request clamped to <paramref name="cap"/>,
    /// or null when the header is absent or asks for the past - callers fall back to their
    /// exponential backoff. A delta is honored even at zero; a date is honored only in the
    /// future. This is exactly what the pipeline and the content client each did alone.
    /// </summary>
    public static TimeSpan? Resolve(RetryConditionHeaderValue? retryAfter, TimeSpan cap)
    {
        if (retryAfter?.Delta is { } delta)
            return delta > cap ? cap : delta;
        if (retryAfter?.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
                return delay > cap ? cap : delay;
        }
        return null;
    }

    /// <summary>
    /// The batch form: whole seconds from a raw header string, rounded up, floored at zero,
    /// clamped to <paramref name="capSeconds"/>. <paramref name="requestedSeconds"/> carries
    /// what the server asked for so the caller can say when it clamped.
    /// </summary>
    public static bool TryResolveSeconds(string? headerValue, int capSeconds,
        out int requestedSeconds, out int clampedSeconds)
    {
        requestedSeconds = 0;
        clampedSeconds = 0;
        if (headerValue == null || !RetryConditionHeaderValue.TryParse(headerValue, out var ra))
            return false;

        var delay = ra.Delta ?? (ra.Date.HasValue ? ra.Date.Value - DateTimeOffset.UtcNow : (TimeSpan?)null);
        if (!delay.HasValue) return false;

        requestedSeconds = (int)Math.Ceiling(delay.Value.TotalSeconds);
        clampedSeconds = Math.Min(Math.Max(requestedSeconds, 0), capSeconds);
        return true;
    }
}
