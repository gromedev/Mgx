using System.Collections;
using System.Diagnostics;
using System.Management.Automation;
using System.Net;
using System.Text.Json;
using Mgx.Cmdlets.Base;
using Mgx.Engine.Http;
using Mgx.Engine.Models;
using Mgx.Engine.Pagination;
using Polly.CircuitBreaker;

namespace Mgx.Cmdlets.Cmdlets.Delta;

/// <summary>
/// Sync-MgxDelta: Incremental sync via Microsoft Graph delta queries.
/// First run performs a full sync and saves the delta token.
/// Subsequent runs retrieve only items changed since the last sync.
/// Delta state persists across successful completions (unlike CheckpointPath which is ephemeral).
/// -CheckpointPath adds mid-run crash resume: the enumeration position is saved at page
/// boundaries (and mid-page in JSONL mode), so a killed sync continues where it stopped
/// instead of re-enumerating from scratch. Resume is at-least-once: in pipeline mode the
/// page in flight at the crash is re-emitted in full.
/// </summary>
[Cmdlet(VerbsData.Sync, "MgxDelta")]
[OutputType(typeof(Hashtable))]
public class SyncMgxDelta : MgxCmdletBase
{
    [Parameter(Mandatory = true, Position = 0)]
    public string Uri { get; set; } = string.Empty;

    [Parameter(Mandatory = true)]
    public string DeltaPath { get; set; } = string.Empty;

    [Parameter]
    [Alias("Select")]
    public string[]? Property { get; set; }

    [Parameter]
    public string? Filter { get; set; }

    /// <summary>
    /// Prefer-header tokens joined into a single Prefer header (drive delta behaviors such as
    /// deltashowremovedasdeleted). A change against the stored state forces a full re-sync,
    /// like -Property and -Filter. Note: deltaExcludeParent is a standalone request header,
    /// not a Prefer token - pass it via -Headers.
    /// </summary>
    [Parameter]
    [ArgumentCompleter(typeof(DeltaPreferCompleter))]
    public string[]? Prefer { get; set; }

    [Parameter]
    [ValidateRange(1, 999)]
    public int Top { get; set; }

    [Parameter]
    public string? OutputFile { get; set; }

    [Parameter]
    public SwitchParameter FullSync { get; set; }

    /// <summary>
    /// Baseline without enumerating: request only the latest delta token ("sync from now").
    /// Drive resources take ?token=latest; directory and other resources take
    /// $deltatoken=latest - the form is chosen automatically from the URI shape.
    /// Ignored (with a warning) when usable delta state already exists.
    /// </summary>
    [Parameter]
    public SwitchParameter Latest { get; set; }

    /// <summary>
    /// Path for the ephemeral mid-run resume checkpoint. Deleted on successful completion;
    /// any event that invalidates the enumeration (410 Gone, -FullSync, a -Property/-Filter/
    /// -Prefer change) deletes it too, so a stale position can never be resumed.
    /// </summary>
    [Parameter]
    public string? CheckpointPath { get; set; }

    [Parameter]
    [ValidateSet("v1.0", "beta")]
    [ArgumentCompleter(typeof(ApiVersionCompleter))]
    public string ApiVersion { get; set; } = "v1.0";

    [Parameter]
    public System.Collections.Hashtable? Headers { get; set; }

    private string VersionedBaseUrl => $"{s_graphEndpoint}/{ApiVersion}";

    /// <summary>
    /// Normalize $select for stable comparison: sort, deduplicate, trim, case-insensitive.
    /// Saved to DeltaState.Select so future comparisons are order-independent.
    /// Also used for Prefer tokens - the same normalization semantics apply.
    /// </summary>
    private static string NormalizeSelect(string? s) =>
        string.IsNullOrEmpty(s) ? "" : string.Join(",",
            s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
             .Distinct(StringComparer.OrdinalIgnoreCase)
             .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

    protected override void BeginProcessing()
    {
        // Reject absolute URLs (relative paths only)
        if (Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            Uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            ThrowTerminatingError(new ErrorRecord(
                new ArgumentException(
                    $"-Uri must be a relative path (e.g., /users/delta), not an absolute URL. "
                    + $"Got: '{Uri}'"),
                "AbsoluteUriNotAllowed", ErrorCategory.InvalidArgument, null));
            return;
        }

        // Fail fast: validate delta file is writable before HTTP calls
        var resolvedDeltaPath = GetUnresolvedProviderPathFromPSPath(DeltaPath);
        DeltaState.ValidateWriteAccess(resolvedDeltaPath);

        // Validate -OutputFile writability before HTTP calls
        string? resolvedOutputPath = null;
        if (OutputFile != null)
        {
            resolvedOutputPath = GetUnresolvedProviderPathFromPSPath(OutputFile);
            if (string.Equals(resolvedDeltaPath, resolvedOutputPath, StringComparison.OrdinalIgnoreCase))
            {
                ThrowTerminatingError(new ErrorRecord(
                    new ArgumentException("-DeltaPath and -OutputFile cannot be the same file."),
                    "DeltaPathOutputFileCollision", ErrorCategory.InvalidArgument, null));
                return;
            }
            DeltaState.ValidateWriteAccess(resolvedOutputPath);
        }

        // The checkpoint must not collide with either state file: sharing a path would
        // corrupt both the position and the data it describes.
        if (CheckpointPath != null)
        {
            var resolvedCheckpointPath = GetUnresolvedProviderPathFromPSPath(CheckpointPath);
            if (string.Equals(resolvedCheckpointPath, resolvedDeltaPath, StringComparison.OrdinalIgnoreCase))
            {
                ThrowTerminatingError(new ErrorRecord(
                    new ArgumentException("-CheckpointPath and -DeltaPath must be different files."),
                    "CheckpointDeltaPathCollision", ErrorCategory.InvalidArgument, CheckpointPath));
                return;
            }
            if (resolvedOutputPath != null
                && string.Equals(resolvedCheckpointPath, resolvedOutputPath, StringComparison.OrdinalIgnoreCase))
            {
                ThrowTerminatingError(new ErrorRecord(
                    new ArgumentException("-CheckpointPath and -OutputFile must be different files."),
                    "CheckpointOutputCollision", ErrorCategory.InvalidArgument, CheckpointPath));
                return;
            }
        }

        // Warn if URI doesn't look like a delta endpoint
        if (!Uri.Contains("/delta", StringComparison.OrdinalIgnoreCase))
        {
            WriteWarning(
                $"URI '{Uri}' does not contain '/delta'. Delta queries require a delta endpoint "
                + "(e.g., /users/delta, /groups/delta). The response may not contain a delta token.");
        }
    }

    protected override void ProcessRecord()
    {
        var sw = Stopwatch.StartNew();
        var resolvedDeltaPath = GetUnresolvedProviderPathFromPSPath(DeltaPath);
        var resolvedOutputPath = OutputFile != null
            ? GetUnresolvedProviderPathFromPSPath(OutputFile)
            : null;
        var resolvedCheckpointPath = CheckpointPath != null
            ? GetUnresolvedProviderPathFromPSPath(CheckpointPath)
            : null;

        // Handle -FullSync: delete existing delta state and any resume checkpoint - the
        // position it describes belongs to the enumeration being discarded.
        if (FullSync.IsPresent)
        {
            if (File.Exists(resolvedDeltaPath))
            {
                if (DeltaState.Delete(resolvedDeltaPath))
                {
                    WriteVerbose("Full sync requested. Deleted existing delta state.");
                }
                else
                {
                    WriteWarning($"Full sync requested but could not delete '{DeltaPath}' (file may be locked). " +
                        "The existing delta state will be ignored and a full sync will proceed.");
                }
            }
            DeleteCheckpoint(resolvedCheckpointPath, "full sync requested");
        }

        // Normalize $select and Prefer for order-independent comparison
        var normalizedSelect = NormalizeSelect(Property != null ? string.Join(",", Property) : null);
        var normalizedPrefer = NormalizeSelect(Prefer != null ? string.Join(",", Prefer) : null);
        var currentFilter = Filter;
        string requestUrl;

        // LoadWithResult distinguishes "not found" from "corrupt".
        // Validate delta state BEFORE GetClient() so validation errors
        // are surfaced without requiring a Graph connection.
        var (existingState, loadResult) = DeltaState.LoadWithResult(resolvedDeltaPath);
        // -Latest means "baseline from now, return nothing". That is right for a first run and
        // catastrophic after a state invalidation: the user is told a full re-sync is starting,
        // gets zero items, and a fresh baseline token is persisted - so every change since the
        // last successful sync is dropped permanently. The guard that warns "-Latest ignored"
        // lives in the resume branch, which an invalidated state never reaches. Track it here
        // and clear it wherever state is discarded.
        var honourLatest = Latest.IsPresent;

        if (loadResult == DeltaLoadResult.Corrupt)
        {
            WriteWarning($"Delta state file '{DeltaPath}' is corrupt. Starting full sync.");
            // A corrupt state means the previous position is unknown, which is exactly when
            // baselining from now would hide the most: everything since the last good sync.
            honourLatest = false;
        }

        if (existingState != null)
        {
            // Validate graph endpoint matches current session
            if (!string.Equals(existingState.GraphEndpoint, s_graphEndpoint, StringComparison.OrdinalIgnoreCase))
            {
                ThrowTerminatingError(new ErrorRecord(
                    new InvalidOperationException(
                        $"Delta state was created against '{existingState.GraphEndpoint}' "
                        + $"but current session is connected to '{s_graphEndpoint}'. "
                        + "Use -FullSync to start fresh, or reconnect to the original endpoint."),
                    "DeltaEndpointMismatch", ErrorCategory.InvalidOperation, null));
                return;
            }

            // The deltaLink is absolute and carries its own version, so a run that omits
            // -ApiVersion silently keeps syncing whichever version built the state - the
            // caller believes they are on the default and are not. Empty means a pre-2.0.1
            // state file: unknown, not mismatched, so upgrades are not broken by this check.
            if (!string.IsNullOrEmpty(existingState.ApiVersion)
                && !string.Equals(existingState.ApiVersion, ApiVersion, StringComparison.OrdinalIgnoreCase))
            {
                ThrowTerminatingError(new ErrorRecord(
                    new InvalidOperationException(
                        $"Delta state was created against Graph {existingState.ApiVersion} "
                        + $"but this run requests {ApiVersion}. Re-run with "
                        + $"-ApiVersion {existingState.ApiVersion}, or use -FullSync to rebuild "
                        + $"against {ApiVersion}."),
                    "DeltaApiVersionMismatch", ErrorCategory.InvalidOperation, null));
                return;
            }

            // Detect resource/URI change between runs
            if (!string.Equals(existingState.Resource, Uri, StringComparison.OrdinalIgnoreCase))
            {
                ThrowTerminatingError(new ErrorRecord(
                    new InvalidOperationException(
                        $"Delta state was created for '{existingState.Resource}' but current URI is '{Uri}'. "
                        + "Use -FullSync to start fresh with the new resource."),
                    "DeltaResourceMismatch", ErrorCategory.InvalidOperation, null));
                return;
            }

            // Normalized $select comparison (order-independent, deduplicated)
            var storedSelect = NormalizeSelect(existingState.Select);
            if (!string.Equals(storedSelect, normalizedSelect, StringComparison.OrdinalIgnoreCase))
            {
                WriteWarning(
                    "Property selection changed since last sync "
                    + $"(was: '{existingState.Select ?? "(all)"}', now: '{(string.IsNullOrEmpty(normalizedSelect) ? "(all)" : normalizedSelect)}')."
                    + " Starting full re-sync to capture all selected properties.");
                if (!DeltaState.Delete(resolvedDeltaPath))
                    WriteVerbose($"Could not delete old delta state at '{DeltaPath}' (file may be locked). It will be overwritten.");
                DeleteCheckpoint(resolvedCheckpointPath, "property selection changed");
                existingState = null;
                honourLatest = false;  // a discarded state is not a fresh run
            }

            // Detect Prefer change between runs: the tokens shape what the enumeration
            // returns (removed facets, sharing annotations), so mixing states is unsound.
            if (existingState != null)
            {
                var storedPrefer = NormalizeSelect(existingState.Prefer);
                if (!string.Equals(storedPrefer, normalizedPrefer, StringComparison.OrdinalIgnoreCase))
                {
                    WriteWarning(
                        "Prefer headers changed since last sync "
                        + $"(was: '{(string.IsNullOrEmpty(storedPrefer) ? "(none)" : storedPrefer)}', now: '{(string.IsNullOrEmpty(normalizedPrefer) ? "(none)" : normalizedPrefer)}')."
                        + " Starting full re-sync.");
                    if (!DeltaState.Delete(resolvedDeltaPath))
                        WriteVerbose($"Could not delete old delta state at '{DeltaPath}' (file may be locked). It will be overwritten.");
                    DeleteCheckpoint(resolvedCheckpointPath, "Prefer headers changed");
                    existingState = null;
                    honourLatest = false;  // a discarded state is not a fresh run
                }
            }

            // Detect filter change between runs
            if (existingState != null &&
                !string.Equals(existingState.Filter ?? "", currentFilter ?? "", StringComparison.OrdinalIgnoreCase))
            {
                WriteWarning(
                    "Filter changed since last sync "
                    + $"(was: '{existingState.Filter ?? "(none)"}', now: '{currentFilter ?? "(none)"}')."
                    + " Starting full re-sync.");
                if (!DeltaState.Delete(resolvedDeltaPath))
                    WriteVerbose($"Could not delete old delta state at '{DeltaPath}' (file may be locked). It will be overwritten.");
                DeleteCheckpoint(resolvedCheckpointPath, "filter changed");
                existingState = null;
                honourLatest = false;  // a discarded state is not a fresh run
            }
        }

        if (existingState != null)
        {
            // SSRF validation: deltaLink is untrusted (from a file on disk)
            var deltaUri = new System.Uri(s_graphEndpoint);
            var validated = NextLinkValidator.Validate(existingState.DeltaLink, deltaUri);
            if (validated == null)
            {
                ThrowTerminatingError(new ErrorRecord(
                    new InvalidOperationException(
                        "Delta state contains an invalid or untrusted URL. "
                        + "Use -FullSync to start fresh."),
                    "DeltaLinkValidationFailed", ErrorCategory.SecurityError, null));
                return;
            }

            // Resource path validation: verify the deltaLink's path contains the expected
            // resource. Prevents a tampered delta file from redirecting queries to a different
            // Graph resource (e.g., /me/messages instead of /users/delta).
            // Compare paths to paths. NormalizePath keeps any query, while AbsolutePath never
            // has one, so "/users/delta?$select=id" - the shape Microsoft's delta docs show -
            // guaranteed a mismatch: run 1 saved state, run 2 died with a SecurityError accusing
            // that state file of tampering. A trailing slash failed identically.
            var expectedPath = NormalizePath(Uri).Split('?')[0].TrimEnd('/');
            if (System.Uri.TryCreate(validated, UriKind.Absolute, out var parsedDelta)
                && !parsedDelta.AbsolutePath.TrimEnd('/')
                        .Contains(expectedPath, StringComparison.OrdinalIgnoreCase))
            {
                ThrowTerminatingError(new ErrorRecord(
                    new InvalidOperationException(
                        $"Delta state URL path does not match expected resource '{expectedPath}'. "
                        + "The delta state file may have been tampered with. Use -FullSync to start fresh."),
                    "DeltaLinkPathMismatch", ErrorCategory.SecurityError, null));
                return;
            }

            if (Latest.IsPresent)
            {
                WriteWarning(
                    $"-Latest ignored: usable delta state already exists at '{DeltaPath}'. "
                    + "Delete it or use -FullSync to re-baseline from now.");
            }

            requestUrl = validated;
            WriteVerbose($"Resuming delta sync from {existingState.LastSync:u} ({existingState.ItemCount} items in previous sync).");
        }
        else
        {
            requestUrl = BuildListUrl(VersionedBaseUrl, Uri,
                new ODataListParams(false, Top, Top > 0 ? Top : 999, Filter, Property, null, null, 0, null));

            if (Latest.IsPresent && !honourLatest)
            {
                WriteWarning(
                    "-Latest ignored: the previous delta state was discarded, so this run must "
                    + "enumerate to rebuild it. Baselining from now would silently drop every "
                    + "change since the last successful sync.");
            }
            else if (honourLatest)
            {
                // "Sync from now": returns an empty page plus a deltaLink; the existing
                // empty-page-still-saves-token path persists the baseline. The token form
                // differs by service: OneDrive/SharePoint take token=latest, directory and
                // everything else $deltatoken=latest.
                var tokenParam = AdaptivePacing.Classify(Uri) == WorkloadBucket.Drive
                    ? "token=latest"
                    : "$deltatoken=latest";
                requestUrl += (requestUrl.Contains('?') ? "&" : "?") + tokenParam;
                WriteVerbose($"No existing delta state. Requesting latest delta token only ({tokenParam}).");
            }
            else
            {
                WriteVerbose("No existing delta state. Performing full initial sync.");
            }
        }

        // GetClient() after validation so delta state errors surface without Graph connection
        var client = GetClient();
        ExecuteDeltaSync(client, requestUrl, resolvedDeltaPath, resolvedOutputPath,
            resolvedCheckpointPath, normalizedSelect, normalizedPrefer, currentFilter, sw);
    }

    private void DeleteCheckpoint(string? checkpointPath, string reason)
    {
        if (checkpointPath == null || !File.Exists(checkpointPath)) return;
        if (PaginationCheckpoint.Delete(checkpointPath))
            WriteVerbose($"Deleted resume checkpoint ({reason}).");
        else
            WriteWarning($"Could not delete resume checkpoint at '{checkpointPath}' ({reason}). " +
                "Delete it manually before the next run.");
    }

    private void ExecuteDeltaSync(
        ResilientGraphClient client,
        string requestUrl,
        string deltaPath,
        string? outputPath,
        string? checkpointPath,
        string? select,
        string? prefer,
        string? filter,
        Stopwatch sw)
    {
        bool isFullResync = false;

        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var headers = BuildRequestHeaders(null, Headers);
                if (Prefer is { Length: > 0 })
                {
                    // Dedicated parameter wins over a Prefer key in -Headers (matches the
                    // ConsistencyLevel convention in BuildRequestHeaders).
                    headers ??= new Dictionary<string, string>();
                    headers["Prefer"] = string.Join(",", Prefer);
                }

                // --- resume from checkpoint, when one exists for THIS enumeration ---
                ResumeState? resume = null;
                long resumedItemCount = 0;
                var currentFetchUrl = requestUrl;
                var appendOutput = false;

                if (checkpointPath != null && File.Exists(checkpointPath))
                {
                    // JSONL crash: the checkpoint survives but the output was never promoted from
                    // its temp file. Adopt the temp (trimmed to the checkpointed count) so resume
                    // appends to real data instead of declaring staleness.
                    //
                    // This used to require the output to be ABSENT, which meant the common
                    // steady-state sequence - a successful run, then a crashed one - skipped
                    // adoption entirely: resume restarted at checkpoint.NextLink and the crashed
                    // run's items, sitting in the orphaned temp, were never emitted, while the
                    // delta token advanced past them on success. TryAdoptOrphanedTemp now appends
                    // to an existing output instead of overwriting it, so ADOPTION no longer needs
                    // the guard - but the deletion branch below still does.
                    if (outputPath != null)
                    {
                        var orphanCp = PaginationCheckpoint.Load(checkpointPath);

                        // Validate the checkpoint's resource BEFORE merging anything. Adoption
                        // used to sit behind an output-absent guard, which incidentally kept it
                        // away from this case; without that guard a temp left by a DIFFERENT
                        // enumeration - the glob is outputPath + ".*.tmp" - would be merged into
                        // a valid output, and a later successful resume would make the pollution
                        // permanent. The mismatch is handled properly a few lines below; here we
                        // only decline to adopt.
                        var resourceMatches = orphanCp != null
                            && string.Equals(orphanCp.Resource, requestUrl, StringComparison.Ordinal);

                        if (resourceMatches && orphanCp!.NextLink != null && TryAdoptOrphanedTemp(outputPath, orphanCp.ItemsCollected))
                        {
                            WriteWarning($"Recovered {orphanCp.ItemsCollected} items from an interrupted sync's temp file. Resuming from checkpoint.");
                        }
                        else if (!File.Exists(outputPath))
                        {
                            // Only when the output is genuinely absent, which is what this
                            // message says. Dropping the outer guard made this else fire on ANY
                            // adoption failure - and adoption fails routinely: a resumed run
                            // writes straight to the output and leaves no temp behind, so a
                            // second interruption reached here with a VALID checkpoint, deleted
                            // it, and re-enumerated from the saved deltaLink - replacing the
                            // output with incremental changes only and losing the baseline for
                            // good. Adoption stays unguarded; only the deletion is conditional.
                            WriteWarning("Checkpoint found but output file is missing. Deleting stale checkpoint and starting fresh.");
                            PaginationCheckpoint.Delete(checkpointPath);
                        }
                    }

                    if (File.Exists(checkpointPath))
                    {
                        var checkpoint = PaginationCheckpoint.Load(checkpointPath);
                        if (checkpoint?.NextLink == null)
                        {
                            WriteVerbose("Checkpoint is stale (corrupt or completed). Deleting.");
                            PaginationCheckpoint.Delete(checkpointPath);
                        }
                        else if (!string.Equals(checkpoint.Resource, requestUrl, StringComparison.Ordinal))
                        {
                            // A checkpoint from a different enumeration (different deltaLink,
                            // different parameters, a completed sync in between).
                            WriteWarning("Checkpoint belongs to a different enumeration. Deleting checkpoint and starting fresh.");
                            PaginationCheckpoint.Delete(checkpointPath);
                        }
                        else
                        {
                            // SSRF validation: the checkpoint nextLink is untrusted (a file on disk)
                            var expectedHost = new System.Uri(requestUrl);
                            var validatedLink = NextLinkValidator.Validate(checkpoint.NextLink, expectedHost);
                            if (validatedLink != null
                                && checkpoint.ItemsCollected >= 0
                                && checkpoint.PageItemsAlreadyWritten >= 0)
                            {
                                resume = new ResumeState(
                                    validatedLink,
                                    checkpoint.PageItemsAlreadyWritten,
                                    checkpoint.ItemsCollected);
                                currentFetchUrl = validatedLink;
                                resumedItemCount = checkpoint.ItemsCollected;
                                appendOutput = outputPath != null && File.Exists(outputPath);
                                WriteVerbose($"Resuming delta enumeration from checkpoint: {resumedItemCount} items already processed"
                                    + (checkpoint.PageItemsAlreadyWritten > 0
                                        ? $", skipping {checkpoint.PageItemsAlreadyWritten} items on first page."
                                        : "."));
                            }
                            else
                            {
                                WriteWarning("Checkpoint nextLink failed validation. Deleting checkpoint and starting fresh.");
                                PaginationCheckpoint.Delete(checkpointPath);
                            }
                        }
                    }
                }

                var iterator = new PageIterator(client);
                string? capturedDeltaLink = null;
                long itemCount = 0;
                long removedCount = 0;
                long totalProcessed = resumedItemCount;
                int pageItemsWritten = 0;

                void OnPageComplete(PageCompletedInfo info)
                {
                    if (info.NextPageUrl != null)
                        currentFetchUrl = info.NextPageUrl;
                    pageItemsWritten = 0;
                }

                void SaveBoundaryCheckpoint(PageCompletedInfo info)
                {
                    if (checkpointPath == null || info.NextPageUrl == null) return;
                    try
                    {
                        new PaginationCheckpoint
                        {
                            Resource = requestUrl,
                            NextLink = info.NextPageUrl,
                            ItemsCollected = totalProcessed,
                            PageItemsAlreadyWritten = 0
                        }.Save(checkpointPath);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        WriteWarning($"Checkpoint save failed (page boundary): {ex.Message}");
                    }
                }

                if (outputPath != null)
                {
                    // JSONL output mode. Fresh runs write to a temp file and promote on
                    // success; checkpointed resumes append to the already-promoted output.
                    var writePath = appendOutput ? outputPath : $"{outputPath}.{Guid.NewGuid():N}.tmp";
                    try
                    {
                        using (var writer = new StreamWriter(writePath, appendOutput))
                        {
                            var enumerable = iterator.StreamAllWithCountAsync(
                                requestUrl,
                                maxItems: 0,
                                onCount: null,
                                headers: headers,
                                resume: resume,
                                onPageComplete: info =>
                                {
                                    writer.Flush();
                                    SaveBoundaryCheckpoint(info);
                                    OnPageComplete(info);
                                },
                                onDeltaLink: dl => capturedDeltaLink = dl,
                                cancellationToken: CancellationToken);

                            var enumerator = enumerable.GetAsyncEnumerator(CancellationToken);
                            try
                            {
                                while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                                {
                                    writer.WriteLine(enumerator.Current.GetRawText());
                                    if (enumerator.Current.TryGetProperty("@removed", out _))
                                        removedCount++;
                                    itemCount++;
                                    pageItemsWritten++;
                                    totalProcessed++;
                                    DrainClientMessages();

                                    if (totalProcessed % 500 == 0)
                                    {
                                        writer.Flush();
                                        // Mid-page checkpoint: tracks items written from the
                                        // current page so crash resume skips them (no dupes).
                                        if (checkpointPath != null)
                                        {
                                            try
                                            {
                                                new PaginationCheckpoint
                                                {
                                                    Resource = requestUrl,
                                                    NextLink = currentFetchUrl,
                                                    ItemsCollected = totalProcessed,
                                                    PageItemsAlreadyWritten = pageItemsWritten
                                                }.Save(checkpointPath);
                                            }
                                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                                            {
                                                WriteWarning($"Mid-page checkpoint save failed: {ex.Message}");
                                            }
                                        }
                                    }
                                }
                            }
                            finally
                            {
                                enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
                            }
                        }
                        if (!appendOutput)
                            File.Move(writePath, outputPath, overwrite: true);
                    }
                    catch (Exception attemptEx)
                    {
                        if (!appendOutput)
                        {
                            // User cancellation of a checkpointed fresh run: promote the temp
                            // file (the using block already flushed it on unwind) and save a
                            // checkpoint matching its exact content, so resume works on first
                            // runs too. Otherwise clean the temp up as before.
                            var cancelled = attemptEx is OperationCanceledException
                                && CancellationToken.IsCancellationRequested;
                            var promoted = false;
                            if (cancelled && checkpointPath != null && itemCount > 0)
                            {
                                try
                                {
                                    new PaginationCheckpoint
                                    {
                                        Resource = requestUrl,
                                        NextLink = currentFetchUrl,
                                        ItemsCollected = totalProcessed,
                                        PageItemsAlreadyWritten = pageItemsWritten
                                    }.Save(checkpointPath);
                                    File.Move(writePath, outputPath, overwrite: true);
                                    promoted = true;
                                }
                                catch (Exception promoteEx) when (promoteEx is IOException or UnauthorizedAccessException)
                                {
                                    // Promotion is best-effort; fall back to the old cleanup.
                                }
                            }
                            if (!promoted)
                            {
                                try { if (File.Exists(writePath)) File.Delete(writePath); } catch { }
                            }
                        }
                        throw;
                    }
                }
                else
                {
                    // Pipeline output mode. Checkpoints save at page boundaries only:
                    // emitted objects cannot be un-emitted, so resume re-emits the page in
                    // flight at the crash (at-least-once, documented).
                    var enumerable = iterator.StreamAllWithCountAsync(
                        requestUrl,
                        maxItems: 0,
                        onCount: null,
                        headers: headers,
                        resume: resume,
                        onPageComplete: info =>
                        {
                            SaveBoundaryCheckpoint(info);
                            OnPageComplete(info);
                        },
                        onDeltaLink: dl => capturedDeltaLink = dl,
                        cancellationToken: CancellationToken);

                    var enumerator = enumerable.GetAsyncEnumerator(CancellationToken);
                    try
                    {
                        while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                        {
                            if (enumerator.Current.TryGetProperty("@removed", out _))
                                removedCount++;
                            var ht = JsonToHashtable(enumerator.Current);
                            WriteObject(ht);
                            itemCount++;
                            pageItemsWritten++;
                            totalProcessed++;
                            DrainClientMessages();
                        }
                    }
                    finally
                    {
                        enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    }
                }

                // Success: the checkpoint's job is done - delete it BEFORE saving delta
                // state, so a crash between the two leaves a fresh incremental (correct)
                // rather than a resumable position into a completed enumeration (wrong).
                DeleteCheckpoint(checkpointPath, "sync completed");

                // Save delta state ONLY after successful completion (Architect P0).
                // Zero-item responses still save the token (Adversarial P0).
                if (capturedDeltaLink != null)
                {
                    new DeltaState
                    {
                        DeltaLink = capturedDeltaLink,
                        Select = select, // Normalized value for stable future comparisons
                        Filter = filter,
                        Prefer = prefer, // Normalized, like Select
                        Resource = Uri,
                        ItemCount = totalProcessed,
                        GraphEndpoint = s_graphEndpoint,
                        ApiVersion = ApiVersion
                    }.Save(deltaPath);
                    WriteVerbose($"Delta state saved to '{deltaPath}'.");
                }
                else
                {
                    WriteWarning("No delta token received from Graph. The endpoint may not support delta queries.");
                }

                DrainClientMessages();
                sw.Stop();

                WriteVerbose(
                    $"Delta sync complete: {itemCount} items"
                    + (removedCount > 0 ? $" ({removedCount} removed)" : "")
                    + $" in {sw.Elapsed.TotalSeconds:F1}s"
                    + (resumedItemCount > 0 ? $" (resumed after {resumedItemCount})" : "")
                    + (isFullResync ? " (full re-sync after 410 Gone)" : "")
                    + (outputPath != null ? $". Output: {outputPath}" : "."));

                return;
            }
            catch (GraphServiceException ex) when (
                attempt == 0
                && ex.StatusCode == HttpStatusCode.Gone)
            {
                // 410 Gone: delta token expired (>7 days for directory objects).
                // Delete delta state AND any checkpoint - both describe the dead
                // enumeration - and restart with full sync.
                // Second attempt builds fresh URL (no delta token), so 410 won't recur.
                DrainClientMessages();
                if (!DeltaState.Delete(deltaPath))
                    WriteVerbose("Could not delete expired delta state (file may be locked). It will be overwritten.");
                DeleteCheckpoint(checkpointPath, "delta token expired (410 Gone)");
                isFullResync = true;
                requestUrl = BuildListUrl(VersionedBaseUrl, Uri,
                    new ODataListParams(false, Top, Top > 0 ? Top : 999, Filter, Property, null, null, 0, null));
                WriteWarning(
                    "Delta token expired (HTTP 410 Gone). Starting full re-sync. "
                    + "Tokens expire after ~7 days for directory objects.");
                continue;
            }
            catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
            {
                DrainClientMessages();
                var resumeHint = checkpointPath != null
                    ? $" Resume with: Sync-MgxDelta '{Uri}' -DeltaPath '{DeltaPath}' -CheckpointPath '{CheckpointPath}'"
                      + (OutputFile != null ? $" -OutputFile '{OutputFile}'" : "")
                    : " Use -CheckpointPath to enable mid-run resume.";
                WriteWarning($"Delta sync cancelled.{resumeHint}");
                return;
            }
            catch (Exception ex) when (ex is GraphServiceException or BrokenCircuitException or HttpRequestException)
            {
                WriteGraphError(ex, Uri);
                return;
            }
            catch (IOException ex)
            {
                DrainClientMessages();
                WriteError(new ErrorRecord(ex, "IOError",
                    ErrorCategory.WriteError, OutputFile));
                return;
            }
            catch (Exception)
            {
                // Drain buffered messages for unexpected exception types
                // (e.g., JsonException, OutOfMemoryException) so diagnostic
                // context is not silently lost.
                DrainClientMessages();
                throw;
            }
        }
    }

}
