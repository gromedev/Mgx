using System.Management.Automation;
using System.Net;
using System.Text;
using Mgx.Engine.Pagination;

namespace Mgx.IntegrationTests;

/// <summary>
/// Two syncs of the same -Uri build the same resource string, so the resource comparison that
/// guards recovery cannot tell them apart. What separates them is the file they collect into,
/// and a checkpoint that records a byte length used to record nothing about which file that
/// length was measured in - so a shared -CheckpointPath let one sync cut the other's output
/// back to its own offset, mid-line, and append the resumed page onto the torn byte.
/// </summary>
[Collection("Pipeline")]
public class DeltaSharedCheckpointTests
{
    private const string ChangesPage1 = """
    {"value":[{"id":"b1"},{"id":"b2"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/users/delta?$skiptoken=B2"}
    """;
    private const string ChangesPage2 = """
    {"value":[{"id":"b3"}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/users/delta?$deltatoken=D2"}
    """;
    private const string ServerError = """{"error":{"code":"InternalServerError","message":"boom"}}""";

    private const string GroupsPage = """
    {"value":[{"id":"g1"}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/groups/delta?$deltatoken=G1"}
    """;
    // A first page with a continuation, so an attempt can reach a page boundary - and save a
    // checkpoint of its own naming its own temp - before the request after it answers 410.
    private const string GroupsPageOne = """
    {"value":[{"id":"g1"},{"id":"g2"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/groups/delta?$skiptoken=G2"}
    """;
    private const string GroupsResync = """
    {"value":[{"id":"g9"}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/groups/delta?$deltatoken=G1"}
    """;
    private const string TokenExpired =
        """{"error":{"code":"deltaTokenExpired","message":"Delta token has expired"}}""";

    private static readonly string[] OldSync =
        ["{\"id\":\"old-0000001\"}", "{\"id\":\"old-0000002\"}", "{\"id\":\"old-0000003\"}"];

    /// <summary>A groups sync holding a token older than Graph keeps them.</summary>
    private static void StaleState(string deltaPath) =>
        new DeltaState
        {
            DeltaLink = "https://graph.microsoft.com/v1.0/groups/delta?$deltatoken=stale",
            Resource = "/groups/delta",
            GraphEndpoint = "https://graph.microsoft.com",
            Select = "",
            ApiVersion = "v1.0",
        }.Save(deltaPath);

    /// <summary>A users sync partway through, holding the token its last run was issued.</summary>
    private static void UsersState(string deltaPath) =>
        new DeltaState
        {
            DeltaLink = "https://graph.microsoft.com/v1.0/users/delta?$deltatoken=D0",
            Resource = "/users/delta",
            GraphEndpoint = "https://graph.microsoft.com",
            Select = "",
            ApiVersion = "v1.0",
        }.Save(deltaPath);

    private static void Sync(string deltaPath, string checkpointPath, string outputPath,
        string uri = "/users/delta")
    {
        using var ps = PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Delta.SyncMgxDelta).Assembly);
        ps.Invoke();
        ps.Commands.Clear();
        ps.AddCommand("Sync-MgxDelta")
          .AddParameter("Uri", uri)
          .AddParameter("DeltaPath", deltaPath)
          .AddParameter("CheckpointPath", checkpointPath)
          .AddParameter("OutputFile", outputPath);
        // A run that dies is part of the scenario, so a terminating error is expected.
        try { ps.Invoke(); }
        catch (CmdletInvocationException) { }
    }

    private static string[] SyncWarnings(string deltaPath, string checkpointPath, string outputPath,
        string uri = "/users/delta")
    {
        using var ps = PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Delta.SyncMgxDelta).Assembly);
        ps.Invoke();
        ps.Commands.Clear();
        ps.AddCommand("Sync-MgxDelta")
          .AddParameter("Uri", uri)
          .AddParameter("DeltaPath", deltaPath)
          .AddParameter("CheckpointPath", checkpointPath)
          .AddParameter("OutputFile", outputPath);
        try { ps.Invoke(); }
        catch (CmdletInvocationException) { }
        return [.. ps.Streams.Warning.Select(w => w.Message)];
    }

    /// <summary>
    /// Sync A is interrupted twice, so the checkpoint it leaves records a length with no temp
    /// to explain it. Sync B, its own delta state and its own output, must not have that
    /// length applied to its file.
    /// </summary>
    [Fact]
    public void A_checkpoint_from_another_sync_does_not_cut_this_ones_output()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-shared-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaA = Path.Combine(dir, "stateA.json");
        var deltaB = Path.Combine(dir, "stateB.json");
        var outA = Path.Combine(dir, "outA.jsonl");
        var outB = Path.Combine(dir, "outB.jsonl");

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);                 // A run 1, page 1
        handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError); // A run 1, page 2
        handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
        handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError); // A run 2 promotes, then dies
        handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
        handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);                 // B, page 1
        handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);                 // B, page 2
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            Sync(deltaA, checkpointPath, outA);
            Sync(deltaA, checkpointPath, outA);

            var cp = PaginationCheckpoint.Load(checkpointPath)!;
            Assert.Null(cp.TempFile);
            Assert.Equal(new FileInfo(outA).Length, cp.DataLength);

            // B has its own delta state and its own output, which already holds an earlier sync.
            File.WriteAllLines(outB, OldSync);
            Sync(deltaB, checkpointPath, outB);

            var lines = File.ReadAllLines(outB);
            Assert.All(lines, l => Assert.StartsWith("{\"id\":\"", l));
            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}"], lines);

            // A's own output is untouched by B.
            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}"], File.ReadAllLines(outA));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The same collision between two directories rather than two names. What distinguishes the
    /// syncs is which file they collect into, and a leaf comparison cannot see that: sync B
    /// adopted A's position and cut B's own output to A's byte count.
    /// </summary>
    [Fact]
    public void A_checkpoint_for_the_same_name_in_another_directory_does_not_cut_this_ones_output()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-shared-dir-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaA = Path.Combine(dir, "stateA.json");
        var deltaB = Path.Combine(dir, "stateB.json");
        var outA = Path.Combine(Directory.CreateDirectory(Path.Combine(dir, "a")).FullName, "out.jsonl");
        var outB = Path.Combine(Directory.CreateDirectory(Path.Combine(dir, "b")).FullName, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);                 // A run 1, page 1
        handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError); // A run 1, page 2
        handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
        handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError); // A run 2 promotes, then dies
        handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
        handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);                 // B, page 1
        handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);                 // B, page 2
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            Sync(deltaA, checkpointPath, outA);
            Sync(deltaA, checkpointPath, outA);

            var cp = PaginationCheckpoint.Load(checkpointPath)!;
            Assert.Null(cp.TempFile);
            Assert.Equal(new FileInfo(outA).Length, cp.DataLength);

            File.WriteAllLines(outB, OldSync);
            Sync(deltaB, checkpointPath, outB);

            var lines = File.ReadAllLines(outB);
            Assert.All(lines, l => Assert.StartsWith("{\"id\":\"", l));
            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}"], lines);
            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}"], File.ReadAllLines(outA));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The same sync, resumed with -Uri typed differently. Graph answers "/users/delta" and
    /// "/Users/delta" from one collection, so both runs enumerate the same thing - but the
    /// recorded resource was compared ordinally, which made the second run a different sync:
    /// its own position was refused as another's, the temp holding the changes the first run
    /// had already collected was swept, and the enumeration started over.
    /// </summary>
    [Fact]
    public void A_checkpoint_written_under_another_spelling_of_the_resource_still_resumes()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-spelling-{Guid.NewGuid():N}")).FullName;
        var deltaPath = Path.Combine(dir, "state.json");
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            // Page one reaches the temp; page two fails, so the position survives.
            Sync(deltaPath, checkpointPath, output, "/users/delta");
            Assert.NotNull(PaginationCheckpoint.Load(checkpointPath));
            Assert.Single(Directory.GetFiles(dir, "out.jsonl.*.tmp"));

            // The resume, typed the other way, with only the page the dead run never got.
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);
            var first = handler.CapturedRequests.Count;
            Sync(deltaPath, checkpointPath, output, "/Users/delta");

            Assert.Contains("skiptoken=B2", handler.CapturedRequests[first].Uri);
            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}"],
                File.ReadAllLines(output));
            var state = DeltaState.Load(deltaPath);
            Assert.NotNull(state);
            Assert.Contains("$deltatoken=D2", state!.DeltaLink);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// A sync writing to the pipeline was outside the ownership check entirely, so a checkpoint
    /// left by a sync collecting into a file resumed it at that sync's nextLink. Every page
    /// before that one went to the file and never to the pipeline, and the delta token was
    /// saved over them on success - which is the one failure a delta sync cannot recover from,
    /// since there is no re-fetch once the token moves.
    /// </summary>
    [Fact]
    public void A_pipeline_sync_does_not_resume_from_a_checkpoint_that_collected_into_a_file()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-mode-{Guid.NewGuid():N}")).FullName;
        var deltaPath = Path.Combine(dir, "state.json");
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        try
        {
            // The other sync's position: page 1 is already in its file, page 2 is what remains.
            new PaginationCheckpoint
            {
                Resource = "https://graph.microsoft.com/v1.0/users/delta?$top=999",
                NextLink = "https://graph.microsoft.com/v1.0/users/delta?$skiptoken=B2",
                ItemsCollected = 2,
                PageItemsAlreadyWritten = 0,
                TempFile = null,
                OutputFile = Path.Combine(dir, "elsewhere.jsonl"),
                DataLength = 24,
            }.Save(checkpointPath);

            using var transport = MgxTransportScope.Inject(new ByUrlHandler());

            using var ps = PowerShell.Create();
            ps.AddCommand("Import-Module")
              .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Delta.SyncMgxDelta).Assembly);
            ps.Invoke();
            ps.Commands.Clear();
            ps.AddCommand("Sync-MgxDelta")
              .AddParameter("Uri", "/users/delta")
              .AddParameter("DeltaPath", deltaPath)
              .AddParameter("CheckpointPath", checkpointPath);
            var emitted = ps.Invoke()
                .Select(r => ((System.Collections.Hashtable)r.BaseObject)["id"]!.ToString()!)
                .ToArray();

            // Every change, not just the ones after the other sync's position.
            Assert.Equal(["b1", "b2", "b3"], emitted);
            // Only now may a token stand for this enumeration.
            var state = DeltaState.Load(deltaPath);
            Assert.NotNull(state);
            Assert.Contains("$deltatoken=D2", state!.DeltaLink);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The same checkpoint from a release that did not record the output file yet. A file-mode
    /// run names no temp while it is appending, and none once a cancellation has promoted the
    /// one it had, so all such a checkpoint says about where its items went is the length it
    /// counted. A pipeline sync adopted every one of those: it emitted the tail of the
    /// enumeration, saved the delta token over everything before it, and there is no re-fetch
    /// once the token has moved. A pipeline run measures no file, so a length is the file mode.
    /// </summary>
    [Fact]
    public void A_pipeline_sync_does_not_resume_from_a_checkpoint_that_records_only_a_length()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-legacy-{Guid.NewGuid():N}")).FullName;
        var deltaPath = Path.Combine(dir, "state.json");
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        try
        {
            // The other sync's position, in the shape 2.1.3 and before wrote it.
            new PaginationCheckpoint
            {
                Resource = "https://graph.microsoft.com/v1.0/users/delta?$top=999",
                NextLink = "https://graph.microsoft.com/v1.0/users/delta?$skiptoken=B2",
                ItemsCollected = 2,
                PageItemsAlreadyWritten = 0,
                TempFile = null,
                OutputFile = null,
                DataLength = 24,
            }.Save(checkpointPath);

            using var transport = MgxTransportScope.Inject(new ByUrlHandler());

            using var ps = PowerShell.Create();
            ps.AddCommand("Import-Module")
              .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Delta.SyncMgxDelta).Assembly);
            ps.Invoke();
            ps.Commands.Clear();
            ps.AddCommand("Sync-MgxDelta")
              .AddParameter("Uri", "/users/delta")
              .AddParameter("DeltaPath", deltaPath)
              .AddParameter("CheckpointPath", checkpointPath);
            var emitted = ps.Invoke()
                .Select(r => ((System.Collections.Hashtable)r.BaseObject)["id"]!.ToString()!)
                .ToArray();

            // Every change, not just the ones after the other sync's position.
            Assert.Equal(["b1", "b2", "b3"], emitted);
            // Only now may a token stand for this enumeration.
            var state = DeltaState.Load(deltaPath);
            Assert.NotNull(state);
            Assert.Contains("$deltatoken=D2", state!.DeltaLink);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// How far "it is left as it is" has to reach. The refused checkpoint's items are in the
    /// temp it names, and the stale-temp sweep a few lines later deleted every temp beside the
    /// output - including that one. The sync it belongs to then came back to a position
    /// pointing at a file that is gone and re-enumerated from the delta token, which is the
    /// whole cost the refusal was written to avoid.
    /// </summary>
    [Fact]
    public void A_refused_checkpoints_temp_survives_the_run_that_refused_it()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-refused-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaA = Path.Combine(dir, "stateA.json");
        var deltaB = Path.Combine(dir, "stateB.json");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            // Sync A dies on page 2: its two items are in its temp, the output was never
            // promoted, and the checkpoint names both.
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            Sync(deltaA, checkpointPath, output);

            var cp = PaginationCheckpoint.Load(checkpointPath)!;
            Assert.NotNull(cp.TempFile);
            var temp = Path.Combine(dir, cp.TempFile!);
            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}"], File.ReadAllLines(temp));
            var before = File.ReadAllBytes(checkpointPath);

            // Sync B: another collection, its own delta state, the same -OutputFile and
            // -CheckpointPath, failing before its first page boundary.
            Sync(deltaB, checkpointPath, output, "/groups/delta");

            Assert.Equal(before, File.ReadAllBytes(checkpointPath));
            Assert.True(File.Exists(temp),
                "the refused checkpoint's items were swept away by the run that refused it");

            // A, back where it left off, rather than at the delta token.
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);
            Sync(deltaA, checkpointPath, output);
            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}"],
                File.ReadAllLines(output));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// How far "spare the temps a refused checkpoint names" reaches. The sweep only ever
    /// deletes names a run gives its own temp - the output's name, a dot, 32 hex digits,
    /// ".tmp" - so a refused checkpoint naming "out.jsonl.{guid}.tmp" beside an output called
    /// "out" names a file this sweep could not touch either way. Skipping on it bought that
    /// file nothing and cost this output its own orphans, which the pre-length adoption path
    /// then picks up on a line count alone.
    /// </summary>
    [Fact]
    public void A_refused_temp_this_sweep_could_never_reach_does_not_spare_this_outputs_orphans()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-sweep-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaA = Path.Combine(dir, "stateA.json");
        var deltaB = Path.Combine(dir, "stateB.json");
        var mine = Path.Combine(dir, "out");
        var theirs = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            // The sync into "out" collects page one into its temp and dies on page two.
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            Sync(deltaA, checkpointPath, mine);
            var orphan = Assert.Single(Directory.GetFiles(dir, "out.*.tmp"));

            // The sync next door takes the shared checkpoint over: it refuses what it finds,
            // saves its own position at its first page boundary, and dies on page two. The
            // first sync's temp is now an orphan - nothing on disk describes it.
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            Sync(deltaB, checkpointPath, theirs);
            var theirTemp = Assert.Single(Directory.GetFiles(dir, "out.jsonl.*.tmp"));

            // "out" again. It refuses a checkpoint naming a temp beside a different output,
            // and that name must not stand in the way of its own sweep.
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);
            Sync(deltaA, checkpointPath, mine);

            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}"],
                File.ReadAllLines(mine));
            Assert.False(File.Exists(orphan),
                "a temp name the sweep could never reach suppressed this output's sweep");
            Assert.True(File.Exists(theirTemp),
                "the refused checkpoint's own staging file was deleted");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// What each refusal says. A checkpoint recording another sync's output is a second sync
    /// over one -CheckpointPath, and naming that is what the caller acts on. One recording no
    /// output at all is not: it was believed while the files beside it corroborated it, and a
    /// single sync whose temp has since gone, or whose output has been replaced, reaches the
    /// same refusal. Every release before this one recorded no output, so that is the ordinary
    /// upgrade path - and it was told there was a second sync somewhere and to give each its
    /// own -CheckpointPath, which fixes nothing it has.
    /// </summary>
    [Fact]
    public void A_refusal_reports_the_cause_it_can_show()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-refusal-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaPath = Path.Combine(dir, "state.json");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            File.WriteAllLines(output, OldSync);

            // The shape an interrupted fresh sync left before outputs were recorded, whose temp
            // is no longer beside it.
            new PaginationCheckpoint
            {
                Resource = "https://graph.microsoft.com/v1.0/users/delta?$top=999",
                NextLink = "https://graph.microsoft.com/v1.0/users/delta?$skiptoken=B2",
                ItemsCollected = 2,
                PageItemsAlreadyWritten = 0,
                TempFile = $"out.jsonl.{Guid.NewGuid():N}.tmp",
                OutputFile = null,
                DataLength = 24,
            }.Save(checkpointPath);

            var uncorroborated = SyncWarnings(deltaPath, checkpointPath, output);
            Assert.Contains(uncorroborated, w => w.Contains("no longer corroborate"));
            Assert.DoesNotContain(uncorroborated,
                w => w.Contains("records an enumeration this run cannot resume from"));

            // The same position, recording an output this run is not collecting into.
            new PaginationCheckpoint
            {
                Resource = "https://graph.microsoft.com/v1.0/users/delta?$top=999",
                NextLink = "https://graph.microsoft.com/v1.0/users/delta?$skiptoken=B2",
                ItemsCollected = 2,
                PageItemsAlreadyWritten = 0,
                TempFile = null,
                OutputFile = Path.Combine(dir, "elsewhere.jsonl"),
                DataLength = 24,
            }.Save(checkpointPath);

            Assert.Contains(SyncWarnings(deltaPath, checkpointPath, output),
                w => w.Contains("records an enumeration this run cannot resume from"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// And the cause it cannot show. A run interrupted partway through an incremental sync
    /// leaves a checkpoint recording the delta URL it was enumerating. Lose the state under it -
    /// corrupt, deleted by hand, a -DeltaPath moved - and the same command line comes back
    /// building a full-sync URL, which is not that one, so it refuses its own earlier position.
    /// The refusal is right; the reading was not. It named a different sync, over a
    /// -CheckpointPath no second sync has ever touched, and told the caller to give each of them
    /// its own - which is the arrangement they were already running.
    /// </summary>
    [Fact]
    public void A_refusal_does_not_name_a_second_sync_it_cannot_see()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-selfrefusal-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaPath = Path.Combine(dir, "state.json");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            // One sync, one -CheckpointPath: interrupted on page 2 of its incremental pass, so
            // what it leaves records the delta URL it was reading.
            UsersState(deltaPath);
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            Sync(deltaPath, checkpointPath, output);
            Assert.Contains("$deltatoken=D0", PaginationCheckpoint.Load(checkpointPath)!.Resource);

            // The state goes, and the same command line enumerates in full.
            File.Delete(deltaPath);
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);
            var refusal = Assert.Single(
                SyncWarnings(deltaPath, checkpointPath, output),
                w => w.Contains(checkpointPath));

            Assert.Contains("records an enumeration this run cannot resume from", refusal);
            Assert.Contains("either another sync's, sharing this -CheckpointPath", refusal);
            Assert.Contains("this sync's own from a pass it no longer makes", refusal);
            Assert.DoesNotContain("belongs to a different sync", refusal);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// A 410 says the token this run holds is dead. It says nothing about a checkpoint this
    /// same run has already decided belongs to another sync, and deleting that one took the
    /// other sync's position away seconds after warning that it would be left alone - while the
    /// retry, running the stale-temp sweep a second time with the refusal forgotten, took the
    /// temp holding its items too. Both halves of the other sync's progress, on the one path
    /// that promised to touch neither.
    /// </summary>
    [Fact]
    public void An_expired_token_does_not_delete_the_checkpoint_this_run_refused()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-410refused-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaA = Path.Combine(dir, "stateA.json");
        var deltaB = Path.Combine(dir, "stateB.json");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            // Sync A dies on page 2: its two items are in its temp, and the checkpoint names
            // both files.
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            Sync(deltaA, checkpointPath, output);

            var cp = PaginationCheckpoint.Load(checkpointPath)!;
            var temp = Path.Combine(dir, cp.TempFile!);
            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}"], File.ReadAllLines(temp));
            var before = File.ReadAllBytes(checkpointPath);

            // Sync B: another collection over the same -CheckpointPath and -OutputFile, holding
            // a token Graph answers 410 for. It refuses A's checkpoint, re-syncs in full, and
            // dies before a page boundary of its own.
            StaleState(deltaB);
            handler.QueueResponse(HttpStatusCode.Gone, TokenExpired);
            Sync(deltaB, checkpointPath, output, "/groups/delta");

            Assert.True(File.Exists(checkpointPath),
                "the expired token deleted a checkpoint this run had refused as another sync's");
            Assert.Equal(before, File.ReadAllBytes(checkpointPath));
            Assert.True(File.Exists(temp),
                "the retry swept away the temp the refusal a moment earlier had spared");

            // A, back where it left off, rather than at the delta token.
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);
            Sync(deltaA, checkpointPath, output);
            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}"],
                File.ReadAllLines(output));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// And the run that refused still gets its own sync done: the 410 retry enumerates in full,
    /// collects into the output and saves the token it was issued, with the other sync's temp
    /// still where the refusal left it.
    /// </summary>
    [Fact]
    public void A_full_resync_after_a_refusal_still_completes_its_own_sync()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-410done-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaA = Path.Combine(dir, "stateA.json");
        var deltaB = Path.Combine(dir, "stateB.json");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            Sync(deltaA, checkpointPath, output);
            var temp = Path.Combine(dir, PaginationCheckpoint.Load(checkpointPath)!.TempFile!);

            StaleState(deltaB);
            handler.QueueResponse(HttpStatusCode.Gone, TokenExpired);
            handler.QueueResponse(HttpStatusCode.OK, GroupsPage);
            Sync(deltaB, checkpointPath, output, "/groups/delta");

            Assert.Equal(["{\"id\":\"g1\"}"], File.ReadAllLines(output));
            Assert.Contains("$deltatoken=G1", DeltaState.Load(deltaB)!.DeltaLink);
            Assert.True(File.Exists(temp),
                "the retry swept away the temp the refusal a moment earlier had spared");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// What the run that refused leaves beside its own output. The attempt a 410 sends round the
    /// loop again keeps its temp - a checkpoint counting those items is on disk - and the retry
    /// spares it, because the sweep is held off over every temp beside this output for as long
    /// as the refusal stands. What ends the holding off was never written: the checkpoint is
    /// replaced by the retry's, or deleted by the run that completed, without a word about the
    /// file it used to name. So a sync that promoted its output and reported success left a
    /// partial copy of the changes it had already collected beside the finished file, for some
    /// later sync's sweep to reclaim.
    /// </summary>
    [Fact]
    public void A_full_resync_after_a_refusal_leaves_no_temp_of_its_own()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-410kept-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaA = Path.Combine(dir, "stateA.json");
        var deltaB = Path.Combine(dir, "stateB.json");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            // Sync A dies on page 2: the checkpoint names the temp holding its two items.
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            Sync(deltaA, checkpointPath, output);
            var theirs = PaginationCheckpoint.Load(checkpointPath)!.TempFile!;

            // Sync B refuses that checkpoint, collects a page of its own into a temp, and loses
            // its token to a 410 on the request after it. The retry re-syncs in full.
            StaleState(deltaB);
            handler.QueueResponse(HttpStatusCode.OK, GroupsPageOne);
            handler.QueueResponse(HttpStatusCode.Gone, TokenExpired);
            handler.QueueResponse(HttpStatusCode.OK, GroupsResync);
            Sync(deltaB, checkpointPath, output, "/groups/delta");

            Assert.Equal(["{\"id\":\"g9\"}"], File.ReadAllLines(output));
            Assert.Contains("$deltatoken=G1", DeltaState.Load(deltaB)!.DeltaLink);

            // The one temp beside the output is the refused checkpoint's, which this run had no
            // business touching either way.
            var left = Assert.Single(Directory.GetFiles(dir, "out.jsonl.*.tmp"));
            Assert.Equal(theirs, Path.GetFileName(left));
            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}"], File.ReadAllLines(left));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The same compound with the 410 on the first request of all, so the attempt that dies has
    /// saved no checkpoint and its temp is empty. The keep asked only whether a checkpoint was
    /// on disk, and the one there is the refused sync's: it counts nothing this run wrote and
    /// names a temp of its own, so an empty file was kept on a foreign file's account and left
    /// beside the output the retry went on to finish.
    /// </summary>
    [Fact]
    public void An_attempt_that_saved_no_checkpoint_keeps_no_temp_on_a_refused_ones_account()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-410empty-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaA = Path.Combine(dir, "stateA.json");
        var deltaB = Path.Combine(dir, "stateB.json");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            Sync(deltaA, checkpointPath, output);
            var theirs = PaginationCheckpoint.Load(checkpointPath)!.TempFile!;

            StaleState(deltaB);
            handler.QueueResponse(HttpStatusCode.Gone, TokenExpired);
            handler.QueueResponse(HttpStatusCode.OK, GroupsResync);
            Sync(deltaB, checkpointPath, output, "/groups/delta");

            Assert.Equal(["{\"id\":\"g9\"}"], File.ReadAllLines(output));
            var left = Assert.Single(Directory.GetFiles(dir, "out.jsonl.*.tmp"));
            Assert.Equal(theirs, Path.GetFileName(left));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// And the temp goes at the moment it stops being anything, not at the end of the run. The
    /// retry saves its own first page-boundary checkpoint, which names its own temp - so the
    /// one the attempt before it left is from then on counted by nothing - and then dies. What
    /// is beside the output is the temp the checkpoint names, and the refused sync's.
    /// </summary>
    [Fact]
    public void A_retry_deletes_the_temp_its_own_checkpoint_has_stopped_naming()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-410release-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaA = Path.Combine(dir, "stateA.json");
        var deltaB = Path.Combine(dir, "stateB.json");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            Sync(deltaA, checkpointPath, output);
            var theirs = PaginationCheckpoint.Load(checkpointPath)!.TempFile!;

            // The retry reaches a page boundary of its own and dies on the page after it.
            StaleState(deltaB);
            handler.QueueResponse(HttpStatusCode.OK, GroupsPageOne);
            handler.QueueResponse(HttpStatusCode.Gone, TokenExpired);
            handler.QueueResponse(HttpStatusCode.OK, GroupsPageOne);
            Sync(deltaB, checkpointPath, output, "/groups/delta");

            var cp = PaginationCheckpoint.Load(checkpointPath);
            Assert.NotNull(cp);
            Assert.NotNull(cp!.TempFile);
            Assert.NotEqual(theirs, cp.TempFile);

            var temps = Directory.GetFiles(dir, "out.jsonl.*.tmp")
                .Select(Path.GetFileName).Order().ToArray();
            Assert.Equal(new[] { theirs, cp.TempFile }.Order().ToArray(), temps);
            Assert.Equal(["{\"id\":\"g1\"}", "{\"id\":\"g2\"}"],
                File.ReadAllLines(Path.Combine(dir, cp.TempFile!)));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The other end of it: the retry dies before a page boundary of its own. The temp the
    /// attempt before it filled is what an interrupted run leaves and stays where it is, while
    /// the retry's own, which nothing counts, is the one that goes. The position that counted
    /// the first goes with the token it belongs to - the 410 door deletes it, because the
    /// attempt that took the path over wrote it, and it enumerates what Graph has just declared
    /// gone.
    /// </summary>
    [Fact]
    public void A_retry_that_dies_leaves_the_temp_its_own_attempt_filled()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-410dead-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaA = Path.Combine(dir, "stateA.json");
        var deltaB = Path.Combine(dir, "stateB.json");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            Sync(deltaA, checkpointPath, output);
            var theirs = PaginationCheckpoint.Load(checkpointPath)!.TempFile!;

            StaleState(deltaB);
            handler.QueueResponse(HttpStatusCode.OK, GroupsPageOne);
            handler.QueueResponse(HttpStatusCode.Gone, TokenExpired);
            Sync(deltaB, checkpointPath, output, "/groups/delta");

            Assert.False(File.Exists(checkpointPath),
                "the expired token spared a position of this run's own into the dead enumeration");

            var temps = Directory.GetFiles(dir, "out.jsonl.*.tmp")
                .Select(Path.GetFileName).Order().ToArray();
            Assert.Equal(2, temps.Length);
            var mine = temps.Single(t => t != theirs);
            Assert.Equal(["{\"id\":\"g1\"}", "{\"id\":\"g2\"}"],
                File.ReadAllLines(Path.Combine(dir, mine!)));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// And what the run after it finds. The refusal is decided against the file that was on
    /// disk when the run started, and the very next page boundary saves this run's own position
    /// over that path - so by the time the 410 door reads the refusal, the file it spares is
    /// this run's, recording the enumeration Graph has just declared gone. The retry then dies
    /// before saving a position of its own, and the door has already deleted the delta state,
    /// so the next invocation of the same command line started with nothing to resume from and
    /// found that checkpoint waiting. It refused it - correctly; it enumerates something this
    /// command line can no longer build - and warned about a sync that was never there, while
    /// holding off its stale-temp sweep for a second run over files nothing counted any more.
    /// </summary>
    [Fact]
    public void A_refused_position_this_run_wrote_over_goes_with_the_expired_token()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-410tookover-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaA = Path.Combine(dir, "stateA.json");
        var deltaB = Path.Combine(dir, "stateB.json");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            // Sync A dies on page 2: the checkpoint names the temp holding its two items.
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            Sync(deltaA, checkpointPath, output);
            var theirs = PaginationCheckpoint.Load(checkpointPath)!.TempFile!;

            // Sync B refuses that checkpoint, reaches a page boundary of its own - which saves
            // over the same path - loses its token to a 410, and dies again in the re-sync
            // before it can record a position of the enumeration it is now making.
            StaleState(deltaB);
            handler.QueueResponse(HttpStatusCode.OK, GroupsPageOne);
            handler.QueueResponse(HttpStatusCode.Gone, TokenExpired);
            Sync(deltaB, checkpointPath, output, "/groups/delta");

            Assert.False(File.Exists(checkpointPath),
                "the 410 door spared a position this run had written over the refused one");
            Assert.True(File.Exists(Path.Combine(dir, theirs)),
                "the retry swept away the temp the refusal a moment earlier had spared");
            Assert.False(File.Exists(deltaB), "the expired delta state outlived the 410");

            // The next invocation of B's command line, with nothing of its own left to resume
            // from: no checkpoint to refuse, so no warning about a sync that was never there,
            // and the sweep it no longer holds off reclaims what the interrupted run left.
            handler.QueueResponse(HttpStatusCode.OK, GroupsResync);
            var warnings = SyncWarnings(deltaB, checkpointPath, output, "/groups/delta");

            Assert.DoesNotContain(warnings, w => w.Contains("resume checkpoint"));
            Assert.Equal(["{\"id\":\"g9\"}"], File.ReadAllLines(output));
            Assert.Contains("$deltatoken=G1", DeltaState.Load(deltaB)!.DeltaLink);
            Assert.Empty(Directory.GetFiles(dir, "out.jsonl.*.tmp"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The other branch of the same door, which is the refusal's promise where it still means
    /// something: the 410 arrives on the first request of all, so no attempt of this run has
    /// saved anything over the path and what is there is still the position that was refused.
    /// It is left exactly as it was found, byte for byte, and it is still the other sync's
    /// enumeration rather than this one's.
    /// </summary>
    [Fact]
    public void An_expired_token_spares_the_refused_position_no_attempt_wrote_over()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-410intact-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaA = Path.Combine(dir, "stateA.json");
        var deltaB = Path.Combine(dir, "stateB.json");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            Sync(deltaA, checkpointPath, output);
            var theirs = PaginationCheckpoint.Load(checkpointPath)!.TempFile!;
            var before = File.ReadAllBytes(checkpointPath);

            // The same compound as above, differing only in reaching no page boundary before
            // the 410 - so the door decides over a path this run has not written to.
            StaleState(deltaB);
            handler.QueueResponse(HttpStatusCode.Gone, TokenExpired);
            Sync(deltaB, checkpointPath, output, "/groups/delta");

            Assert.Equal(before, File.ReadAllBytes(checkpointPath));
            var cp = PaginationCheckpoint.Load(checkpointPath);
            Assert.NotNull(cp);
            Assert.Contains("/users/delta", cp!.Resource);
            Assert.Equal(theirs, cp.TempFile);
            Assert.True(File.Exists(Path.Combine(dir, theirs)),
                "the retry swept away the temp the refusal a moment earlier had spared");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// Answers by URL rather than in order, so what the sync asks for first is what decides
    /// which items it receives.
    /// </summary>
    private sealed class ByUrlHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri!.ToString().Contains("skiptoken=B2", StringComparison.Ordinal)
                ? ChangesPage2
                : ChangesPage1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
