using System.Management.Automation;
using System.Net;
using System.Net.Http.Headers;
using Mgx.Cmdlets.Base;
using Mgx.Engine.Http;
using Mgx.Engine.Models;
using Polly.CircuitBreaker;

namespace Mgx.Cmdlets.Cmdlets.Content;

/// <summary>
/// Get-MgxContent: Fetch content bytes ($value / /content endpoints), whole or as a byte
/// range. Ranged reads are the point: pulling 256 KB headers off 20k files moves ~5 GB
/// instead of 63 GB for a metadata job.
///
/// Two hops under the hood: the authenticated Graph request (full resilience pipeline,
/// pacing, rate limiting), then - when Graph 302s to a pre-authenticated download host - a
/// token-free fetch whose target is validated against a Microsoft-hosts allowlist. The
/// bearer token never reaches the download host. See GraphContentClient for the mechanism
/// and the transport preconditions.
///
/// Output is a single byte[] to the pipeline, or a file via -OutFile (temp + atomic move).
/// Piped DriveItems use their @microsoft.graph.downloadUrl directly (validated first) or
/// fall back to /drives/{driveId}/items/{id}/content.
/// </summary>
[Cmdlet(VerbsCommon.Get, "MgxContent", DefaultParameterSetName = "Uri")]
[OutputType(typeof(byte[]))]
public class GetMgxContent : MgxCmdletBase
{
    /// <summary>Pipeline output guard: a byte[] larger than this must go to -OutFile.</summary>
    internal const long MaxPipelineBytes = 100L * 1024 * 1024;

    [Parameter(Mandatory = true, Position = 0, ParameterSetName = "Uri")]
    [Alias("Resource")]
    public string Uri { get; set; } = string.Empty;

    [Parameter(Mandatory = true, ValueFromPipeline = true, ParameterSetName = "InputObject")]
    public object? InputObject { get; set; }

    /// <summary>First N bytes (Range: bytes=0..N-1). Mutually exclusive with -Offset/-Length.</summary>
    [Parameter]
    [ValidateRange(1, long.MaxValue)]
    public long First { get; set; }

    /// <summary>Range start, used with -Length (Range: bytes=Offset..Offset+Length-1).</summary>
    [Parameter]
    [ValidateRange(0, long.MaxValue)]
    public long Offset { get; set; }

    /// <summary>Range length, from -Offset (default 0).</summary>
    [Parameter]
    [ValidateRange(1, long.MaxValue)]
    public long Length { get; set; }

    [Parameter]
    public string? OutFile { get; set; }

    [Parameter]
    [ValidateSet("v1.0", "beta")]
    [ArgumentCompleter(typeof(ApiVersionCompleter))]
    public string ApiVersion { get; set; } = "v1.0";

    [Parameter]
    public System.Collections.Hashtable? Headers { get; set; }

    private string VersionedBaseUrl => $"{s_graphEndpoint}/{ApiVersion}";
    private string? _resolvedOutFile;
    private bool _transportChecked;

    protected override void BeginProcessing()
    {
        if (ParameterSetName == "Uri")
        {
            if (Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                Uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                ThrowTerminatingError(new ErrorRecord(
                    new ArgumentException(
                        $"-Uri must be a relative Graph path (e.g., /me/drive/items/{{id}}/content), not an absolute URL. Got: '{Uri}'"),
                    "AbsoluteUriNotAllowed", ErrorCategory.InvalidArgument, null));
                return;
            }

            if (!Uri.Contains("/content", StringComparison.OrdinalIgnoreCase)
                && !Uri.Contains("$value", StringComparison.OrdinalIgnoreCase))
            {
                WriteWarning(
                    $"URI '{Uri}' does not look like a content endpoint (/content or /$value). "
                    + "The response may be JSON metadata rather than file bytes.");
            }
        }

        // -First XOR -Offset/-Length: two ways to say the same thing must not disagree.
        var boundParams = MyInvocation.BoundParameters;
        var hasFirst = boundParams.ContainsKey(nameof(First));
        var hasOffset = boundParams.ContainsKey(nameof(Offset));
        var hasLength = boundParams.ContainsKey(nameof(Length));
        if (hasFirst && (hasOffset || hasLength))
        {
            ThrowTerminatingError(new ErrorRecord(
                new ArgumentException("-First cannot be combined with -Offset/-Length. Use one range form."),
                "RangeParameterConflict", ErrorCategory.InvalidArgument, null));
            return;
        }
        if (hasOffset && !hasLength)
        {
            ThrowTerminatingError(new ErrorRecord(
                new ArgumentException("-Offset requires -Length (Range: bytes=Offset..Offset+Length-1)."),
                "OffsetRequiresLength", ErrorCategory.InvalidArgument, null));
            return;
        }

        if (OutFile != null)
            _resolvedOutFile = GetUnresolvedProviderPathFromPSPath(OutFile);
    }

    protected override void ProcessRecord()
    {
        if (ParameterSetName == "Uri")
        {
            FetchContent(downloadUrl: null,
                relativeUri: Uri,
                errorTarget: Uri);
            return;
        }

        if (InputObject == null)
        {
            WriteVerbose("Skipping null pipeline input.");
            return;
        }

        var value = UnwrapPSObject(InputObject);

        // Prefer the item's own pre-authenticated download URL: no hop-1 round trip, no
        // request-budget charge. Validated against the allowlist before any fetch.
        if (TryGetMember(value, "@microsoft.graph.downloadUrl")?.ToString() is { Length: > 0 } itemDownloadUrl)
        {
            FetchContent(downloadUrl: itemDownloadUrl, relativeUri: null, errorTarget: InputObject);
            return;
        }

        // Fall back to /drives/{driveId}/items/{id}/content from the item's identifiers.
        var id = Cmdlets.InvokeMgxRequest.ResolvePipelineId(value);
        var driveId = TryGetMember(TryGetMember(value, "parentReference"), "driveId")?.ToString();
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(driveId))
        {
            WriteError(new ErrorRecord(
                new ArgumentException(
                    "Pipeline input needs either @microsoft.graph.downloadUrl, or both 'id' and "
                    + "'parentReference.driveId', to locate the content. Pipe DriveItems from "
                    + "Invoke-MgxRequest or Sync-MgxDelta."),
                "MissingDriveItemInfo", ErrorCategory.InvalidArgument, InputObject));
            return;
        }

        FetchContent(downloadUrl: null,
            relativeUri: $"/drives/{driveId}/items/{id}/content",
            errorTarget: InputObject);
    }

    private RangeHeaderValue? BuildRange()
    {
        if (First > 0) return new RangeHeaderValue(0, First - 1);
        if (Length > 0) return new RangeHeaderValue(Offset, Offset + Length - 1);
        return null;
    }

    /// <summary>Bytes the caller actually asked for; null = the whole file.</summary>
    private long? RequestedBytes => First > 0 ? First : Length > 0 ? Length : null;

    private void FetchContent(string? downloadUrl, string? relativeUri, object? errorTarget)
    {
        var client = GetClient();

        // Fail closed on non-owned transports: the 302-interception design requires
        // AllowAutoRedirect off, which only the mgx-owned clean client guarantees. The SDK
        // fallback client ships a RedirectHandler that would auto-follow to an unvalidated
        // host. Checked once per invocation, after GetClient() decided the transport.
        if (!_transportChecked)
        {
            _transportChecked = true;
            if (!TransportIsOwned)
            {
                ThrowTerminatingError(new ErrorRecord(
                    new InvalidOperationException(
                        "Content downloads require the mgx-owned HTTP transport, but this session "
                        + "is using the Graph SDK's client (mgx could not build its own). The SDK "
                        + "transport auto-follows redirects, which defeats the download-host "
                        + "validation. Reconnect with Connect-MgGraph and retry."),
                    "ContentRequiresOwnedTransport", ErrorCategory.SecurityError, null));
                return;
            }
        }

        var range = BuildRange();

        try
        {
            using var result = downloadUrl != null
                ? GraphContentClient.GetFromDownloadUrlAsync(
                    downloadUrl, range, client.BodyReadTimeout, CancellationToken)
                    .GetAwaiter().GetResult()
                : client.GetContentAsync(
                    $"{VersionedBaseUrl}{NormalizePath(relativeUri!)}", range,
                    BuildRequestHeaders(null, Headers), CancellationToken)
                    .GetAwaiter().GetResult();

            DrainClientMessages();

            // Truncation path: a range was requested but the server ignored it and answered
            // 200 with the full body (profile photos do this). Copy only the requested bytes;
            // disposing the result aborts the rest of the transfer.
            long? maxBytes = null;
            long skipBytes = 0;
            if (range != null && result.StatusCode == HttpStatusCode.OK)
            {
                maxBytes = RequestedBytes;
                // The server ignored the offset too, not just the length. Discarding the head
                // locally is the only way -Offset can mean what it says on this path; without
                // it the caller silently receives bytes 0..Length-1 and is told it worked.
                skipBytes = Offset;
                WriteVerbose(skipBytes > 0
                    ? $"Server ignored the Range header (HTTP 200). Discarding the first {skipBytes:N0} bytes and taking {maxBytes:N0}."
                    : $"Server ignored the Range header (HTTP 200). Truncating to the requested {maxBytes} bytes.");
            }

            if (_resolvedOutFile != null)
            {
                WriteToFile(result, maxBytes, skipBytes);
            }
            else
            {
                WriteToPipeline(result, maxBytes, skipBytes, errorTarget);
            }
        }
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
        {
            DrainClientMessages();
            WriteWarning("Content download cancelled.");
        }
        catch (InvalidOperationException ex)
        {
            // Validator refusal or transport-redirect detection: a security boundary, not a
            // transient fault - surface as such, per item.
            DrainClientMessages();
            WriteError(new ErrorRecord(ex, "ContentDownloadRefused", ErrorCategory.SecurityError, errorTarget));
        }
        catch (Exception ex) when (ex is GraphServiceException or BrokenCircuitException or HttpRequestException)
        {
            WriteGraphError(ex, errorTarget, ApiVersion);
        }
        catch (IOException ex)
        {
            DrainClientMessages();
            WriteError(new ErrorRecord(ex, "IOError", ErrorCategory.WriteError, OutFile));
        }
    }

    private void WriteToFile(GraphContentResult result, long? maxBytes, long skipBytes)
    {
        // Temp + atomic move, like every other file-writing path in mgx: a failed download
        // never truncates an existing file.
        var tempPath = $"{_resolvedOutFile}.{Guid.NewGuid():N}.tmp";
        long copied;
        try
        {
            using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            {
                copied = GraphContentClient.CopyWithIdleTimeoutAsync(
                        result.Content, file, maxBytes, GetClient().BodyReadTimeout,
                        CancellationToken, skipBytes)
                    .GetAwaiter().GetResult();
            }
            File.Move(tempPath, _resolvedOutFile!, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }

        WriteVerbose($"{copied:N0} bytes ({(int)result.StatusCode} {result.StatusCode}"
            + (result.ContentRange != null ? $", {result.ContentRange}" : "")
            + $") -> {_resolvedOutFile}");
    }

    private void WriteToPipeline(GraphContentResult result, long? maxBytes, long skipBytes, object? errorTarget)
    {
        // Known-oversized before a single byte moves: refuse early.
        var expected = maxBytes ?? result.ContentLength;
        if (expected > MaxPipelineBytes)
        {
            ThrowTerminatingError(new ErrorRecord(
                new InvalidOperationException(
                    $"Content is {expected:N0} bytes; the pipeline guard is {MaxPipelineBytes:N0} (100 MB). "
                    + "Use -OutFile for large downloads, or a byte range (-First / -Offset -Length)."),
                "ContentTooLargeForPipeline", ErrorCategory.LimitsExceeded, errorTarget));
            return;
        }

        // Unknown length (chunked): enforce the guard during the copy - cap one byte over
        // the limit so hitting the cap is distinguishable from an exact-limit body.
        var copyLimit = maxBytes ?? MaxPipelineBytes + 1;
        using var buffer = new MemoryStream(expected is > 0 and <= int.MaxValue ? (int)expected : 0);
        var copied = GraphContentClient.CopyWithIdleTimeoutAsync(
                result.Content, buffer, copyLimit, GetClient().BodyReadTimeout,
                CancellationToken, skipBytes)
            .GetAwaiter().GetResult();

        if (copied > MaxPipelineBytes)
        {
            ThrowTerminatingError(new ErrorRecord(
                new InvalidOperationException(
                    $"Content exceeded the {MaxPipelineBytes:N0}-byte (100 MB) pipeline guard mid-download. "
                    + "Use -OutFile for large downloads, or a byte range (-First / -Offset -Length)."),
                "ContentTooLargeForPipeline", ErrorCategory.LimitsExceeded, errorTarget));
            return;
        }

        WriteVerbose($"{copied:N0} bytes ({(int)result.StatusCode} {result.StatusCode}"
            + (result.ContentRange != null ? $", {result.ContentRange}" : "") + ")");
        WriteObject(buffer.ToArray(), enumerateCollection: false);
    }
}
