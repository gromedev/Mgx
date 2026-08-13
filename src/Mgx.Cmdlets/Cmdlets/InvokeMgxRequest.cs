using System.Collections;
using System.Management.Automation;
using System.Net;
using System.Text;
using System.Text.Json;
using Mgx.Cmdlets.Base;
using Mgx.Engine.Http;
using Mgx.Engine.Models;
using Mgx.Engine.Pagination;
using Polly.CircuitBreaker;

namespace Mgx.Cmdlets.Cmdlets;

/// <summary>
/// Invoke-MgxRequest: General-purpose resilient client for any Microsoft Graph endpoint.
/// Supports streaming pagination, fan-out concurrency, write operations, and checkpoint/resume.
/// For bulk writes (>10 items), consider Invoke-MgxBatchRequest (measured ~1.5x faster
/// than fan-out for PATCH at 1k scale; fewer HTTP round-trips and server-side pacing).
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "MgxRequest", DefaultParameterSetName = "Direct",
    SupportsShouldProcess = true)]
[OutputType(typeof(Hashtable), typeof(string))]
public class InvokeMgxRequest : MgxCmdletBase
{
    #region Common parameters

    [Parameter(Mandatory = true, Position = 0)]
    [Alias("Resource")]
    public string Uri { get; set; } = string.Empty;

    [Parameter]
    [ValidateSet("GET", "POST", "PATCH", "PUT", "DELETE")]
    public string Method { get; set; } = "GET";

    [Parameter]
    public object? Body { get; set; }

    [Parameter]
    [Alias("Select")]
    public string[]? Property { get; set; }

    [Parameter]
    [Alias("Expand")]
    public string[]? ExpandProperty { get; set; }

    [Parameter]
    [ArgumentCompleter(typeof(ConsistencyLevelCompleter))]
    public string? ConsistencyLevel { get; set; }

    [Parameter]
    public System.Collections.Hashtable? Headers { get; set; }

    [Parameter]
    [ValidateSet("v1.0", "beta")]
    [ArgumentCompleter(typeof(ApiVersionCompleter))]
    public string ApiVersion { get; set; } = "v1.0";

    [Parameter]
    public SwitchParameter Raw { get; set; }

    #endregion

    #region List parameters

    [Parameter]
    public string? Filter { get; set; }

    [Parameter]
    [Alias("OrderBy")]
    public string[]? Sort { get; set; }

    [Parameter]
    public string? Search { get; set; }

    [Parameter]
    [ValidateRange(0, int.MaxValue)]
    public int Skip { get; set; }

    [Parameter]
    public SwitchParameter All { get; set; }

    [Parameter]
    [ValidateRange(0, int.MaxValue)]
    public int Top { get; set; }

    [Parameter]
    [ValidateRange(1, 999)]
    public int PageSize { get; set; } = 999;

    [Parameter]
    [Alias("CV")]
    public string? CountVariable { get; set; }

    [Parameter]
    public string? CheckpointPath { get; set; }

    [Parameter]
    public SwitchParameter NoPageSize { get; set; }

    #endregion

    #region Fan-out parameters

    /// <summary>
    /// Entity ID, or an object carrying one. Accepts a plain string, a Hashtable (what the
    /// Mgx cmdlets emit), or a PSCustomObject; the 'id' member is extracted in ProcessRecord.
    /// </summary>
    [Parameter(ValueFromPipeline = true, ParameterSetName = "Pipeline")]
    [Alias("Id")]
    public object? InputObject { get; set; }

    [Parameter(ParameterSetName = "Pipeline")]
    [ValidateRange(1, 128)]
    public int Concurrency { get; set; } = 5;

    [Parameter(ParameterSetName = "Pipeline")]
    public SwitchParameter SkipNotFound { get; set; }

    [Parameter(ParameterSetName = "Pipeline")]
    public SwitchParameter SkipForbidden { get; set; }

    #endregion

    private readonly List<string> _pipelineIds = [];
    private bool _isFanOut;

    /// <summary>
    /// Full base URL including API version (e.g., "https://graph.microsoft.com/v1.0").
    /// </summary>
    private string VersionedBaseUrl => $"{s_graphEndpoint}/{ApiVersion}";

    /// <summary>
    /// Whether the current invocation is a collection/list operation.
    /// </summary>
    private bool IsCollectionMode =>
        All.IsPresent || Top > 0 || !string.IsNullOrEmpty(Filter) ||
        !string.IsNullOrEmpty(Search) || Sort is { Length: > 0 } ||
        !string.IsNullOrEmpty(CountVariable) || Skip > 0;

    protected override void BeginProcessing()
    {
        _isFanOut = Uri.Contains("{id}", StringComparison.OrdinalIgnoreCase);

        // $search requires ConsistencyLevel: eventual. Error if missing (data loss otherwise)
        if (!string.IsNullOrEmpty(Search) && string.IsNullOrEmpty(ConsistencyLevel))
        {
            ThrowTerminatingError(new ErrorRecord(
                new ArgumentException(
                    "-Search requires -ConsistencyLevel eventual. Without it, Graph returns incomplete results."),
                "ConsistencyLevelRequired", ErrorCategory.InvalidArgument, Search));
            return;
        }

        // $count=true requires ConsistencyLevel: eventual on directory endpoints;
        // auto-add when -CountVariable or -Filter is used (enables count discrepancy detection)
        if ((!string.IsNullOrEmpty(CountVariable) || !string.IsNullOrEmpty(Filter))
            && string.IsNullOrEmpty(ConsistencyLevel))
        {
            ConsistencyLevel = "eventual";
            WriteVerbose("Auto-adding ConsistencyLevel:eventual header (required by -Filter/-CountVariable for $count=true).");
        }

        // $skip is not supported by most Graph directory endpoints (silently ignored)
        if (Skip > 0)
            WriteWarning("-Skip ($skip) is not supported by many Graph API endpoints (e.g., /users, /groups). The parameter may be silently ignored.");
    }

    protected override void ProcessRecord()
    {
        if (_isFanOut)
        {
            if (InputObject == null)
            {
                WriteVerbose("Skipping null pipeline input.");
                return;
            }

            var id = ResolvePipelineId(InputObject);
            if (string.IsNullOrEmpty(id))
            {
                WriteError(new ErrorRecord(
                    new ArgumentException(
                        "Pipeline input is missing an 'id' property. Pipe entity IDs, or objects with an 'id' property."),
                    "MissingPipelineId", ErrorCategory.InvalidArgument, InputObject));
                return;
            }

            _pipelineIds.Add(id);
            return;
        }

        // Direct mode (no fan-out): execute immediately
        ExecuteRequest(Uri, sourceId: null);
    }

    /// <summary>
    /// Extract the entity ID from pipeline input: a bare string, or the 'id' member of a
    /// Hashtable / PSCustomObject.
    /// </summary>
    internal static string? ResolvePipelineId(object input)
    {
        var value = UnwrapPSObject(input);

        if (value is string s)
            return s;

        return TryGetMember(value, "id")?.ToString();
    }

    protected override void EndProcessing()
    {
        try
        {
            if (_isFanOut)
            {
                if (_pipelineIds.Count == 0)
                {
                    // Error on {id} URI without pipeline input
                    ThrowTerminatingError(new ErrorRecord(
                        new ArgumentException(
                            "URI contains '{id}' placeholder but no pipeline input was provided. Pipe entity IDs to this cmdlet."),
                        "MissingPipelineInput", ErrorCategory.InvalidArgument, Uri));
                    return;
                }

                ExecuteFanOut();
            }
        }
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
        {
            WriteWarning("Request cancelled by user.");
        }
        finally
        {
            base.EndProcessing();
        }
    }

    #region Request execution

    private void ExecuteRequest(string relativeUri, string? sourceId)
    {
        // Ensure client is initialized before using VersionedBaseUrl
        // (populates s_graphEndpoint for sovereign clouds)
        GetClient();

        var httpMethod = new HttpMethod(Method.ToUpperInvariant());

        if (httpMethod == HttpMethod.Get)
        {
            // Graph GET has no request body, and neither GET path sends one
            if (Body != null)
                WriteWarning("-Body is ignored on GET requests.");

            if (IsCollectionMode)
                ExecuteList(relativeUri, sourceId);
            else
                ExecuteGet(relativeUri, sourceId);
        }
        else
        {
            ExecuteWrite(httpMethod, relativeUri, sourceId);
        }
    }

    private void ExecuteList(string relativeUri, string? sourceId)
    {
        // Track whether $count=true was auto-added (not user-requested via -CountVariable).
        // If the endpoint rejects it with 400, retry without.
        bool countAutoAdded = !string.IsNullOrEmpty(Filter) && string.IsNullOrEmpty(CountVariable);
        bool includeAutoCount = countAutoAdded;
        bool suppressTop = false;

        // Consumer-owned checkpoint: resolve path once before the retry loop
        var cpPath = CheckpointPath != null
            ? GetUnresolvedProviderPathFromPSPath(CheckpointPath)
            : null;

        // If checkpoint was saved during a previous retry (URL without $count=true),
        // match the checkpoint's URL to avoid mismatch on resume
        if (countAutoAdded && cpPath != null)
        {
            var existingCp = PaginationCheckpoint.Load(cpPath);
            if (existingCp?.Resource != null && !existingCp.Resource.Contains("$count=true"))
                includeAutoCount = false;
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            long? reportedODataCount = null;
            try
            {
                var url = BuildCollectionUrl(relativeUri,
                    includeCount: !string.IsNullOrEmpty(CountVariable) || includeAutoCount,
                    noPageSize: suppressTop);
                var iterator = new PageIterator(GetClient());
                var maxItems = (All.IsPresent || Top <= 0) ? 0 : Top;
                var headers = BuildHeaders();
                long itemCount = 0;

                ResumeState? resume = null;

                if (cpPath != null)
                {
                    var checkpoint = PaginationCheckpoint.Load(cpPath);
                    if (checkpoint != null)
                    {
                        if (checkpoint.NextLink == null)
                        {
                            // Completion marker: previous run finished
                            PaginationCheckpoint.Delete(cpPath);
                        }
                        else if (string.Equals(checkpoint.Resource, url, StringComparison.Ordinal))
                        {
                            var expectedHost = new System.Uri(url);
                            var validated = NextLinkValidator.Validate(checkpoint.NextLink, expectedHost);
                            if (validated != null && checkpoint.ItemsCollected >= 0)
                            {
                                // Page-boundary only: skipOnFirstPage = 0 because pipeline items are
                                // ephemeral (no file to dedup against). On resume, the interrupted page
                                // may re-emit items already sent downstream. Downstream consumers
                                // (e.g., Export-Csv -Append) are responsible for their own dedup.
                                resume = new ResumeState(validated, 0, checkpoint.ItemsCollected);
                            }
                            else
                            {
                                PaginationCheckpoint.Delete(cpPath);
                            }
                        }
                        else
                        {
                            PaginationCheckpoint.Delete(cpPath);
                        }
                    }
                }

                var enumerable = iterator.StreamAllWithCountAsync(
                    url,
                    maxItems,
                    count =>
                    {
                        if (!string.IsNullOrEmpty(CountVariable))
                            SessionState.PSVariable.Set(CountVariable, count);
                        reportedODataCount = count;
                    },
                    headers,
                    resume: resume,
                    onPageComplete: info =>
                    {
                        // Save page-boundary checkpoint
                        if (cpPath != null && info.NextPageUrl != null)
                        {
                            try
                            {
                                new PaginationCheckpoint
                                {
                                    Resource = url,
                                    NextLink = info.NextPageUrl,
                                    ItemsCollected = itemCount + (resume?.ItemsAlreadyCollected ?? 0)
                                }.Save(cpPath);
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            {
                                WriteWarning($"Checkpoint save failed: {ex.Message}");
                            }
                        }
                    },
                    cancellationToken: CancellationToken);

                var enumerator = enumerable.GetAsyncEnumerator(CancellationToken);
                try
                {
                    while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                    {
                        DrainClientMessages();
                        itemCount++;
                        OutputItem(enumerator.Current, sourceId);
                    }
                }
                catch (PipelineStoppedException)
                {
                    // Pipeline consumer is done (e.g., Select-Object -First N); stop gracefully
                    throw;
                }
                finally
                {
                    enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }

                // Warn if actual items significantly fewer than @odata.count
                if (reportedODataCount.HasValue && maxItems == 0 && resume == null)
                    WriteCountDiscrepancyWarning(relativeUri, reportedODataCount.Value, itemCount, Filter);

                // Delete checkpoint on successful completion
                if (cpPath != null) PaginationCheckpoint.Delete(cpPath);
                return; // Success, exit the retry loop
            }
            catch (PipelineStoppedException)
            {
                throw;
            }
            catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
            {
                WriteWarning("Request cancelled by user.");
                return;
            }
            catch (GraphServiceException ex) when (includeAutoCount && countAutoAdded && ex.StatusCode == HttpStatusCode.BadRequest)
            {
                // Auto-added $count=true rejected by this endpoint; retry without it
                WriteVerbose("Endpoint rejected $count=true (HTTP 400). Retrying without count parameter.");
                includeAutoCount = false;
                continue;
            }
            catch (GraphServiceException ex) when (
                !suppressTop
                && !NoPageSize.IsPresent
                && ex.StatusCode == HttpStatusCode.BadRequest
                && string.Equals(ex.ErrorCode, "Request_UnsupportedQuery", StringComparison.OrdinalIgnoreCase))
            {
                // Endpoint doesn't support $top (e.g., /directoryRoles). Retry without page size.
                WriteVerbose("Endpoint rejected $top (Request_UnsupportedQuery). Retrying without page size.");
                suppressTop = true;
                continue;
            }
            catch (GraphServiceException ex) when (ShouldSkipGraphError(ex))
            {
                return;
            }
            catch (Exception ex) when (ex is GraphServiceException or BrokenCircuitException or HttpRequestException)
            {
                WriteGraphError(ex, relativeUri, ApiVersion);
                return;
            }
        }
    }

    private void ExecuteGet(string relativeUri, string? sourceId)
    {
        try
        {
            var url = BuildGetUrl(relativeUri);
            var client = GetClient();
            var headers = BuildHeaders();

            using var response = client.GetAsync(url, CancellationToken, headers)
                .GetAwaiter().GetResult();
            DrainClientMessages();

            if (!response.IsSuccessStatusCode)
            {
                var body = response.Content.ReadAsStringAsync(CancellationToken).GetAwaiter().GetResult();
                throw new GraphServiceException(response.StatusCode, body);
            }

            using var stream = response.Content.ReadAsStreamAsync(CancellationToken).GetAwaiter().GetResult();
            var json = JsonSerializer.DeserializeAsync<JsonElement>(stream, cancellationToken: CancellationToken)
                .AsTask().GetAwaiter().GetResult();

            OutputPayload(json, sourceId);
        }
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
        {
            WriteWarning("Request cancelled by user.");
        }
        catch (GraphServiceException ex) when (ShouldSkipGraphError(ex))
        {
            DrainClientMessages();
        }
        catch (Exception ex) when (ex is GraphServiceException or BrokenCircuitException or HttpRequestException)
        {
            WriteGraphError(ex, relativeUri, ApiVersion);
        }
    }

    private void ExecuteWrite(HttpMethod method, string relativeUri, string? sourceId)
    {
        if (!ShouldProcess(relativeUri, method.Method))
            return;

        try
        {
            var url = $"{VersionedBaseUrl}{NormalizePath(relativeUri)}";
            var client = GetClient();
            var headers = BuildHeaders();

            var serializedBody = ResolveRequestBody(method);
            HttpContent? content = serializedBody != null
                ? new StringContent(serializedBody, Encoding.UTF8, "application/json")
                : null;

            try
            {
                using var response = client.SendAsync(method, url, content, headers, CancellationToken)
                    .GetAwaiter().GetResult();
                DrainClientMessages();

                if (!response.IsSuccessStatusCode)
                {
                    var body = response.Content.ReadAsStringAsync(CancellationToken).GetAwaiter().GetResult();
                    throw new GraphServiceException(response.StatusCode, body);
                }

                // DELETE typically returns 204 No Content
                if (response.StatusCode == HttpStatusCode.NoContent)
                    return;

                // stream.Length throws NotSupportedException on network/decompression streams.
                // Read as bytes to safely handle null ContentLength (chunked transfer) and empty bodies.
                var bodyBytes = response.Content.ReadAsByteArrayAsync(CancellationToken).GetAwaiter().GetResult();
                if (bodyBytes.Length > 0)
                {
                    var jsonEl = JsonSerializer.Deserialize<JsonElement>(bodyBytes);
                    OutputPayload(jsonEl, sourceId);
                }
            }
            finally
            {
                content?.Dispose();
            }
        }
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
        {
            WriteWarning("Request cancelled by user.");
        }
        catch (GraphServiceException ex) when (ShouldSkipGraphError(ex))
        {
            DrainClientMessages();
        }
        catch (Exception ex) when (ex is GraphServiceException or BrokenCircuitException or HttpRequestException)
        {
            WriteGraphError(ex, relativeUri, ApiVersion);
        }
    }

    #endregion

    #region Fan-out

    private void ExecuteFanOut()
    {
        // -CountVariable with multi-ID fan-out is ambiguous
        if (!string.IsNullOrEmpty(CountVariable) && _pipelineIds.Count > 1)
        {
            ThrowTerminatingError(new ErrorRecord(
                new ArgumentException(
                    "-CountVariable is not supported with multi-ID fan-out. The count would be ambiguous across multiple entities."),
                "CountVariableNotSupported", ErrorCategory.InvalidArgument, CountVariable));
            return;
        }

        if (_pipelineIds.Count == 1)
        {
            // Single ID: direct execution, no ConcurrentFanOut overhead
            var resolved = ResolveTemplate(_pipelineIds[0]);
            ExecuteRequest(resolved, _pipelineIds[0]);
            return;
        }

        // Deduplicate pipeline IDs to avoid dict key collision and redundant HTTP calls
        var uniqueIds = _pipelineIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (uniqueIds.Count < _pipelineIds.Count)
            WriteVerbose($"Deduplicated {_pipelineIds.Count} pipeline IDs to {uniqueIds.Count} unique IDs.");

        // Ensure client is initialized (populates s_graphEndpoint for sovereign clouds)
        var client = GetClient();
        var fanOut = new ConcurrentFanOut(client, Concurrency);
        var headers = BuildHeaders();

        // Route write methods to bulk write fan-out
        var httpMethod = new HttpMethod(Method.ToUpperInvariant());
        if (httpMethod != HttpMethod.Get)
        {
            ExecuteWriteFanOut(fanOut, uniqueIds, headers, httpMethod);
            return;
        }

        // Branch on collection vs entity mode
        if (IsCollectionMode)
        {
            ExecuteCollectionFanOut(fanOut, uniqueIds, headers);
        }
        else
        {
            ExecuteEntityFanOut(fanOut, uniqueIds, headers);
        }
    }

    /// <summary>
    /// Collection fan-out: each ID resolves to a collection endpoint (e.g., /groups/{id}/members).
    /// Uses FetchAllAsync which calls GetCollectionPageAsync (expects "value" array).
    /// </summary>
    private void ExecuteCollectionFanOut(ConcurrentFanOut fanOut, List<string> uniqueIds, Dictionary<string, string>? headers)
    {
        var urls = uniqueIds.Select(id => BuildCollectionUrl(ResolveTemplate(id), includeCount: false)).ToList();

        // Map URL → sourceId for correlation
        var urlToSourceId = new Dictionary<string, string>();
        for (int i = 0; i < uniqueIds.Count; i++)
            urlToSourceId[urls[i]] = uniqueIds[i];

        // Respect -Top limit per URL
        var maxItems = (All.IsPresent || Top <= 0) ? 0 : Top;

        // Pass headers to FetchAllAsync
        var fanOutResult = fanOut.FetchAllAsync(urls, maxItems, headers, CancellationToken)
            .GetAwaiter().GetResult();
        DrainClientMessages();

        int totalItems = 0;
        foreach (var (url, items) in fanOutResult.Results)
        {
            var sourceId = urlToSourceId.GetValueOrDefault(url);
            foreach (var item in items)
            {
                totalItems++;
                OutputItem(item, sourceId);
            }
        }

        HandleFanOutErrors(fanOutResult.Errors);
    }

    /// <summary>
    /// Entity fan-out: each ID resolves to a single entity endpoint (e.g., /users/{id}).
    /// Uses ForEachAsync with GetAsync per entity since the response is a flat object, not a collection.
    /// </summary>
    private void ExecuteEntityFanOut(ConcurrentFanOut fanOut, List<string> uniqueIds, Dictionary<string, string>? headers)
    {
        // Clear results from any previous invocation
        _entityFanOutResults.Clear();

        int totalItems = 0;
        var client = GetClient();

        try
        {
            var errors = fanOut.ForEachAsync(
                uniqueIds,
                async (id, ct) =>
                {
                    var resolved = ResolveTemplate(id);
                    var url = BuildGetUrl(resolved);
                    using var response = await client.GetAsync(url, ct, headers);

                    if (!response.IsSuccessStatusCode)
                    {
                        var body = await response.Content.ReadAsStringAsync(ct);
                        throw new GraphServiceException(response.StatusCode, body);
                    }

                    using var stream = await response.Content.ReadAsStreamAsync(ct);
                    var json = await JsonSerializer.DeserializeAsync<JsonElement>(stream, cancellationToken: ct);

                    // Clone JsonElement to detach from the parent JsonDocument's buffer.
                    // Without Clone(), the JsonElement holds a reference to the document's internal
                    // memory, which could become invalid after the response stream is disposed.
                    var cloned = json.Clone();

                    // Must marshal back to the cmdlet thread for WriteObject
                    // ConcurrentFanOut collects results; we output them after
                    // Store in a thread-safe structure
                    lock (_entityFanOutResults)
                    {
                        _entityFanOutResults.Add((id, cloned));
                    }
                },
                CancellationToken).GetAwaiter().GetResult();
            DrainClientMessages();

            // Output results on the cmdlet thread
            foreach (var (sourceId, json) in _entityFanOutResults)
            {
                totalItems++;
                OutputItem(json, sourceId);
            }

            HandleFanOutErrors(errors);
            }
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
        {
            WriteWarning("Entity fan-out cancelled by user.");
        }
    }

    private readonly List<(string sourceId, JsonElement json)> _entityFanOutResults = [];

    /// <summary>
    /// Write fan-out: execute POST/PATCH/PUT/DELETE for each piped ID concurrently.
    /// Same body is applied to all operations. URIs are resolved via {id} template.
    /// </summary>
    private void ExecuteWriteFanOut(ConcurrentFanOut fanOut, List<string> uniqueIds, Dictionary<string, string>? headers, HttpMethod httpMethod)
    {
        if (!ShouldProcess($"{httpMethod.Method} {uniqueIds.Count} items via {Uri}", "Bulk write"))
            return;

        try
        {
            // Serialize body once (shared across all operations)
            string? serializedBody = ResolveRequestBody(httpMethod);

            // Build operations list: (id, resolved URL)
            var operations = uniqueIds.Select(id =>
            {
                var resolved = ResolveTemplate(id);
                var url = $"{VersionedBaseUrl}{NormalizePath(resolved)}";
                return (id, url);
            }).ToList();

            var telemetryBefore = MgxTelemetryCollector.Current.GetSummary();

            var result = fanOut.BulkWriteAsync(
                httpMethod,
                operations,
                serializedBody,
                headers,
                onProgress: null, // WriteProgress cannot be called from background threads
                CancellationToken).GetAwaiter().GetResult();
            DrainClientMessages();

            // Output response bodies (created/updated entities)
            foreach (var (sourceId, json) in result.Responses)
            {
                OutputPayload(json, sourceId);
            }

            // Handle errors with SkipNotFound/SkipForbidden filtering
            HandleBulkWriteErrors(result.Errors);

            // Summary with timing breakdown
            if (result.Succeeded > 0 || result.Failed > 0)
            {
                var telemetryAfter = MgxTelemetryCollector.Current.GetSummary();
                var elapsedSec = result.ElapsedMs / 1000.0;
                var throttles = telemetryAfter.ThrottleRetries - telemetryBefore.ThrottleRetries;
                var retryDelayMs = telemetryAfter.RetryDelayMs - telemetryBefore.RetryDelayMs;
                var rateLimiterMs = telemetryAfter.RateLimiterWaitMs - telemetryBefore.RateLimiterWaitMs;
                var httpMs = telemetryAfter.HttpMs - telemetryBefore.HttpMs;
                var throughput = result.ElapsedMs > 0 ? result.Succeeded / elapsedSec : 0;

                var summary = $"Bulk {Method}: {result.Succeeded} succeeded, {result.Failed} failed in {elapsedSec:F1}s ({throughput:F1}/sec)";
                if (throttles > 0 || retryDelayMs > 0 || rateLimiterMs > 0)
                {
                    var parts = new System.Collections.Generic.List<string>();
                    if (httpMs > 0) parts.Add($"HTTP {httpMs / 1000.0:F1}s");
                    if (retryDelayMs > 0) parts.Add($"throttle wait {retryDelayMs / 1000.0:F1}s ({throttles} 429s)");
                    if (rateLimiterMs > 0) parts.Add($"rate-limiter {rateLimiterMs / 1000.0:F1}s");
                    summary += $" | {string.Join(", ", parts)}";
                }
                WriteVerbose(summary);
            }

        }
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
        {
            WriteWarning("Bulk write cancelled by user.");
        }
    }

    private void HandleBulkWriteErrors(IReadOnlyList<BulkWriteError> errors)
    {
        int skipped404 = 0;
        int skipped403 = 0;
        bool has404 = false;
        foreach (var error in errors)
        {
            var statusCode = (HttpStatusCode)error.StatusCode;

            if (statusCode == HttpStatusCode.NotFound)
                has404 = true;

            if (SkipNotFound.IsPresent && statusCode == HttpStatusCode.NotFound)
            {
                skipped404++;
                continue;
            }
            if (SkipForbidden.IsPresent && statusCode == HttpStatusCode.Forbidden)
            {
                skipped403++;
                continue;
            }

            var ex = new InvalidOperationException($"HTTP {error.StatusCode} for '{error.Id}': {error.Message}");
            // StatusCode 0 = infrastructure exception (network, circuit breaker, deserialization)
            var (errorId, category) = error.StatusCode == 0
                ? ("BulkWriteInfraError", ErrorCategory.ConnectionError)
                : ("BulkWriteError", MapStatusToCategory(statusCode));
            WriteError(new ErrorRecord(ex, errorId, category, error.Id));
        }

        if (has404)
            WriteBetaHintIfApplicable(HttpStatusCode.NotFound, ApiVersion);

        WriteSkipSummaryWarning(skipped404, skipped403, "operations");
    }

    private void HandleFanOutErrors(Dictionary<string, Exception> errors)
    {
        int skipped404 = 0;
        int skipped403 = 0;
        bool has404 = false;
        foreach (var (key, ex) in errors)
        {
            var statusCode = GetStatusCodeFromException(ex);

            if (statusCode == HttpStatusCode.NotFound)
                has404 = true;

            if (SkipNotFound.IsPresent && statusCode == HttpStatusCode.NotFound)
            {
                skipped404++;
                continue;
            }
            if (SkipForbidden.IsPresent && statusCode == HttpStatusCode.Forbidden)
            {
                skipped403++;
                continue;
            }

            // Preserve diagnostic specificity for infrastructure exceptions
            var (errorId, category) = ex switch
            {
                Polly.CircuitBreaker.BrokenCircuitException => ("CircuitBroken", ErrorCategory.ResourceUnavailable),
                HttpRequestException => ("HttpError", ErrorCategory.ConnectionError),
                _ => ("FanOutError", statusCode.HasValue ? MapStatusToCategory(statusCode.Value) : ErrorCategory.NotSpecified)
            };
            WriteError(new ErrorRecord(ex, errorId, category, key));
        }

        if (has404)
            WriteBetaHintIfApplicable(HttpStatusCode.NotFound, ApiVersion);

        WriteSkipSummaryWarning(skipped404, skipped403, "entities");
    }

    private void WriteSkipSummaryWarning(int skipped404, int skipped403, string noun)
    {
        var total = skipped404 + skipped403;
        if (total == 0) return;
        var reasons = new List<string>();
        if (skipped404 > 0) reasons.Add("404 (Not Found)");
        if (skipped403 > 0) reasons.Add("403 (Forbidden)");
        WriteWarning($"Skipped {total} {noun} due to {string.Join(" and ", reasons)} responses.");
    }

    private string ResolveTemplate(string id)
    {
        // Replace {id} (case-insensitive) with the escaped entity ID
        return System.Text.RegularExpressions.Regex.Replace(
            Uri, @"\{id\}", System.Uri.EscapeDataString(id),
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static HttpStatusCode? GetStatusCodeFromException(Exception ex)
    {
        if (ex is GraphServiceException gse) return gse.StatusCode;
        if (ex is HttpRequestException hre && hre.StatusCode.HasValue) return hre.StatusCode.Value;
        return null;
    }

    /// <summary>
    /// Check if a GraphServiceException should be silently skipped based on
    /// -SkipNotFound / -SkipForbidden switches. Used by single-request paths
    /// (ExecuteGet, ExecuteWrite, ExecuteList) so that these switches work
    /// consistently regardless of whether the pipeline has 1 or N items.
    /// </summary>
    private bool ShouldSkipGraphError(GraphServiceException ex)
    {
        if (SkipNotFound.IsPresent && ex.StatusCode == HttpStatusCode.NotFound)
        {
            WriteVerbose($"Skipping 404 (Not Found) for request: {ex.Message}");
            return true;
        }
        if (SkipForbidden.IsPresent && ex.StatusCode == HttpStatusCode.Forbidden)
        {
            WriteVerbose($"Skipping 403 (Forbidden) for request: {ex.Message}");
            return true;
        }
        return false;
    }

    #endregion

    #region URL building

    private string BuildCollectionUrl(string relativeUri) => BuildCollectionUrl(relativeUri,
        includeCount: !string.IsNullOrEmpty(CountVariable) || !string.IsNullOrEmpty(Filter));

    private string BuildCollectionUrl(string relativeUri, bool includeCount, bool noPageSize = false) => BuildListUrl(
        VersionedBaseUrl, relativeUri,
        new ODataListParams(NoPageSize.IsPresent || noPageSize, Top, PageSize, Filter,
            Property, Sort, Search, Skip, ExpandProperty,
            IncludeCount: includeCount));

    private string BuildGetUrl(string relativeUri)
    {
        var baseUrl = $"{VersionedBaseUrl}{NormalizePath(relativeUri)}";
        var queryParams = new List<string>();

        if (Property is { Length: > 0 })
            queryParams.Add($"$select={System.Uri.EscapeDataString(string.Join(",", Property))}");

        if (ExpandProperty is { Length: > 0 })
            queryParams.Add($"$expand={System.Uri.EscapeDataString(string.Join(",", ExpandProperty))}");

        if (queryParams.Count == 0)
            return baseUrl;

        // If URI already contains query parameters, append with & instead of ?
        var separator = baseUrl.Contains('?') ? "&" : "?";
        return $"{baseUrl}{separator}{string.Join("&", queryParams)}";
    }

    #endregion

    #region Helpers

    private Dictionary<string, string>? BuildHeaders() =>
        BuildRequestHeaders(ConsistencyLevel, Headers);

    /// <summary>
    /// Emit a Graph response payload. A collection envelope ({"value":[...]}, returned by GET and by
    /// action endpoints such as /directoryObjects/getByIds) is unwrapped into one item per element;
    /// anything else is emitted whole.
    /// </summary>
    private void OutputPayload(JsonElement json, string? sourceId)
    {
        var items = TryUnwrapCollection(json, out var truncated);
        if (items == null)
        {
            OutputItem(json, sourceId);
            return;
        }

        if (truncated)
            WriteWarning("Response contains more items. Use -All to retrieve all pages, or -Top to limit results.");

        foreach (var item in items)
            OutputItem(item, sourceId);
    }

    /// <summary>
    /// The elements of a Graph collection envelope ({"value":[...]}), or null when the payload is a
    /// single entity. <paramref name="truncated"/> reports whether the envelope carried @odata.nextLink.
    /// The gate is structural: an entity whose own 'value' property happens to be an array is
    /// indistinguishable from an envelope and unwraps too (use -Raw to see such a payload whole).
    /// </summary>
    internal static List<JsonElement>? TryUnwrapCollection(JsonElement json, out bool truncated)
    {
        truncated = false;

        if (json.ValueKind != JsonValueKind.Object
            || !json.TryGetProperty("value", out var valueArray)
            || valueArray.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        truncated = json.TryGetProperty("@odata.nextLink", out _);
        return valueArray.EnumerateArray().ToList();
    }

    private void OutputItem(JsonElement element, string? sourceId)
    {
        if (Raw.IsPresent)
        {
            WriteObject(element.GetRawText());
            return;
        }

        var ht = JsonToHashtable(element);

        if (sourceId != null)
        {
            // Unique prefix avoids collision with Graph entity properties. The indexer
            // overwrites, so a repeated key does not need removing first.
            ht["_MgxSourceId"] = sourceId;
        }

        // Single-argument WriteObject does not enumerate, so the Hashtable is emitted whole
        WriteObject(ht);
    }

    /// <summary>
    /// Serialized request body for a write method, or null when no content should be sent.
    /// Graph requires Content-Type: application/json on POST/PATCH/PUT even with an empty body,
    /// so those default to "{}". DELETE sends no content.
    /// </summary>
    private string? ResolveRequestBody(HttpMethod method)
    {
        var serialized = Body != null ? SerializeBody(Body) : null;
        if (!string.IsNullOrWhiteSpace(serialized))
            return serialized;

        return method != HttpMethod.Delete ? "{}" : null;
    }

    /// <summary>
    /// Serialize a -Body argument to the JSON that goes on the wire. A raw JSON string is passed
    /// through verbatim, everything else is serialized.
    /// </summary>
    internal static string SerializeBody(object body)
    {
        var value = UnwrapPSObject(body);
        if (value is string s) return s;
        // Still a PSObject after unwrapping: a PSCustomObject, whose members live on the wrapper
        if (value is PSObject pso) return JsonSerializer.Serialize(PSOToDict(pso));
        if (value is IDictionary dict) return JsonSerializer.Serialize(DictionaryToDict(dict));
        // Handle array body (object[], ArrayList, List<object>, ... from PowerShell)
        if (value is IEnumerable seq) return JsonSerializer.Serialize(EnumerableToArray(seq));
        return JsonSerializer.Serialize(value);
    }

    internal static Dictionary<string, object?> PSOToDict(PSObject pso)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var prop in pso.Properties)
        {
            if (prop.MemberType == PSMemberTypes.NoteProperty)
                dict[prop.Name] = UnwrapValue(prop.Value);
        }
        return dict;
    }

    /// <summary>
    /// Flatten any IDictionary (Hashtable or ordered dictionary) into a serializable
    /// dictionary, unwrapping nested PowerShell values.
    /// </summary>
    internal static Dictionary<string, object?> DictionaryToDict(IDictionary source)
    {
        var dict = new Dictionary<string, object?>();
        foreach (DictionaryEntry entry in source)
            dict[entry.Key.ToString()!] = UnwrapValue(entry.Value);
        return dict;
    }

    /// <summary>
    /// Unwraps a PowerShell value to its underlying .NET representation.
    /// A PSCustomObject keeps its members on the PSObject (its BaseObject is an empty
    /// marker), so it must be read through PSOToDict rather than its BaseObject.
    /// </summary>
    internal static object? UnwrapValue(object? value)
    {
        if (value is PSObject pso)
        {
            var unwrapped = UnwrapPSObject(pso);
            return ReferenceEquals(unwrapped, pso) ? PSOToDict(pso) : UnwrapValue(unwrapped);
        }
        if (value is IDictionary dict)
            return DictionaryToDict(dict);
        if (value is IEnumerable seq and not string)
            return EnumerableToArray(seq);
        return value;
    }

    /// <summary>
    /// Flatten any non-string sequence (object[], ArrayList, List&lt;object&gt;, ...) into an array of
    /// unwrapped values.
    /// </summary>
    private static object?[] EnumerableToArray(IEnumerable source) =>
        source.Cast<object?>().Select(UnwrapValue).ToArray();

    #endregion

}
