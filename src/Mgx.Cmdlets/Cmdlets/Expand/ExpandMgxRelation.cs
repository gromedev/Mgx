using System.Collections;
using System.Collections.Concurrent;
using System.Management.Automation;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mgx.Cmdlets.Base;
using Mgx.Engine.Http;
using Mgx.Engine.Models;
using Mgx.Engine.Pagination;
using Polly.CircuitBreaker;

namespace Mgx.Cmdlets.Cmdlets.Expand;

/// <summary>
/// Expand-MgxRelation: Enriches Graph objects with related data via a template URI.
/// Buffers pipeline input, fans out concurrent requests, and attaches results as a new property.
/// Handles both collection endpoints (returning {"value": [...]}) and singleton endpoints
/// (returning a flat object, e.g. /users/{id}/manager) automatically.
/// Buffers all input before issuing requests, so use upstream filtering
/// (-Top, -Filter) on the source cmdlet rather than downstream Select-Object -First.
/// </summary>
[Cmdlet(VerbsData.Expand, "MgxRelation")]
[OutputType(typeof(Hashtable))]
public class ExpandMgxRelation : MgxCmdletBase
{
    /// <summary>
    /// Object to enrich. Accepts a Hashtable (what the Mgx cmdlets emit) or a PSCustomObject;
    /// the relation is attached in the same shape the object arrived in.
    /// </summary>
    [Parameter(Mandatory = true, ValueFromPipeline = true)]
    public object InputObject { get; set; } = null!;

    [Parameter(Mandatory = true, Position = 0)]
    public string Uri { get; set; } = string.Empty;

    [Parameter(Mandatory = true, Position = 1)]
    public string As { get; set; } = string.Empty;

    [Parameter]
    public SwitchParameter Flatten { get; set; }

    [Parameter]
    public string IdProperty { get; set; } = "id";

    [Parameter]
    [ValidateRange(1, 128)]
    public int Concurrency { get; set; } = 5;

    [Parameter]
    public SwitchParameter SkipNotFound { get; set; }

    [Parameter]
    public SwitchParameter SkipForbidden { get; set; }

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
    [ValidateRange(1, int.MaxValue)]
    public int Top { get; set; }

    private readonly List<object> _buffer = [];

    private static readonly Regex IdPlaceholder = new(
        @"\{id\}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private string VersionedBaseUrl => $"{s_graphEndpoint}/{ApiVersion}";

    protected override void BeginProcessing()
    {
        base.BeginProcessing();

        if (!Uri.Contains("{id}", StringComparison.OrdinalIgnoreCase))
        {
            ThrowTerminatingError(new ErrorRecord(
                new ArgumentException("The -Uri parameter must contain a '{id}' placeholder."),
                "MissingIdPlaceholder",
                ErrorCategory.InvalidArgument,
                Uri));
        }

        // $search requires ConsistencyLevel: eventual. Error if missing (silent data loss otherwise).
        // Matches validation in InvokeMgxRequest and InvokeMgxBatchRequest.
        if (Uri.Contains("$search", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(ConsistencyLevel))
        {
            ThrowTerminatingError(new ErrorRecord(
                new ArgumentException(
                    "-Uri contains $search, which requires -ConsistencyLevel eventual. " +
                    "Without it, Graph returns incomplete results."),
                "ConsistencyLevelRequired",
                ErrorCategory.InvalidArgument,
                Uri));
        }
    }

    protected override void ProcessRecord()
    {
        _buffer.Add(InputObject);
        if (_buffer.Count == 50_000)
            WriteWarning(
                "Buffered 50,000+ objects in memory. All input is held until fan-out completes. " +
                "Consider filtering upstream with -Filter or -Top to reduce memory usage.");
    }

    protected override void EndProcessing()
    {
        if (_buffer.Count == 0)
        {
            base.EndProcessing();
            return;
        }

        try
        {
            ExecuteFanOut();
        }
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
        {
            WriteWarning("Expand-MgxRelation cancelled by user.");
        }

        base.EndProcessing();
    }

    private void ExecuteFanOut()
    {
        var client = GetClient();
        var fanOut = new ConcurrentFanOut(client, Concurrency);
        var headers = BuildRequestHeaders(ConsistencyLevel, Headers);

        // Extract IDs, dedup, track objects missing IdProperty
        var seen = new HashSet<string>();
        var uniqueIds = new List<string>();

        foreach (var obj in _buffer)
        {
            var idValue = TryGetMember(obj, IdProperty);
            if (idValue == null)
            {
                WriteError(new ErrorRecord(
                    new ArgumentException($"Input object is missing the '{IdProperty}' property."),
                    "MissingIdProperty",
                    ErrorCategory.InvalidArgument,
                    obj));
                // Object will still be output with null relation (not silently dropped)
                continue;
            }

            var id = idValue.ToString() ?? string.Empty;
            if (seen.Add(id))
                uniqueIds.Add(id);
        }

        if (uniqueIds.Count == 0)
        {
            // Output objects that had missing IdProperty with null relation
            OutputBufferedObjects(new Dictionary<string, JsonElement[]>());
            return;
        }

        // Build URLs from template
        var urls = uniqueIds.Select(BuildUrl).ToList();

        // Map URL to source ID for correlation
        var urlToId = new Dictionary<string, string>();
        for (int i = 0; i < uniqueIds.Count; i++)
            urlToId[urls[i]] = uniqueIds[i];

        WriteVerbose($"Expand-MgxRelation: fetching '{As}' for {uniqueIds.Count} objects (concurrency: {Concurrency})");

        // Fan-out with auto-detection of collection vs singleton responses.
        // Cannot use FetchAllAsync because it assumes collection endpoints ({"value": [...]}).
        // Singleton endpoints (/users/{id}/manager) return flat objects and would silently
        // produce empty results. ForEachAsync lets us inspect each response and handle both.
        var results = new ConcurrentDictionary<string, JsonElement[]>();
        var errors = fanOut.ForEachAsync(
            urls,
            async (url, ct) =>
            {
                using var response = await client.GetAsync(url, ct, headers);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync(ct);
                    throw new GraphServiceException(response.StatusCode, errorBody);
                }

                using var stream = await response.Content.ReadAsStreamAsync(ct);
                var json = await JsonSerializer.DeserializeAsync<JsonElement>(stream, cancellationToken: ct);
                var root = json.Clone();

                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("value", out var valueEl)
                    && valueEl.ValueKind == JsonValueKind.Array)
                {
                    // Collection response: extract items and follow nextLink for pagination
                    var items = new List<JsonElement>();
                    foreach (var item in valueEl.EnumerateArray())
                        items.Add(item.Clone());

                    // Respect -Top: stop pagination once we have enough items per relation
                    var maxItems = Top > 0 ? Top : 0;
                    if (maxItems > 0 && items.Count >= maxItems)
                    {
                        results[url] = items.Take(maxItems).ToArray();
                        return;
                    }

                    string? nextLink = root.TryGetProperty("@odata.nextLink", out var nl)
                        ? nl.GetString() : null;
                    Uri? expectedHost = System.Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                        ? parsed : null;
                    nextLink = NextLinkValidator.ValidateOrThrow(nextLink, expectedHost);

                    int consecutiveEmptyPages = 0;
                    while (nextLink != null)
                    {
                        ct.ThrowIfCancellationRequested();
                        var page = await client.GetCollectionPageAsync(nextLink, ct, headers);

                        if (page.Value.Length == 0)
                        {
                            consecutiveEmptyPages++;
                            if (consecutiveEmptyPages >= 3)
                                break;
                        }
                        else
                        {
                            consecutiveEmptyPages = 0;
                        }

                        items.AddRange(page.Value);

                        if (maxItems > 0 && items.Count >= maxItems)
                        {
                            items = items.Take(maxItems).ToList();
                            break;
                        }

                        nextLink = NextLinkValidator.ValidateOrThrow(page.NextLink, expectedHost);
                    }

                    results[url] = items.ToArray();
                }
                else
                {
                    // Singleton response (e.g., /users/{id}/manager): wrap as single-element array
                    results[url] = [root];
                }
            },
            CancellationToken).GetAwaiter().GetResult();
        DrainClientMessages();

        // Build results dictionary keyed by ID
        var resultsById = new Dictionary<string, JsonElement[]>();
        foreach (var (url, items) in results)
        {
            var id = urlToId[url];
            resultsById[id] = items;
        }

        HandleFanOutErrors(errors, urlToId);
        OutputBufferedObjects(resultsById);

    }

    /// <summary>
    /// Output all buffered objects with the relation property attached.
    /// Objects missing IdProperty or with errored IDs get null for the relation.
    /// Preserves original pipeline order.
    /// </summary>
    private void OutputBufferedObjects(Dictionary<string, JsonElement[]> resultsById)
    {
        // Cache converted results per ID to avoid redundant JsonToHashtable calls
        // when multiple buffer objects share the same ID
        HashSet<string>? flattenWarned = Flatten.IsPresent ? [] : null;

        foreach (var obj in _buffer)
        {
            var id = TryGetMember(obj, IdProperty)?.ToString();

            object? relationValue = null;

            if (id != null && resultsById.ContainsKey(id))
            {
                // Converted per input object rather than once per id. Two inputs carrying the
                // same id used to receive the SAME hashtables, so writing to one output's
                // relation silently rewrote the others'. The conversion only repeats when an id
                // actually appears twice, which is the same case that made the sharing visible.
                var items = resultsById[id];
                var converted = items.Select(JsonToHashtable).ToArray();

                if (Flatten.IsPresent)
                {
                    if (converted.Length <= 1)
                    {
                        relationValue = converted.Length == 1 ? converted[0] : null;
                    }
                    else
                    {
                        if (flattenWarned!.Add(id))
                            WriteWarning($"-Flatten: entity '{id}' returned {converted.Length} items instead of 1. Returning array.");
                        relationValue = converted;
                    }
                }
                else
                {
                    relationValue = converted;
                }
            }

            // Attach the relation in whatever shape the object arrived in, so hashtables
            // from the Mgx cmdlets and user-supplied PSCustomObjects both round-trip.
            var target = UnwrapPSObject(obj);

            if (target is IDictionary dict)
            {
                dict[As] = relationValue;
            }
            else if (target is PSObject pso)
            {
                // Remove existing property if present (same pattern as _MgxSourceId in InvokeMgxRequest)
                if (pso.Properties[As] != null)
                    pso.Properties.Remove(As);

                pso.Properties.Add(new PSNoteProperty(As, relationValue));
            }
            else
            {
                // Plain .NET object with no writable member store
                // wrap it so the relation sticks
                var wrapped = PSObject.AsPSObject(target);
                if (wrapped.Properties[As] != null)
                    wrapped.Properties.Remove(As);

                wrapped.Properties.Add(new PSNoteProperty(As, relationValue));
                target = wrapped;
            }

            WriteObject(target);
        }
    }

    private string BuildUrl(string id)
    {
        var resolved = IdPlaceholder.Replace(Uri, System.Uri.EscapeDataString(id));
        var url = $"{VersionedBaseUrl}{NormalizePath(resolved)}";

        // Pass $top to Graph so it returns only the items we need, avoiding full-page downloads
        // when only a few items are wanted. Skipped when the caller already wrote one into
        // -Uri: Graph rejects a URL carrying $top twice, which turned -Top into a 400 for
        // every input object.
        // The client-side truncation in the lambda enforces -Top either way.
        if (Top > 0 && !HasTopQueryOption(url))
        {
            var separator = url.Contains('?') ? "&" : "?";
            url = $"{url}{separator}$top={Top}";
        }

        return url;
    }

    /// <summary>True when the URL already carries a $top query option.</summary>
    private static bool HasTopQueryOption(string url)
    {
        var q = url.IndexOf('?');
        if (q < 0) return false;
        foreach (var part in url[(q + 1)..].Split('&'))
        {
            var name = part.Split('=', 2)[0];
            if (string.Equals(name, "$top", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private void HandleFanOutErrors(
        Dictionary<string, Exception> errors,
        Dictionary<string, string> urlToId)
    {
        int skipped404 = 0;
        int skipped403 = 0;

        foreach (var (url, ex) in errors)
        {
            var id = urlToId.GetValueOrDefault(url, url);
            var statusCode = GetStatusCodeFromException(ex);

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

            var (errorId, category) = ex switch
            {
                BrokenCircuitException bce => ("CircuitBroken", bce.InnerException is GraphServiceException inner
                    ? MapStatusToCategory(inner.StatusCode)
                    : ErrorCategory.ResourceUnavailable),
                HttpRequestException => ("HttpError", ErrorCategory.ConnectionError),
                _ => ("ExpandRelationError", statusCode.HasValue
                    ? MapStatusToCategory(statusCode.Value)
                    : ErrorCategory.NotSpecified)
            };
            Exception reportEx = ex is BrokenCircuitException
                ? new InvalidOperationException(CircuitBreakerMessage, ex) : ex;
            WriteError(new ErrorRecord(reportEx, errorId, category, id));
        }

        int skippedTotal = skipped404 + skipped403;
        if (skippedTotal > 0)
        {
            var reasons = new List<string>();
            if (skipped404 > 0) reasons.Add("404 (Not Found)");
            if (skipped403 > 0) reasons.Add("403 (Forbidden)");
            WriteWarning($"Skipped {skippedTotal} entities due to {string.Join(" and ", reasons)} responses.");
        }
    }

    private static HttpStatusCode? GetStatusCodeFromException(Exception ex)
    {
        if (ex is GraphServiceException gse) return gse.StatusCode;
        if (ex is HttpRequestException hre && hre.StatusCode.HasValue) return hre.StatusCode.Value;
        return null;
    }
}
