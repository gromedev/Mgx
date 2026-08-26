using System.Management.Automation;
using System.Net;
using Mgx.Engine.Errors;
using Mgx.Engine.Models;
using Polly.CircuitBreaker;

namespace Mgx.Cmdlets.Base;

/// <summary>
/// How a classified failure surfaces to PowerShell: the ErrorCategory for a class, the
/// status inside an exception, and the shared presentation of infrastructure failures.
/// Before this existed, the category map covered six statuses and left a 500 in
/// NotSpecified, the status extraction existed verbatim in two cmdlets, and the fan-out
/// error switches disagreed on whether a broken circuit carried its inner status.
/// </summary>
internal static class MgxErrorPresentation
{
    /// <summary>The PowerShell ErrorCategory for a failure class.</summary>
    internal static ErrorCategory Category(MgxErrorClass cls) => cls switch
    {
        MgxErrorClass.Throttle => ErrorCategory.LimitsExceeded,
        MgxErrorClass.TransientServer => ErrorCategory.ResourceUnavailable,
        MgxErrorClass.TransientTransport => ErrorCategory.ConnectionError,
        MgxErrorClass.Authentication => ErrorCategory.AuthenticationError,
        MgxErrorClass.Authorization => ErrorCategory.PermissionDenied,
        MgxErrorClass.Consistency => ErrorCategory.ResourceUnavailable,
        MgxErrorClass.InvalidRequest => ErrorCategory.InvalidArgument,
        MgxErrorClass.NotFound => ErrorCategory.ObjectNotFound,
        MgxErrorClass.Conflict => ErrorCategory.ResourceExists,
        _ => ErrorCategory.NotSpecified,
    };

    /// <summary>The category for a status, through the classifier.</summary>
    internal static ErrorCategory CategoryForStatus(HttpStatusCode statusCode)
        => Category(MgxErrorClassifier.Classify((int)statusCode).Class);

    /// <summary>The HTTP status a thrown exception carries, when it carries one.</summary>
    internal static HttpStatusCode? TryGetStatus(Exception ex)
    {
        if (ex is GraphServiceException gse) return gse.StatusCode;
        if (ex is HttpRequestException hre && hre.StatusCode.HasValue) return hre.StatusCode.Value;
        return null;
    }

    /// <summary>Whether -SkipNotFound / -SkipForbidden apply to this status.</summary>
    internal static bool ShouldSkip(HttpStatusCode? status, bool skipNotFound, bool skipForbidden)
        => (skipNotFound && status == HttpStatusCode.NotFound)
           || (skipForbidden && status == HttpStatusCode.Forbidden);

    /// <summary>
    /// One presentation for a per-item failure: id, category, and the exception to report.
    /// A broken circuit is rewrapped with the session's guidance text - previously one
    /// fan-out path did this and the other did not, so the same failure surfaced
    /// differently depending on the cmdlet. Always ResourceUnavailable: the breaker's
    /// exception never carries a Graph error (those are constructed above the pipeline),
    /// and "stop sending" is the message whatever opened it.
    /// </summary>
    internal static (string ErrorId, ErrorCategory Category, Exception Report) PresentItemFailure(
        Exception ex, string fallbackErrorId, string circuitMessage)
    {
        return ex switch
        {
            BrokenCircuitException bce => (
                "CircuitBroken",
                ErrorCategory.ResourceUnavailable,
                new InvalidOperationException(circuitMessage, bce)),
            HttpRequestException => ("HttpError", ErrorCategory.ConnectionError, ex),
            System.Text.Json.JsonException => (
                "MalformedJsonResponse", ErrorCategory.InvalidData,
                new InvalidOperationException($"A response declared JSON but does not parse: {ex.Message}", ex)),
            _ => (
                fallbackErrorId,
                TryGetStatus(ex) is { } status ? CategoryForStatus(status) : ErrorCategory.NotSpecified,
                ex),
        };
    }
}
