using System.Text;
using System.Text.RegularExpressions;

namespace Mgx.Engine.Http;

/// <summary>
/// Formats HTTP request/response traces for -Debug output. Credentials are redacted and bodies
/// truncated so a trace never leaks a secret or dumps a full page of Graph data.
/// </summary>
internal static partial class GraphRequestTracer
{
    /// <summary>Bodies longer than this are cut, with the omitted length noted.</summary>
    internal const int MaxBodyChars = 4096;

    /// <summary>
    /// Response headers worth tracing. Everything else (caching, CORS, transport) is noise.
    /// Prefix entries match any header starting with the value.
    /// </summary>
    private static readonly string[] ResponseHeaderNames =
    [
        "request-id", "client-request-id", "x-ms-ags-diagnostic", "Retry-After",
        "x-ms-resource-unit", "Location", "OData-Version"
    ];

    private static readonly string[] ResponseHeaderPrefixes = ["RateLimit-", "x-ms-throttle-"];

    [GeneratedRegex("\"([^\"]*(?:password|secret|credential|token|key)[^\"]*)\"\\s*:\\s*\"[^\"]*\"",
        RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveJsonValue();

    /// <summary>
    /// A pre-authenticated URL is a credential whose property name says nothing about it:
    /// "@microsoft.graph.downloadUrl" contains none of password/secret/credential/token/key, so
    /// SensitiveJsonValue leaves it whole and the tempauth JWT reaches -Debug output. Match on
    /// the VALUE instead - a URL carrying one of the capability parameters Graph, SharePoint and
    /// the download hosts actually use. Deliberately not "every URL": @odata.nextLink is a URL
    /// too, and losing it from a trace would remove the thing paging bugs are diagnosed with.
    /// </summary>
    [GeneratedRegex("\"(https?://[^\"]*?(?:tempauth|guestaccesstoken|authkey|X-Amz-Signature|(?<![a-z])sig)=)[^\"]*\"",
        RegexOptions.IgnoreCase)]
    private static partial Regex CapabilityUrlValue();

    /// <summary>
    /// Property names that carry a pre-authenticated URL even when the value does not expose a
    /// recognisable parameter - the short-form download hosts put the capability in the path.
    /// </summary>
    [GeneratedRegex("\"([^\"]*downloadurl[^\"]*)\"\\s*:\\s*\"[^\"]*\"",
        RegexOptions.IgnoreCase)]
    private static partial Regex DownloadUrlProperty();

    /// <summary>
    /// Trace line for an outgoing request. <paramref name="attempt"/> is 1-based so retries are visible.
    /// </summary>
    internal static string FormatRequest(HttpRequestMessage request, byte[]? body, int attempt)
    {
        var sb = new StringBuilder();
        var label = attempt > 1 ? $"Request (attempt {attempt})" : "Request";
        sb.Append("[Mgx] ").Append(label).Append(": ")
          .Append(request.Method.Method).Append(' ').Append(request.RequestUri);

        sb.AppendLine().Append("  Headers:");
        // The bearer token is attached further down the pipeline by the auth handler,
        // so it is not on this HttpRequestMessage. Show it for completeness, never its value.
        sb.AppendLine().Append("    Authorization: Bearer <redacted>");
        foreach (var header in request.Headers)
            sb.AppendLine().Append("    ").Append(header.Key).Append(": ").Append(Join(header.Value));
        if (request.Content != null)
        {
            foreach (var header in request.Content.Headers)
                sb.AppendLine().Append("    ").Append(header.Key).Append(": ").Append(Join(header.Value));
        }

        if (body is { Length: > 0 })
        {
            sb.AppendLine().Append("  Body (").Append(body.Length).Append(" bytes):")
              .AppendLine().Append(Sanitize(Encoding.UTF8.GetString(body)));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Trace line for a response. <paramref name="body"/> is null when the body was not buffered.
    /// </summary>
    internal static string FormatResponse(HttpResponseMessage response, long elapsedMs, string? body)
    {
        var sb = new StringBuilder();
        sb.Append("[Mgx] Response: ").Append((int)response.StatusCode).Append(' ')
          .Append(response.StatusCode).Append(" in ").Append(elapsedMs).Append(" ms");

        var headers = response.Headers
            .Concat(response.Content.Headers)
            .Where(h => IsTraced(h.Key))
            .ToList();
        if (headers.Count > 0)
        {
            sb.AppendLine().Append("  Headers:");
            foreach (var header in headers)
                sb.AppendLine().Append("    ").Append(header.Key).Append(": ")
                  .Append(RedactHeaderValue(header.Key, Join(header.Value)));
        }

        if (!string.IsNullOrEmpty(body))
        {
            sb.AppendLine().Append("  Body (").Append(Encoding.UTF8.GetByteCount(body)).Append(" bytes):")
              .AppendLine().Append(Sanitize(body));
        }

        return sb.ToString();
    }

    private static bool IsTraced(string name) =>
        ResponseHeaderNames.Contains(name, StringComparer.OrdinalIgnoreCase)
        || ResponseHeaderPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
        || name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase);

    private static string Join(IEnumerable<string> values) => string.Join(", ", values);

    /// <summary>
    /// A content 302's Location is a pre-authenticated URL: it grants the file bytes without a
    /// bearer token, so tracing it verbatim writes a live credential into -Debug output. The
    /// capability is not always in the query - some download hosts carry it in the path - so
    /// keep only scheme and host, which is the whole diagnostic value of the header anyway.
    /// The JSON-body redaction cannot reach header values, hence this hook.
    /// </summary>
    private static string RedactHeaderValue(string name, string value)
    {
        if (!name.Equals("Location", StringComparison.OrdinalIgnoreCase)) return value;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.IdnHost}/<redacted>"
            : "<redacted>";
    }

    /// <summary>Redact credential-looking JSON properties and pre-authenticated URLs, then truncate.</summary>
    private static string Sanitize(string body)
    {
        var redacted = SensitiveJsonValue().Replace(body, "\"$1\": \"<redacted>\"");
        // Property-name match first: covers a downloadUrl whose capability sits in the path.
        redacted = DownloadUrlProperty().Replace(redacted, "\"$1\": \"<redacted>\"");
        // Then any remaining URL value carrying a capability parameter, keeping the parameter
        // name visible so a trace still shows WHICH kind of URL was redacted.
        redacted = CapabilityUrlValue().Replace(redacted, "\"$1<redacted>\"");
        return redacted.Length <= MaxBodyChars
            ? redacted
            : redacted[..MaxBodyChars] + $"... [truncated, {redacted.Length - MaxBodyChars} more chars]";
    }
}
