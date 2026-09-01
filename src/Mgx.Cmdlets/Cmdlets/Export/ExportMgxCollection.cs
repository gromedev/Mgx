using System.Diagnostics;
using System.Management.Automation;
using System.Net;
using Mgx.Cmdlets.Base;
using Mgx.Engine.Http;
using Mgx.Engine.Models;
using Mgx.Engine.Pagination;
using Polly.CircuitBreaker;

namespace Mgx.Cmdlets.Cmdlets.Export;

/// <summary>
/// Export-MgxCollection: Stream paginated Graph API results directly to a JSONL file.
/// One JSON object per line; no PSObject conversion, minimal memory pressure.
/// Supports checkpoint/resume for interrupted exports.
/// Consumer owns checkpoint lifecycle: saves at page boundaries and mid-page flushes
/// to prevent duplicate items on crash resume (H6 dedup fix).
/// </summary>
[Cmdlet(VerbsData.Export, "MgxCollection", SupportsShouldProcess = true)]
[OutputType(typeof(PSObject))]
public class ExportMgxCollection : MgxCmdletBase
{
    [Parameter(Mandatory = true, Position = 0)]
    [Alias("Resource")]
    public string Uri { get; set; } = string.Empty;

    [Parameter(Mandatory = true)]
    public string OutputFile { get; set; } = string.Empty;

    [Parameter]
    [Alias("Select")]
    public string[]? Property { get; set; }

    [Parameter]
    public string? Filter { get; set; }

    [Parameter]
    [Alias("Expand")]
    public string[]? ExpandProperty { get; set; }

    [Parameter]
    public string? Search { get; set; }

    [Parameter]
    [Alias("OrderBy")]
    public string[]? Sort { get; set; }

    [Parameter]
    [ValidateRange(0, int.MaxValue)]
    public int Skip { get; set; }

    [Parameter]
    [ValidateRange(0, int.MaxValue)]
    public int Top { get; set; }

    [Parameter]
    public SwitchParameter All { get; set; }

    [Parameter]
    [ValidateRange(1, 999)]
    public int PageSize { get; set; } = 999;

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
    public string? CheckpointPath { get; set; }

    [Parameter]
    public SwitchParameter NoPageSize { get; set; }

    private string VersionedBaseUrl => $"{s_graphEndpoint}/{ApiVersion}";

    protected override void BeginProcessing()
    {
        // Reject absolute URLs (relative paths only); concatenation onto the versioned
        // base URL would otherwise silently produce /v1.0/https:/... on the wire.
        if (Uri.TrimStart().StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            Uri.TrimStart().StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            ThrowTerminatingError(new ErrorRecord(
                new ArgumentException(
                    $"-Uri must be a relative path (e.g., /users), not an absolute URL. Got: '{Uri}'"),
                "AbsoluteUriNotAllowed", ErrorCategory.InvalidArgument, null));
            return;
        }

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
        // auto-add when -Filter is used (enables count discrepancy detection)
        if (!string.IsNullOrEmpty(Filter) && string.IsNullOrEmpty(ConsistencyLevel))
        {
            ConsistencyLevel = "eventual";
            WriteVerbose("Auto-adding ConsistencyLevel:eventual header (required by -Filter for $count=true).");
        }
    }

    protected override void ProcessRecord()
    {
        var sw = Stopwatch.StartNew();

        // Resolve paths (before requiring Graph connection, so -WhatIf works without auth)
        var outputPath = GetUnresolvedProviderPathFromPSPath(OutputFile);
        var cpPath = CheckpointPath != null
            ? GetUnresolvedProviderPathFromPSPath(CheckpointPath)
            : null;

        // Validate CheckpointPath != OutputFile (would corrupt both files)
        if (cpPath != null && string.Equals(cpPath, outputPath, StringComparison.OrdinalIgnoreCase))
        {
            ThrowTerminatingError(new ErrorRecord(
                new ArgumentException(
                    "-CheckpointPath and -OutputFile must be different files. Using the same file would corrupt both the checkpoint and the export data."),
                "CheckpointOutputCollision", ErrorCategory.InvalidArgument, CheckpointPath));
            return;
        }

        // -All says how far to page, -Top says how much to return. They answer different
        // questions, so -All no longer discards the cap: -Top is documented as the total
        // maximum, and Invoke-MgxRequest already honours it either way. Neither, and a single
        // page is the limit.
        int maxItems;
        bool defaultedToPageSize = false;
        if (Top > 0)
            maxItems = Top;
        else if (All.IsPresent)
            maxItems = 0; // unlimited
        else
        {
            maxItems = PageSize; // single page worth
            defaultedToPageSize = true;
        }

        // Track whether $count=true was auto-added (not user-requested).
        // If the endpoint rejects it with 400, retry without. Settled before the checkpoint is
        // looked at: which URL this run fetches is half of whether the checkpoint on disk is
        // about it, and none of this needs a Graph connection.
        bool countAutoAdded = !string.IsNullOrEmpty(Filter)
            && !ExistingQueryOptions(Uri).Contains("$count");
        bool includeAutoCount = countAutoAdded;
        bool suppressTop = false;

        // Which form of that URL the checkpoint on disk was written under, when it is this
        // export's. The loop below drops the auto-added $count on a bare 400 and the automatic
        // $top on a Request_UnsupportedQuery, so an export interrupted after a rejection
        // recorded a URL this run does not build until its own retry. Comparing the first form
        // alone called that checkpoint another export's: the reconcile recovered nothing, the
        // sweep below deleted the temp holding its items, and the retry - which does build that
        // form - matched with the append already decided from the output merely existing, so
        // the remainder of the enumeration was appended onto a previous export's file. Every
        // form goes to the same comparison, and it settles which URL this run resumes before
        // anything is promoted, trimmed or deleted.
        if (cpPath != null && File.Exists(cpPath)
            && AttemptFormOf(PaginationCheckpoint.Load(cpPath), outputPath, countAutoAdded) is { } form)
        {
            includeAutoCount = form.IncludeCount;
            suppressTop = form.SuppressTop;
        }

        // ShouldProcess check (before requiring Graph connection, so -WhatIf works without auth).
        // The action it names is what recovery below would do, worked out without doing any of
        // it: recovery promotes, trims and deletes, and it used to run above this gate, so
        // -WhatIf rewrote the output and removed checkpoints on the way to reporting that it
        // would not. It stays above GetClient either way - "what would this do" is not a
        // question that should need a Graph connection.
        var wouldAppend = ReconcileCheckpoint(cpPath, outputPath, includeAutoCount, suppressTop, apply: false);
        if (!ShouldProcess(outputPath, wouldAppend ? "Append JSONL data" : "Export JSONL data"))
            return;

        // Whether this run appends to the output rather than exporting into a fresh temp,
        // decided once, against every form of the URL the attempts below can build, and only
        // ever withdrawn afterwards. Recomputed from a checkpoint and an output merely
        // EXISTING, it came back true on the attempt after the reconcile had refused - and the
        // rest of the enumeration went onto whatever file was sitting at -OutputFile. A
        // refusal has to outlast the attempt that made it.
        var appendToOutput = ReconcileCheckpoint(cpPath, outputPath, includeAutoCount, suppressTop, apply: true);

        // Init client after the gate (populates s_graphEndpoint for sovereign clouds)
        var client = GetClient();

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                // The checkpoint can go between attempts - a previous one deleted it as stale,
                // or finished with it - so its existence is re-checked. Whose it is, is not
                // re-decided: that answer is above, and it can only be taken away here.
                appendToOutput = appendToOutput
                    && cpPath != null && File.Exists(cpPath) && File.Exists(outputPath);

                var url = BuildUrl(includeAutoCount, suppressTop);
                var headers = BuildHeaders();

                // Load checkpoint and compute resume state (consumer owns checkpoint lifecycle)
                ResumeState? resume = null;
                long resumedItemCount = 0;
                string currentFetchUrl = url;

                if (cpPath != null && appendToOutput)
                {
                    var checkpoint = PaginationCheckpoint.Load(cpPath);
                    if (checkpoint != null)
                    {
                        if (!DescribesThisExport(checkpoint, outputPath, includeAutoCount, suppressTop))
                        {
                            // Ownership is re-decided on every load, not carried over from the
                            // check above: the checkpoint is a file, the run that owns it is
                            // still going, and it can be replaced between the two reads. A
                            // checkpoint that is not this export's is left where it is - the
                            // export it does belong to needs it - and this run exports fresh.
                            // Only ever this way round: a refusal stands for the rest of the
                            // run, and no later attempt grants it back from the two files
                            // being where they were.
                            //
                            // And said out loud. This is the only refusal that lands on a
                            // checkpoint already accepted, so a resume has been reported and is
                            // being taken back: the collection is enumerated from the first page
                            // again, and the pages collected under the resume are collected a
                            // second time. Withdrawn in silence, the recovery message was the
                            // last thing on any stream, and a run that had quietly started over
                            // was indistinguishable from one that resumed.
                            _refusedCheckpointTemp = checkpoint.TempFile;
                            appendToOutput = false;
                            WriteWarning(
                                $"The resume checkpoint at '{cpPath}' no longer describes this export - the "
                                + "request was rebuilt without a query option the endpoint refused, or another "
                                + $"run replaced the file - so the resume is dropped and '{outputPath}' is "
                                + "exported from the beginning; nothing is lost.");
                        }
                        else if (checkpoint.NextLink == null)
                        {
                            // Completion marker: previous export finished, checkpoint is stale
                            WriteVerbose("Checkpoint indicates previous export completed. Deleting stale checkpoint.");
                            PaginationCheckpoint.Delete(cpPath);
                            appendToOutput = false;
                        }
                        else
                        {
                            // Validate NextLink (SSRF protection)
                            var expectedHost = new System.Uri(url);
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
                                WriteVerbose($"Resuming from checkpoint: {resumedItemCount} items already exported, skipping {checkpoint.PageItemsAlreadyWritten} items on first page.");
                            }
                            else
                            {
                                WriteWarning("Checkpoint nextLink failed validation. Deleting checkpoint and starting fresh.");
                                PaginationCheckpoint.Delete(cpPath);
                                appendToOutput = false;
                            }
                        }
                    }
                    else
                    {
                        // The unreadable checkpoint above could not be deleted either - it is
                        // locked, or the account cannot touch it. Appending to the output on
                        // the strength of a file nothing can read is what has to stop, not the
                        // export: write a fresh run to a temp and let it replace the output.
                        appendToOutput = false;
                    }
                }

                // For fresh exports (not resume), write to a temp file first.
                // This protects any pre-existing output file from truncation if
                // the Graph request fails on the first page.
                // Use GUID to prevent collision when multiple exports target the same file.
                if (!appendToOutput)
                {
                    // No resume is pending, so every "{output}.{guid}.tmp" on disk is an orphan.
                    // Leaving them is not inert: the pre-length adoption path picks the NEWEST
                    // match with only a line count to go on, so a survivor of an unrelated run is
                    // adoptable by some later crash's checkpoint.
                    //
                    // Except after a refusal, where one of them is not an orphan at all: a
                    // checkpoint describing it is on disk, left there deliberately a moment ago,
                    // and the items it counts are in that temp. Sweeping it took the one file
                    // that made the checkpoint worth keeping, so the export it belongs to came
                    // back to a position pointing at nothing and re-enumerated from the first
                    // page - the cost the refusal was written to avoid. The sweep is all or
                    // nothing over this output's temps, so a run that has just refused leaves
                    // them to the next run that has not.
                    //
                    // And once more on a retry, where the checkpoint standing over the temp is
                    // this run's own. An endpoint that refuses the auto-added $count, or $top,
                    // sends the attempt loop round again after pages have already been
                    // collected and checkpointed, and the attempt that died kept its temp for
                    // exactly that checkpoint to resume from. Sweeping here deleted it while
                    // the checkpoint naming it was still on disk - the one combination neither
                    // this policy nor the retry's intends - and the next invocation found a
                    // position pointing at nothing and exported from the first page.
                    if (RefusedTempIsOnDisk(_refusedCheckpointTemp, outputPath))
                    {
                        WriteVerbose(
                            $"Left the temp files beside '{outputPath}' alone: '{_refusedCheckpointTemp}' "
                            + "holds the items of the checkpoint this run refused.");
                    }
                    else if (_keptTempPath != null && File.Exists(_keptTempPath))
                    {
                        WriteVerbose(
                            $"Left the temp files beside '{outputPath}' alone: "
                            + $"'{Path.GetFileName(_keptTempPath)}' holds the items of this run's own "
                            + "checkpoint, which a previous attempt left on disk.");
                    }
                    else
                    {
                        DeleteStaleTemps(outputPath);
                    }
                }
                var writePath = appendToOutput ? outputPath : $"{outputPath}.{Guid.NewGuid():N}.tmp";
                // What the checkpoint sites below should say about WHERE the counted items are.
                // A resumed run appends to the output itself, so it has no temp to name.
                string? checkpointTempFile = appendToOutput ? null : Path.GetFileName(writePath);
                // Whether the checkpoint on disk is one THIS attempt saved. A checkpoint an
                // earlier attempt left names an earlier temp, and the two are not
                // interchangeable when it comes to deciding what a file on disk is still for.
                var savedOwnCheckpoint = false;
                long? checkpointDataLength = null;
                long itemCount = 0;
                // Seeded from the resume skip, not 0. PageIterator drops the skipped items before
                // this loop ever sees them, so a counter starting at 0 records only the NEWLY
                // written items of a resumed first page. A mid-page checkpoint saved there then
                // claims fewer items of that page than the output holds, and the next resume skips
                // too few and writes the difference twice.
                int pageItemsWritten = resume?.SkipOnFirstPage ?? 0;
                long totalWritten = resumedItemCount;
                long? reportedODataCount = null;

                // A temp a failed attempt of this run kept is kept on one condition: a
                // checkpoint counting its items is on disk. Saving a checkpoint here is what
                // ends that - the file just written describes this attempt's temp, or the
                // output on a resumed run, and never the earlier one - so from that moment
                // nothing on disk refers to it, no recovery can reach it, and the sweep at the
                // top of the next attempt is still being held off on its behalf. Left where it
                // was, it outlived the export that made it: a partial copy of the caller's
                // data sitting beside the finished file, waiting for some later run's sweep.
                //
                // Never the file this attempt is writing into, which on a resumed run is the
                // output itself. Best effort otherwise: a temp something else holds open is
                // not worth failing an export over.
                void ReleaseKeptTemp()
                {
                    var kept = _keptTempPath;
                    if (kept == null
                        || string.Equals(kept, writePath, StringComparison.OrdinalIgnoreCase))
                        return;
                    _keptTempPath = null;
                    try { if (File.Exists(kept)) File.Delete(kept); } catch { }
                }

                try
                {
                    using (var writer = new StreamWriter(writePath, appendToOutput))
                    {
                        var iterator = new PageIterator(client);

                        var enumerable = iterator.StreamAllWithCountAsync(
                            url,
                            maxItems,
                            count => { reportedODataCount = count; },
                            headers,
                            resume: resume,
                            onPageComplete: info =>
                            {
                                // Save page-boundary checkpoint (PageItemsAlreadyWritten = 0 since page is complete)
                                if (cpPath != null && info.NextPageUrl != null)
                                {
                                    try
                                    {
                                        writer.Flush();
                                        checkpointDataLength = writer.BaseStream.Position;
                                        new PaginationCheckpoint
                                        {
                                            Resource = url,
                                            NextLink = info.NextPageUrl,
                                            ItemsCollected = totalWritten,
                                            PageItemsAlreadyWritten = 0,
                                            TempFile = checkpointTempFile,
                                            OutputFile = Path.GetFullPath(outputPath),
                                            DataLength = checkpointDataLength
                                        }.Save(cpPath);
                                        savedOwnCheckpoint = true;
                                        ReleaseKeptTemp();
                                    }
                                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                                    {
                                        WriteWarning($"Checkpoint save failed (page boundary): {ex.Message}");
                                    }
                                }
                                if (info.NextPageUrl != null)
                                    currentFetchUrl = info.NextPageUrl;
                                pageItemsWritten = 0;
                            },
                            cancellationToken: CancellationToken);

                        var enumerator = enumerable.GetAsyncEnumerator(CancellationToken);
                        try
                        {
                            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                            {
                                writer.WriteLine(enumerator.Current.GetRawText());
                                itemCount++;
                                pageItemsWritten++;
                                totalWritten++;

                                if (totalWritten % 500 == 0)
                                {
                                    DrainClientMessages();
                                    writer.Flush();

                                    // Mid-page checkpoint: tracks items written from current page
                                    // to prevent duplicates on crash resume (H6 fix)
                                    if (cpPath != null)
                                    {
                                        try
                                        {
                                            checkpointDataLength = writer.BaseStream.Position;
                                            new PaginationCheckpoint
                                            {
                                                Resource = url,
                                                NextLink = currentFetchUrl,
                                                ItemsCollected = totalWritten,
                                                PageItemsAlreadyWritten = pageItemsWritten,
                                                TempFile = checkpointTempFile,
                                                OutputFile = Path.GetFullPath(outputPath),
                                                DataLength = checkpointDataLength
                                            }.Save(cpPath);
                                            savedOwnCheckpoint = true;
                                            ReleaseKeptTemp();
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

                    // Writer closed. Promote the temp, and only then let go of the checkpoint.
                    //
                    // The move is the one step that can still fail with every page already
                    // fetched: the output held open by another process, a read-only
                    // destination, a share that dropped between the last write and the rename.
                    // Deleting the checkpoint first made that failure destroy the run - the
                    // catch below read "no checkpoint" as "nothing to resume", deleted the temp
                    // holding every item, and the next invocation enumerated from the first
                    // page. This way round a failed move leaves the checkpoint and the temp on
                    // disk exactly as an interruption does, and the next run promotes the temp
                    // and resumes from the last page the checkpoint recorded.
                    if (!appendToOutput)
                    {
                        File.Move(writePath, outputPath, overwrite: true);
                    }

                    // The items are in the output now, so the position describing them is
                    // spent. A crash in the gap costs a re-enumeration: the checkpoint names a
                    // temp that is no longer there, which recovery reads as unusable.
                    if (cpPath != null) PaginationCheckpoint.Delete(cpPath);

                    // And with it goes the last thing that could have named a temp an earlier
                    // attempt kept - the attempt that finished need never have saved a
                    // checkpoint of its own to release it, if everything it had left fitted in
                    // one page. Only here, on the way out of a run that promoted: a promotion
                    // that failed leaves the checkpoint and the temp for the next run to resume
                    // from, and both are still exactly what that run needs.
                    ReleaseKeptTemp();
                }
                catch (Exception attemptEx)
                {
                    if (!appendToOutput)
                    {
                        // User cancellation of a checkpointed fresh run: promote the temp
                        // file (the using block already flushed it on unwind) and save a
                        // checkpoint matching its exact content, so the printed resume
                        // hint is true for first runs too. Previously the temp was
                        // deleted here and the next run declared the checkpoint stale.
                        var cancelled = attemptEx is OperationCanceledException
                            && CancellationToken.IsCancellationRequested;
                        var promoted = false;
                        if (cancelled && cpPath != null && itemCount > 0)
                        {
                            try
                            {
                                // Move first, then record. A checkpoint that named the output
                                // before the move existed would describe a file that is not
                                // there yet, and a move that then failed would leave it saying
                                // so. The length is the temp's, taken before the move, because
                                // it is the same bytes under a different name afterwards.
                                var promotedLength = new FileInfo(writePath).Length;
                                File.Move(writePath, outputPath, overwrite: true);
                                promoted = true;
                                new PaginationCheckpoint
                                {
                                    Resource = url,
                                    NextLink = currentFetchUrl,
                                    ItemsCollected = totalWritten,
                                    PageItemsAlreadyWritten = pageItemsWritten,
                                    TempFile = null,
                                    OutputFile = Path.GetFullPath(outputPath),
                                    DataLength = promotedLength
                                }.Save(cpPath);
                                ReleaseKeptTemp();
                            }
                            catch (Exception promoteEx) when (promoteEx is IOException or UnauthorizedAccessException)
                            {
                                // Promotion is best-effort; fall back to the old cleanup.
                            }
                        }
                        if (!promoted)
                        {
                            // A surviving checkpoint counts items that exist only in this temp:
                            // every checkpoint site flushes the writer before recording the
                            // position, so the temp always holds at least what it promises.
                            // Deleting it made the next run's recovery find the checkpoint
                            // naming a missing file and start the export over - resume worked
                            // after a kill or a Ctrl-C but never after a handled error, which
                            // is the common way a long export dies. Keep the temp for the next
                            // run to promote; it is deleted by promotion or by the stale-temp
                            // sweep once the checkpoint is gone.
                            //
                            // The checkpoint counting them has to be one this attempt saved.
                            // Any other is an earlier attempt's, naming an earlier temp, and
                            // this attempt's items are then counted by nothing: keeping the
                            // file left a partial page no recovery can reach, and the newest
                            // temp beside an output is what the pre-length adoption path picks
                            // up on a line count alone.
                            var resumable = cpPath != null && File.Exists(cpPath) && savedOwnCheckpoint;
                            if (resumable)
                            {
                                // Named for the sweep above, which the next attempt reaches
                                // before this checkpoint has been resumed from or replaced.
                                _keptTempPath = writePath;
                            }
                            else
                            {
                                // Whatever is named stays named: the checkpoint on disk is
                                // still the earlier attempt's, and so is the temp it counts.
                                try { if (File.Exists(writePath)) File.Delete(writePath); } catch { }
                            }
                        }
                    }
                    throw; // re-throw to retry catch or outer catch blocks
                }

                sw.Stop();
                DrainClientMessages();

                var totalItems = resumedItemCount + itemCount;

                // Count discrepancy warning (only for full exports without resume)
                if (reportedODataCount.HasValue && maxItems == 0 && resume == null)
                    WriteCountDiscrepancyWarning(Uri, reportedODataCount.Value, totalItems, Filter);

                // Warn if 0 items and not resuming (could be a single-entity URI)
                if (totalItems == 0)
                {
                    WriteWarning(
                        "Export completed with 0 items. If you intended to retrieve a single entity, " +
                        "use Invoke-MgxRequest instead of Export-MgxCollection.");
                }

                // Warn if export hit the default page-size cap (may have more data)
                if (defaultedToPageSize && itemCount >= maxItems)
                {
                    WriteWarning(
                        $"Export stopped at {totalItems} items (default page size). " +
                        "Use -All to export everything, or -Top N to set an explicit limit.");
                }


                // Output summary
                var summary = new Models.MgxExportResult
                {
                    ItemCount = totalItems,
                    OutputFile = outputPath,
                    Duration = sw.Elapsed,
                    ResumedFrom = resumedItemCount > 0 ? resumedItemCount : null,
                };
                WriteObject(summary);
                return; // Success, exit the retry loop
            }
            catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
            {
                DrainClientMessages();
                var resumeHint = CheckpointPath != null
                    ? $"Resume with: Export-MgxCollection '{Uri}' -OutputFile '{OutputFile}' -CheckpointPath '{CheckpointPath}'"
                    : "Use -CheckpointPath to enable resume on next run.";
                WriteWarning($"Export cancelled. {resumeHint}");
                return;
            }
            catch (System.Text.Json.JsonException ex)
            {
                // A page body that does not parse. Items already exported stay in the file;
                // report the failure instead of ending the pipeline with a raw exception.
                DrainClientMessages();
                WriteError(new ErrorRecord(
                    new InvalidOperationException($"A response page declared JSON but does not parse: {ex.Message}", ex),
                    "MalformedJsonResponse", ErrorCategory.InvalidData, Uri));
                return;
            }
            catch (GraphServiceException ex) when (IsCountRejection(ex, includeAutoCount && countAutoAdded))
            {
                DrainClientMessages();
                WriteVerbose(CountRejectedVerbose);
                includeAutoCount = false;
                continue;
            }
            catch (GraphServiceException ex) when (IsTopRejection(ex, suppressTop, NoPageSize.IsPresent))
            {
                DrainClientMessages();
                WriteVerbose(TopRejectedVerbose);
                suppressTop = true;
                continue;
            }
            catch (Exception ex) when (ex is GraphServiceException or BrokenCircuitException or HttpRequestException)
            {
                WriteGraphError(ex, Uri, ApiVersion);
                return;
            }
            catch (IOException ex)
            {
                DrainClientMessages();
                WriteError(new ErrorRecord(ex, "IOError",
                    ErrorCategory.WriteError, OutputFile));
                return;
            }
            catch (UnauthorizedAccessException ex)
            {
                DrainClientMessages();
                WriteError(new ErrorRecord(ex, "AccessDenied",
                    ErrorCategory.PermissionDenied, OutputFile));
                return;
            }
            catch (Exception)
            {
                // Unexpected exception types skip the drains above; the buffered verbose
                // and warning messages are the context that explains the failure.
                DrainClientMessages();
                throw;
            }
        }
    }

    private bool _warnedDeferredOptions;

    private string BuildUrl(bool includeCount, bool suppressTop = false)
    {
        var url = BuildListUrl(
            VersionedBaseUrl, Uri,
            new ODataListParams(NoPageSize.IsPresent || suppressTop, Top, PageSize, Filter,
                Property, Sort, Search, Skip, ExpandProperty,
                IncludeCount: includeCount),
            out var deferred);
        if (deferred.Count > 0 && !_warnedDeferredOptions)
        {
            _warnedDeferredOptions = true;
            WriteWarning(DescribeDeferredOptions(deferred));
        }
        return url;
    }

    private Dictionary<string, string>? BuildHeaders() =>
        BuildRequestHeaders(ConsistencyLevel, Headers);

    /// <summary>
    /// What a caller can act on when the checkpoint on disk turns out to be another export's:
    /// which two files disagree, what this run does instead, and that a -CheckpointPath belongs
    /// to one export. Refusing it is the whole response - the file is left alone, and so is any
    /// staging file beside it, because the export that wrote it resumes from exactly those.
    /// </summary>
    private static string ForeignCheckpointWarning(string checkpointPath, string outputPath) =>
        $"The resume checkpoint at '{checkpointPath}' belongs to a different export, so it is "
        + $"left as it is and '{outputPath}' is exported from the beginning. Two exports sharing "
        + "one -CheckpointPath overwrite each other's resume position; give each its own.";

    /// <summary>
    /// What a caller can act on when a checkpoint that records no output file is refused: it
    /// was written before the output was recorded, nothing beside this one's output stands for
    /// it any more, and this run exports from the beginning. Naming a different export there
    /// sends the caller looking for a second run over the same -CheckpointPath, and there is
    /// none to find - a temp that has since been removed and an output that has since been
    /// replaced reach the same refusal.
    /// </summary>
    private static string UncorroboratedCheckpointWarning(string checkpointPath, string outputPath) =>
        $"The resume checkpoint at '{checkpointPath}' records no output file, and the files "
        + $"beside '{outputPath}' no longer corroborate it, so it is left as it is and "
        + $"'{outputPath}' is exported from the beginning; nothing is lost.";

    /// <summary>
    /// What a caller can act on when the temp a checkpoint names is open in another run. Not
    /// phrased as a fault: nothing has gone wrong with either file, two exports are writing one
    /// -OutputFile, and the only thing the caller has to decide is which of them they meant to
    /// keep - the one that finishes last is the one whose rows the output ends up with.
    /// </summary>
    private static string HeldTempWarning(string checkpointPath, string outputPath, long items) =>
        $"Another export is still writing the temp file the resume checkpoint at '{checkpointPath}' "
        + $"names, so the {items} items it records are that run's and are not recovered here. Both "
        + $"files are left as they are and '{outputPath}' is exported from the beginning. Two "
        + "exports writing one -OutputFile replace each other's result; give each its own.";

    /// <summary>
    /// The form of this export's URL a checkpoint on disk was written under - whether the
    /// auto-added $count was on it and whether the automatic $top was - or null when no form of
    /// it is this export's. The attempt loop reaches each of these in turn, dropping $count on a
    /// bare 400 and $top on a Request_UnsupportedQuery, and a checkpoint records whichever URL
    /// was current when it was saved. Every form is put to the same comparison the loop makes,
    /// so a URL the loop can legitimately build is recognized as this export's and one it cannot
    /// is still refused. Ordered as the loop reaches them, so an unambiguous match is the one
    /// this run would have built first.
    /// </summary>
    private (bool IncludeCount, bool SuppressTop)? AttemptFormOf(
        PaginationCheckpoint? checkpoint, string outputPath, bool countAutoAdded)
    {
        if (checkpoint == null) return null;
        bool[] counts = countAutoAdded ? [true, false] : [false];
        bool[] tops = [false, true];
        foreach (var includeCount in counts)
        {
            foreach (var suppressTop in tops)
            {
                if (DescribesThisExport(checkpoint, outputPath, includeCount, suppressTop))
                    return (includeCount, suppressTop);
            }
        }
        return null;
    }

    /// <summary>
    /// True when a checkpoint on disk is about the export this run is making. The reconcile
    /// below promotes and trims files before any other part of the checkpoint is looked at, and
    /// a byte offset applies to whatever file it is handed - so a -CheckpointPath shared with a
    /// second export cut a file that checkpoint knows nothing about, mid-line, and the resumed
    /// pages were appended onto the torn byte. Two things have to agree: the output the writing
    /// run named - the whole path, since two exports to "users.jsonl" in different directories
    /// agree on the name and on nothing else, and on checkpoints too old to record one, the
    /// files on disk standing in for it - and the resource, path and query both. The query is
    /// half of what a resource is: -Top, -Filter and -Select change which items come back and
    /// in what order, so a checkpoint recorded under one of them counts a different enumeration
    /// than the one this run is making. Leaving it out put the exact comparison after the
    /// promoting and trimming instead of before it, which is not a comparison that can refuse.
    /// </summary>
    private bool DescribesThisExport(PaginationCheckpoint checkpoint, string outputPath,
        bool includeCount, bool suppressTop)
        => OwnershipOf(checkpoint, outputPath, includeCount, suppressTop) == CheckpointOwnership.Mine;

    /// <summary>
    /// The temp file a checkpoint this run refused still points at, or null. Refusing is the
    /// whole response: the checkpoint is left where it is because the export that wrote it
    /// resumes from exactly that, and the items it counted are in the temp it names.
    /// </summary>
    private string? _refusedCheckpointTemp;

    /// <summary>
    /// The temp a failed attempt of this run left on disk for a checkpoint of its own, or null.
    /// The attempt loop comes back round on a query the endpoint refused, and the sweep it
    /// reaches on the way is the one thing that can take that temp away while the checkpoint
    /// naming it is still there.
    ///
    /// It says nothing once that checkpoint has been replaced by one naming another file, or
    /// deleted by the run that finished: the temp is then a partial copy of the caller's data
    /// that nothing refers to, and it is deleted where the checkpoint changes rather than left
    /// beside the output for a later run's sweep.
    /// </summary>
    private string? _keptTempPath;

    /// <summary>
    /// Whether that temp is a file the stale-temp sweep would reach. The name comes off a
    /// checkpoint, which is untrusted once it is on disk, and nothing here opens it - all it
    /// decides is whether the sweep runs at all, so a name of some other shape is answered no
    /// rather than refused: the sweep cannot delete it either, and skipping the sweep for it
    /// would leave real orphans behind for nothing.
    ///
    /// "Some other shape" is the sweep's own test of a name and not a looser one kept here.
    /// Matching on the prefix and the suffix alone answers yes for "users.jsonl.{guid}.tmp"
    /// beside an output named "users" - a file the sweep passes over, because the glob's '*'
    /// spans dots and the sweep's own filter does not - so a checkpoint left by the run next
    /// door suppressed this output's sweep and its real orphans stayed on disk, which is the
    /// case this comment says is answered no.
    /// </summary>
    private static bool RefusedTempIsOnDisk(string? tempFile, string outputPath)
    {
        if (tempFile == null || tempFile != Path.GetFileName(tempFile)) return false;
        if (!IsRunTempName(Path.GetFileName(outputPath), tempFile)) return false;
        var dir = Path.GetDirectoryName(outputPath);
        return !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, tempFile));
    }

    /// <summary>Whose the checkpoint on disk is, and where it is not this export's, why not.</summary>
    private enum CheckpointOwnership
    {
        /// <summary>This export's.</summary>
        Mine,

        /// <summary>Another export's: it records another output, or another enumeration.</summary>
        AnotherExports,

        /// <summary>
        /// Records no output file, and the files beside this one no longer stand for it. Which
        /// export wrote it cannot be told from here, and telling the caller it was another one
        /// is a diagnosis they can go and check and find nothing behind.
        /// </summary>
        Uncorroborated,
    }

    /// <summary>
    /// Which of the three the checkpoint is. A checkpoint recording neither an output nor a
    /// length has nothing here to be weighed: RecordedOutputMatches needs a length to hold a
    /// file against, and answering "not yours" for want of one put the whole pre-2.1.0 shape
    /// behind a refusal, where the stale-temp sweep deleted the items it was pointing at. That
    /// shape is decided on the evidence it does have - a temp carrying this output's own name,
    /// beside an output that is not there - by the reconcile, which refuses it wherever an
    /// output exists. A recorded resource that is not a URL says nothing that can be matched
    /// and is refused with the enumerations that do not match.
    /// </summary>
    private CheckpointOwnership OwnershipOf(PaginationCheckpoint checkpoint, string outputPath,
        bool includeCount, bool suppressTop)
    {
        if ((checkpoint.OutputFile != null || checkpoint.DataLength != null)
            && !RecordedOutputMatches(checkpoint.OutputFile, checkpoint.TempFile,
                   checkpoint.DataLength, outputPath))
        {
            return checkpoint.OutputFile != null
                ? CheckpointOwnership.AnotherExports
                : CheckpointOwnership.Uncorroborated;
        }

        return SameResourceIdentity(ResourceIdentity(checkpoint.Resource),
                   ResourceIdentity(BuildUrl(includeCount, suppressTop)))
            ? CheckpointOwnership.Mine
            : CheckpointOwnership.AnotherExports;
    }

    /// <summary>
    /// Put the files into the state the checkpoint claims, or leave them alone, and answer
    /// whether the run that follows appends to the output rather than exporting into a fresh
    /// temp. With <paramref name="apply"/> false nothing is written, deleted or warned about:
    /// the ShouldProcess gate has to name the action before the run is allowed to take any, and
    /// -WhatIf has to leave every file it found exactly as it found it.
    ///
    /// A checkpoint records which file its items were written to and how many bytes of that
    /// file they occupy, which makes three cases decidable instead of guessed.
    ///
    /// A temp is named, so the interrupted run was fresh and its items are in that temp while
    /// the output still holds a PREVIOUS export. An export is a snapshot and a fresh run that
    /// finishes moves its temp over the output, so recovery promotes the temp the same way -
    /// appending would leave the previous export's rows in front of this one's.
    ///
    /// None is named, so the run was appending to the output and its items are already there,
    /// past the recorded length only if it wrote more after its last save. Cutting back to that
    /// length is what stops those from being written twice.
    ///
    /// Neither is recorded, so the checkpoint predates this and is handled as it was before.
    ///
    /// When the counted items turn out to be in no file, nothing has been promoted and no token
    /// has moved, so starting over costs a pass and loses nothing, while resuming past them
    /// would drop them from the output for good.
    /// </summary>
    private bool ReconcileCheckpoint(string? checkpointPath, string outputPath,
        bool includeCount, bool suppressTop, bool apply)
    {
        if (checkpointPath == null || !File.Exists(checkpointPath)) return false;

        var checkpoint = PaginationCheckpoint.Load(checkpointPath);
        if (checkpoint == null)
        {
            // Load answers null for a checkpoint that does not deserialize and for one that
            // cannot be read at all. Either way it says nothing about how much of the output is
            // already there, and the run below decides to append from the file merely EXISTING -
            // which appended a second complete export onto the first. A checkpoint that cannot
            // be read is no checkpoint.
            //
            // It is left where it is. The loop below forces a fresh export whenever the load
            // answers null, so deleting the file changes nothing this run does - and "cannot be
            // read" covers a lock and a denying ACL as well as a torn file, so it threw away a
            // position that the next run, or another account, could still have resumed from.
            if (apply)
            {
                WriteWarning(
                    "The resume checkpoint could not be read, so it cannot say what the output already holds. "
                    + "Exporting again from the beginning; nothing is lost.");
            }
            return false;
        }

        var ownership = OwnershipOf(checkpoint, outputPath, includeCount, suppressTop);
        if (ownership != CheckpointOwnership.Mine)
        {
            if (apply)
            {
                // What the refusal leaves has to survive the sweep in this same run.
                _refusedCheckpointTemp = checkpoint.TempFile;
                WriteWarning(ownership == CheckpointOwnership.AnotherExports
                    ? ForeignCheckpointWarning(checkpointPath, outputPath)
                    : UncorroboratedCheckpointWarning(checkpointPath, outputPath));
            }
            return false;
        }

        if (checkpoint.NextLink == null)
        {
            // Completion marker. With the output beside it the loop below deletes it and
            // exports fresh; without one it describes nothing at all.
            if (!File.Exists(outputPath) && apply)
            {
                WriteWarning("Checkpoint found but output file is missing. Deleting stale checkpoint and starting fresh.");
                PaginationCheckpoint.Delete(checkpointPath);
            }
            return false;
        }

        if (checkpoint.DataLength is not { } dataLength)
        {
            // Written before any of this was recorded. Adoption then has only a line count and
            // the newest matching temp to go on, which is safe to attempt only when there is no
            // output it could be merged into - exactly the case this path used to be limited to.
            if (!File.Exists(outputPath))
            {
                if (apply
                        ? TryAdoptOrphanedTemp(outputPath, checkpoint.ItemsCollected)
                        : CanAdoptOrphanedTemp(outputPath, checkpoint.ItemsCollected))
                {
                    if (apply)
                        WriteWarning($"Recovered {checkpoint.ItemsCollected} items from an interrupted export's temp file. Resuming from checkpoint.");
                    return true;
                }

                if (apply)
                {
                    WriteWarning("Checkpoint found but output file is missing. Deleting stale checkpoint and starting fresh.");
                    PaginationCheckpoint.Delete(checkpointPath);
                }
                return false;
            }

            // The output exists and the checkpoint cannot say whether its items are in it. Both
            // shapes are possible from a release that recorded neither field: a run that was
            // appending, whose items ARE there, and a fresh run killed mid-flight, whose items
            // are in a temp while the output still holds a PREVIOUS export. Resuming assumed the
            // first, so upgrading with the second on disk appended the remainder of the
            // enumeration onto the earlier export - a 100,000-row file coming back with 163,037.
            // Undecidable means start over: an export re-runs from the first page and replaces
            // the output, which costs a pass and cannot leave a wrong file behind.
            if (apply)
            {
                WriteWarning(
                    "The resume checkpoint does not record which file the interrupted export's items are in. "
                    + "Exporting again from the beginning; nothing is lost.");
                PaginationCheckpoint.Delete(checkpointPath);
            }
            return false;
        }

        if (checkpoint.TempFile != null)
        {
            if (apply
                    ? TryPromoteNamedTemp(outputPath, checkpoint.TempFile, dataLength)
                    : CanPromoteNamedTemp(outputPath, checkpoint.TempFile, dataLength))
            {
                if (apply)
                {
                    WriteWarning($"Recovered {checkpoint.ItemsCollected} items from an interrupted export's temp file. Resuming from checkpoint.");
                    // Those items are the output now. Repoint the checkpoint at it before
                    // anything else can fail, so a second interruption cannot promote the same
                    // temp again.
                    checkpoint.TempFile = null;
                    checkpoint.OutputFile = Path.GetFullPath(outputPath);
                    checkpoint.DataLength = new FileInfo(outputPath).Length;
                    try { checkpoint.Save(checkpointPath); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        WriteWarning($"Checkpoint save failed after recovery: {ex.Message}");
                    }
                }
                return true;
            }

            // The temp is there and whole, and another run has it open - so the export this
            // checkpoint belongs to is not interrupted at all, it is collecting into that file
            // right now. Nothing here is a recovery: the items are that run's, the checkpoint is
            // the position it comes back to, and this run exports from the beginning into a temp
            // of its own. Deleting the checkpoint below would be the same mistake as unlinking
            // the temp, one file over.
            if (NamedTempIsHeld(outputPath, checkpoint.TempFile, dataLength))
            {
                if (apply)
                {
                    // What the refusal leaves has to survive the sweep in this same run: the
                    // holder can be gone by the time the sweep runs, and the checkpoint left
                    // standing over that temp is what makes it recoverable rather than stale.
                    _refusedCheckpointTemp = checkpoint.TempFile;
                    WriteWarning(HeldTempWarning(checkpointPath, outputPath, checkpoint.ItemsCollected));
                }
                return false;
            }

            if (apply)
            {
                WriteWarning(
                    $"The interrupted export's temp file is missing or incomplete, so the {checkpoint.ItemsCollected} items it "
                    + "recorded are not on disk. Exporting again from the beginning; nothing is lost.");
                PaginationCheckpoint.Delete(checkpointPath);
            }
            return false;
        }

        if (apply
                ? TryTrimOutputToCheckpoint(outputPath, dataLength)
                : CanTrimOutputToCheckpoint(outputPath, dataLength))
            return true;

        if (apply)
        {
            WriteWarning(
                $"'{outputPath}' no longer holds the {checkpoint.ItemsCollected} items the resume checkpoint records. "
                + "Exporting again from the beginning; nothing is lost.");
            PaginationCheckpoint.Delete(checkpointPath);
        }
        return false;
    }
}
