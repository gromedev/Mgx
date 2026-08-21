using System.Net;
using System.Text.Json;

namespace Mgx.Engine.Models;

/// <summary>
/// Exception thrown when the Graph API returns an error response.
/// Parses the { "error": { "code": "...", "message": "..." } } body.
/// </summary>
public class GraphServiceException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string? ErrorCode { get; }

    public GraphServiceException(HttpStatusCode statusCode, string responseBody)
        : base(FormatAndExtract(statusCode, responseBody, out var code))
    {
        StatusCode = statusCode;
        ErrorCode = code;
    }

    /// <summary>
    /// Parse the Graph error response body once, extracting both the formatted message and error code.
    /// Appends guidance hint when available for known error codes.
    /// </summary>
    private static string FormatAndExtract(HttpStatusCode statusCode, string responseBody, out string? errorCode)
    {
        errorCode = null;
        if (string.IsNullOrEmpty(responseBody))
            return $"HTTP {(int)statusCode}: {statusCode}";

        try
        {
            using var doc = JsonDocument.Parse(responseBody);

            // Shape-check before every access. TryGetProperty throws InvalidOperationException on
            // a non-object, and GetString() throws on a non-string - neither is a JsonException,
            // so valid JSON of an unexpected shape used to escape this method as an exception
            // thrown from inside an exception's own constructor. Graph always emits the OData
            // envelope, but the content path's second hop talks to SharePoint, OneDrive and CDN
            // hosts that are not Graph and answer with whatever they like.
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return $"HTTP {(int)statusCode}: {statusCode}";

            if (doc.RootElement.TryGetProperty("error", out var errorObj))
            {
                if (errorObj.ValueKind != JsonValueKind.Object)
                    return $"HTTP {(int)statusCode}: {statusCode}";

                var code = AsString(errorObj, "code");
                var message = AsString(errorObj, "message");
                errorCode = code;

                // Build formatted message from whatever Graph provided
                var formatted = !string.IsNullOrEmpty(code)
                    ? $"{code}: {message}"
                    : !string.IsNullOrEmpty(message)
                        ? message
                        : $"HTTP {(int)statusCode}: {statusCode}";

                var guidance = GetGuidanceForCode(code);
                if (guidance != null)
                    formatted += $"\nHint: {guidance}";
                return formatted;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Backstop: the shape guards above cover the reachable cases, but an error body must
            // never be able to throw out of here - callers are constructing an exception.
        }
        return $"HTTP {(int)statusCode}: {statusCode}";
    }

    /// <summary>
    /// A string property, or null when absent or not actually a string. Graph nests a non-string
    /// "message" on some endpoints (an object with a "value"), which GetString() rejects outright.
    /// </summary>
    private static string? AsString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.String) return v.GetString();

        // Some endpoints nest it as { "value": "..." } - the very shape this helper's own
        // comment names. Returning null there produced "Code: " with an empty message, which
        // is strictly worse than the text that was sitting one level down.
        if (v.ValueKind == JsonValueKind.Object
            && v.TryGetProperty("value", out var inner)
            && inner.ValueKind == JsonValueKind.String)
        {
            return inner.GetString();
        }
        return null;
    }

    /// <summary>
    /// Maps common Graph error codes to user-facing guidance strings.
    /// Returns null for unrecognized codes.
    /// </summary>
    internal static string? GetGuidanceForCode(string? errorCode) => errorCode switch
    {
        "Authorization_RequestDenied" => "Check your Graph scopes with Get-MgContext. The required permission may not be consented.",
        "Request_ResourceNotFound" => "Verify the URI path and that the resource exists. Use -SkipNotFound to suppress in fan-out.",
        "Request_BadRequest" => "Check $filter syntax, property names, and $search quoting. Use -ConsistencyLevel eventual for advanced queries.",
        "InvalidAuthenticationToken" => "Session may have expired. Run Connect-MgGraph to re-authenticate.",
        "Authentication_ExpiredToken" => "Token has expired. Run Connect-MgGraph to re-authenticate.",
        "ErrorAccessDenied" => "Insufficient permissions. Check required scopes at https://learn.microsoft.com/graph/permissions-reference.",
        "Forbidden" => "Access denied. This may require admin consent or an application permission (not delegated).",
        "TooManyRequests" or "activityLimitReached" => "Throttled by Graph API. Mgx handles this automatically; increase -TotalTimeoutSeconds if retries are exhausted.",
        "ServiceNotAvailable" => "Graph service is temporarily unavailable. Mgx retries automatically; check https://status.cloud.microsoft.com for outages.",
        "BadRequest" => "Malformed request. Check $filter, $select, $orderby syntax and property name spelling.",
        _ => null
    };
}
