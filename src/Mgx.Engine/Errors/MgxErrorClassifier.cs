using System.Net.Http.Headers;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Mgx.Engine.Errors;

/// <summary>
/// The one place a status code or exception becomes an <see cref="MgxErrorClass"/>.
/// Before this existed the same decision lived in the retry predicate, the circuit-breaker
/// predicate, the batch item loop, the content download pipeline, and the pacer - each with
/// its own list, and the lists disagreed in ways none of them documented.
/// </summary>
public static class MgxErrorClassifier
{
    /// <summary>Classify an HTTP status. For non-success codes; a 2xx/3xx is not a failure
    /// and lands in <see cref="MgxErrorClass.Permanent"/> only because nothing retries it.</summary>
    public static MgxErrorInfo Classify(int statusCode, string? errorCode = null, TimeSpan? serverRetryAfter = null)
    {
        var cls = statusCode switch
        {
            429 => MgxErrorClass.Throttle,
            500 or 502 or 503 or 504 => MgxErrorClass.TransientServer,
            // A 408 is a timeout the request may not have survived - transport, not server:
            // the server did not fail, it never saw the whole request.
            408 => MgxErrorClass.TransientTransport,
            401 => MgxErrorClass.Authentication,
            403 => MgxErrorClass.Authorization,
            404 => MgxErrorClass.NotFound,
            409 or 412 => MgxErrorClass.Conflict,
            400 or 405 or 411 or 413 or 414 or 415 or 416 or 422 or 501 => MgxErrorClass.InvalidRequest,
            // Unknown 5xx (505, 507, ...) deliberately Permanent: mgx has never retried
            // them, and classifying them TransientServer would silently start.
            _ => MgxErrorClass.Permanent,
        };
        return new MgxErrorInfo(cls, statusCode, errorCode, serverRetryAfter);
    }

    /// <summary>Classify a response. Reads status and Retry-After; never the body -
    /// retry decisions run at ResponseHeadersRead, so the body is not available.</summary>
    public static MgxErrorInfo Classify(HttpResponseMessage response, string? errorCode = null)
        => Classify((int)response.StatusCode, errorCode,
            RetryAfterPolicy.ServerRequested(response.Headers.RetryAfter));

    /// <summary>
    /// Classify an exception. <paramref name="cancellationRequested"/> is the caller's own
    /// token state: a cancellation-shaped exception after the caller cancelled is
    /// Permanent - nothing may act on it - while the same exception without a cancelled
    /// token is a timeout, which is transport.
    /// </summary>
    public static MgxErrorInfo Classify(Exception exception, bool cancellationRequested)
    {
        if (cancellationRequested && exception
                is OperationCanceledException or TimeoutRejectedException)
            return new MgxErrorInfo(MgxErrorClass.Permanent, 0);

        var cls = exception switch
        {
            HttpRequestException => MgxErrorClass.TransientTransport,
            System.Net.Sockets.SocketException => MgxErrorClass.TransientTransport,
            IOException => MgxErrorClass.TransientTransport,
            // TaskCanceledException without user cancellation is HttpClient's own timeout.
            OperationCanceledException => MgxErrorClass.TransientTransport,
            TimeoutRejectedException => MgxErrorClass.TransientTransport,
            // The breaker is open; retrying into it is the thing it exists to stop.
            BrokenCircuitException => MgxErrorClass.Permanent,
            _ => MgxErrorClass.Permanent,
        };
        return new MgxErrorInfo(cls, 0);
    }
}
