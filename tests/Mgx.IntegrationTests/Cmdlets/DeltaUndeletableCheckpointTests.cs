using System.Management.Automation;
using System.Net;
using System.Text;
using Mgx.Engine.Pagination;

namespace Mgx.IntegrationTests;

/// <summary>
/// A recovery that gives up on the checkpoint has to give up on it whether or not the file goes
/// away. Every bail-out in the reconcile deletes the checkpoint and says what it does instead -
/// "Re-enumerating from the last saved delta token; no changes are lost" - and that delete can
/// fail: a checkpoint directory this account may read but not unlink from is the ordinary case,
/// and PaginationCheckpoint.Delete answers false for exactly it. The refusal used to be recorded
/// by nothing but the file's absence, so a failed delete left the run reloading the checkpoint it
/// had just given up on, resuming from its nextLink, putting the previous sync's rows in front of
/// this one's, and saving a delta token past items that are then in no file at all - under a
/// warning promising the opposite.
///
/// Export-MgxCollection never had this: every bail-out in its reconcile returns false, and that
/// bool is what decides whether the run appends. These pin the same shape for the delta sync.
/// </summary>
[Collection("Pipeline")]
public class DeltaUndeletableCheckpointTests
{
    private const string Baseline = """
    {"value":[{"id":"a1"},{"id":"a2"}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/users/delta?$deltatoken=D1"}
    """;
    private const string ChangesPage1 = """
    {"value":[{"id":"b1"},{"id":"b2"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/users/delta?$skiptoken=B2"}
    """;
    private const string ChangesPage2 = """
    {"value":[{"id":"b3"}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/users/delta?$deltatoken=D2"}
    """;
    private const string ServerError = """{"error":{"code":"InternalServerError","message":"boom"}}""";

    private static readonly string[] ThisSyncsRows =
        ["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}"];

    /// <summary>
    /// Answers by URL rather than in order, so what the sync asks for first is what decides
    /// which items it receives: a resume from the checkpoint's skiptoken gets what that
    /// skiptoken addresses and nothing before it, which is the whole difference between
    /// resuming and re-enumerating. Dies on that page while <see cref="DieOnResume"/> is set,
    /// which is how the interrupted run that leaves the checkpoint dies.
    /// </summary>
    private sealed class ByUrlHandler : HttpMessageHandler
    {
        private readonly List<string> _uris = [];

        public bool DieOnResume { get; set; }

        public List<string> Uris { get { lock (_uris) { return [.. _uris]; } } }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            lock (_uris) { _uris.Add(uri); }

            var (status, body) = uri.Contains("skiptoken=B2", StringComparison.Ordinal)
                ? DieOnResume
                    ? (HttpStatusCode.InternalServerError, ServerError)
                    : (HttpStatusCode.OK, ChangesPage2)
                : uri.Contains("$deltatoken=D1", StringComparison.Ordinal)
                    ? (HttpStatusCode.OK, ChangesPage1)
                    : (HttpStatusCode.OK, Baseline);

            return Task.FromResult(new HttpResponseMessage(status)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private static PowerShell Shell()
    {
        var ps = PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Delta.SyncMgxDelta).Assembly);
        ps.Invoke();
        ps.Commands.Clear();
        ps.AddScript("function Get-MgContext { [PSCustomObject]@{ TenantId = 'test-tenant-00000000-0000-0000-0000-000000000000' } }");
        ps.Invoke();
        ps.Commands.Clear();
        return ps;
    }

    /// <summary>The warnings the run wrote. A run that dies is setup here, not a failure.</summary>
    private static List<string> Sync(string deltaPath, string checkpointPath, string outputPath)
    {
        using var ps = Shell();
        ps.AddCommand("Sync-MgxDelta")
          .AddParameter("Uri", "/users/delta")
          .AddParameter("DeltaPath", deltaPath)
          .AddParameter("CheckpointPath", checkpointPath)
          .AddParameter("OutputFile", outputPath);
        try { ps.Invoke(); }
        catch (CmdletInvocationException) { }
        return [.. ps.Streams.Warning.Select(w => w.Message)];
    }

    private static string NewDir() => Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"mgx-undeletable-{Guid.NewGuid():N}")).FullName;

    /// <summary>
    /// Run 1 completes and records the D1 token; run 2 dies on the second page, leaving a
    /// checkpoint that counts two items into the temp it names. That is the state every test
    /// here starts from.
    /// </summary>
    private static void BaselineThenDeath(
        ByUrlHandler handler, string deltaPath, string checkpointPath, string outputPath)
    {
        Sync(deltaPath, checkpointPath, outputPath);
        handler.DieOnResume = true;
        Sync(deltaPath, checkpointPath, outputPath);
        handler.DieOnResume = false;
    }

    /// <summary>
    /// Make the checkpoint's directory one this account can read but not unlink from, which is
    /// what makes PaginationCheckpoint.Delete answer false. Answers false itself where the host
    /// does not enforce that - root ignores the mode bits, and there is no undeletable
    /// checkpoint to be had there, so the caller returns rather than asserting something the
    /// host cannot show.
    /// </summary>
    private static bool TrySeal(string dir)
    {
        if (OperatingSystem.IsWindows()) return false;
        var canary = Path.Combine(dir, "deletable.probe");
        File.WriteAllText(canary, "");
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try { File.Delete(canary); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
        Unseal(dir);
        return false;
    }

    private static void Unseal(string dir)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(dir,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>
    /// The counted items are in no file - the temp holding them is gone - and the checkpoint
    /// naming it cannot be deleted. The bail-out's warning says this run re-enumerates from the
    /// delta token, so that is what has to go on the wire: the two items the temp lost come
    /// back, and the output holds this sync's rows rather than the previous sync's with the
    /// remainder of this one behind them.
    /// </summary>
    [Fact]
    public void A_missing_temp_bailout_reenumerates_when_the_checkpoint_cannot_be_deleted()
    {
        // The checkpoint sits in a directory the run cannot write, which is what makes its
        // deletion fail. Windows expresses that through ACLs, not a mode.
        if (OperatingSystem.IsWindows()) return;

        var dir = NewDir();
        var vault = Directory.CreateDirectory(Path.Combine(dir, "vault")).FullName;
        var deltaPath = Path.Combine(dir, "state.json");
        var checkpointPath = Path.Combine(vault, "run.checkpoint");
        var outputPath = Path.Combine(dir, "out.jsonl");

        var handler = new ByUrlHandler();
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            BaselineThenDeath(handler, deltaPath, checkpointPath, outputPath);

            var checkpoint = PaginationCheckpoint.Load(checkpointPath)!;
            Assert.Contains("skiptoken=B2", checkpoint.NextLink);
            Assert.Equal(2, checkpoint.ItemsCollected);
            Assert.NotNull(checkpoint.TempFile);

            // The temp holding b1 and b2 goes: a tmp sweeper, a container restart, a hand. The
            // checkpoint still names it and still counts them.
            foreach (var t in Directory.GetFiles(dir, "out.jsonl.*.tmp")) File.Delete(t);
            if (!TrySeal(vault)) return;

            var before = handler.Uris.Count;
            var warnings = Sync(deltaPath, checkpointPath, outputPath);
            Unseal(vault);

            Assert.Contains(warnings, w => w.Contains("temp file is missing or incomplete"));
            Assert.True(File.Exists(checkpointPath),
                "the checkpoint was deletable after all, so no undeletable one was tested");

            var wire = handler.Uris.Skip(before).ToList();
            Assert.True(wire[0].Contains("$deltatoken=D1", StringComparison.Ordinal),
                $"the run resumed the checkpoint it gave up on; first request: {wire[0]}");
            Assert.DoesNotContain("skiptoken=B2", wire[0]);

            // "No changes are lost": b1 and b2 are back, and the previous sync's rows are not
            // in front of them.
            Assert.Equal(ThisSyncsRows, File.ReadAllLines(outputPath));
            Assert.Contains("$deltatoken=D2", DeltaState.Load(deltaPath)!.DeltaLink);
        }
        finally
        {
            try { Unseal(vault); } catch { }
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The other bail-out a sync reaches with a checkpoint it wrote for itself: the checkpoint
    /// counts its items into the output, and the output no longer holds them, so there is
    /// nothing to trim back to. Same undeletable checkpoint, same promise, same properties -
    /// and here a resume appends the remainder of the enumeration onto a file that never held
    /// its first pages.
    /// </summary>
    [Fact]
    public void A_trim_bailout_reenumerates_when_the_checkpoint_cannot_be_deleted()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = NewDir();
        var vault = Directory.CreateDirectory(Path.Combine(dir, "vault")).FullName;
        var deltaPath = Path.Combine(dir, "state.json");
        var checkpointPath = Path.Combine(vault, "run.checkpoint");
        var outputPath = Path.Combine(dir, "out.jsonl");

        var handler = new ByUrlHandler();
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            BaselineThenDeath(handler, deltaPath, checkpointPath, outputPath);

            // The shape a promoted cancellation leaves: the items are in the output itself, and
            // the checkpoint records how many bytes of it they occupy.
            File.WriteAllText(outputPath, "{\"id\":\"b1\"}\n{\"id\":\"b2\"}\n");
            foreach (var t in Directory.GetFiles(dir, "out.jsonl.*.tmp")) File.Delete(t);
            var checkpoint = PaginationCheckpoint.Load(checkpointPath)!;
            checkpoint.TempFile = null;
            checkpoint.OutputFile = Path.GetFullPath(outputPath);
            checkpoint.DataLength = new FileInfo(outputPath).Length;
            checkpoint.Save(checkpointPath);

            // And the output is replaced by one that does not hold them.
            File.WriteAllText(outputPath, "");
            if (!TrySeal(vault)) return;

            var before = handler.Uris.Count;
            var warnings = Sync(deltaPath, checkpointPath, outputPath);
            Unseal(vault);

            Assert.Contains(warnings, w => w.Contains("no longer holds the 2 items"));
            Assert.True(File.Exists(checkpointPath),
                "the checkpoint was deletable after all, so no undeletable one was tested");

            var wire = handler.Uris.Skip(before).ToList();
            Assert.True(wire[0].Contains("$deltatoken=D1", StringComparison.Ordinal),
                $"the run resumed the checkpoint it gave up on; first request: {wire[0]}");
            Assert.DoesNotContain("skiptoken=B2", wire[0]);

            Assert.Equal(ThisSyncsRows, File.ReadAllLines(outputPath));
            Assert.Contains("$deltatoken=D2", DeltaState.Load(deltaPath)!.DeltaLink);
        }
        finally
        {
            try { Unseal(vault); } catch { }
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The control, in the same undeletable directory: a checkpoint whose temp is where it says
    /// it is is still resumed from, and the resume is the same one it has always been - one
    /// request, for the page the checkpoint stopped at. What decides is the reconcile's answer
    /// and not what is left on disk, so a run that can delete nothing at all resumes exactly as
    /// it does when it can.
    /// </summary>
    [Fact]
    public void A_healthy_checkpoint_still_resumes_when_it_cannot_be_deleted()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = NewDir();
        var vault = Directory.CreateDirectory(Path.Combine(dir, "vault")).FullName;
        var deltaPath = Path.Combine(dir, "state.json");
        var checkpointPath = Path.Combine(vault, "run.checkpoint");
        var outputPath = Path.Combine(dir, "out.jsonl");

        var handler = new ByUrlHandler();
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            BaselineThenDeath(handler, deltaPath, checkpointPath, outputPath);

            // Nothing is missing this time: the temp the checkpoint names holds the two items
            // it counts.
            Assert.Single(Directory.GetFiles(dir, "out.jsonl.*.tmp"));
            if (!TrySeal(vault)) return;

            var before = handler.Uris.Count;
            var warnings = Sync(deltaPath, checkpointPath, outputPath);
            Unseal(vault);

            Assert.Contains(warnings, w => w.Contains("Recovered 2 items"));
            var wire = handler.Uris.Skip(before).ToList();
            Assert.Single(wire);
            Assert.Contains("skiptoken=B2", wire[0]);

            Assert.Equal(ThisSyncsRows, File.ReadAllLines(outputPath));
            Assert.Contains("$deltatoken=D2", DeltaState.Load(deltaPath)!.DeltaLink);
        }
        finally
        {
            try { Unseal(vault); } catch { }
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The steady state a checkpoint nothing can remove leaves: it is still there next run, and
    /// next run gives up on it again. Noisy - the warning repeats every time, which is what the
    /// delete-failure warning already tells the caller to end by hand - and correct. What must
    /// not happen is a later run reading the survivor as a position to resume from.
    /// </summary>
    [Fact]
    public void A_second_run_against_the_surviving_checkpoint_gives_up_on_it_again()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = NewDir();
        var vault = Directory.CreateDirectory(Path.Combine(dir, "vault")).FullName;
        var deltaPath = Path.Combine(dir, "state.json");
        var checkpointPath = Path.Combine(vault, "run.checkpoint");
        var outputPath = Path.Combine(dir, "out.jsonl");

        var handler = new ByUrlHandler();
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            BaselineThenDeath(handler, deltaPath, checkpointPath, outputPath);
            foreach (var t in Directory.GetFiles(dir, "out.jsonl.*.tmp")) File.Delete(t);
            if (!TrySeal(vault)) return;

            // Run 3 gives up on the checkpoint, cannot delete it, cannot save one of its own
            // over it either, and dies where the run before it died - so run 4 meets the file
            // run 3 met, unchanged, with the delta token still where it was.
            handler.DieOnResume = true;
            var beforeThird = handler.Uris.Count;
            var third = Sync(deltaPath, checkpointPath, outputPath);
            handler.DieOnResume = false;

            Assert.Contains(third, w => w.Contains("temp file is missing or incomplete"));
            Assert.True(handler.Uris[beforeThird].Contains("$deltatoken=D1", StringComparison.Ordinal),
                $"the run resumed the checkpoint it gave up on; first request: {handler.Uris[beforeThird]}");
            Assert.True(File.Exists(checkpointPath));
            Assert.Contains("skiptoken=B2", PaginationCheckpoint.Load(checkpointPath)!.NextLink);

            var beforeFourth = handler.Uris.Count;
            var fourth = Sync(deltaPath, checkpointPath, outputPath);
            Unseal(vault);

            Assert.Contains(fourth, w => w.Contains("temp file is missing or incomplete"));
            var wire = handler.Uris.Skip(beforeFourth).ToList();
            Assert.True(wire[0].Contains("$deltatoken=D1", StringComparison.Ordinal),
                $"the run resumed the checkpoint it gave up on; first request: {wire[0]}");
            Assert.DoesNotContain("skiptoken=B2", wire[0]);

            // Nothing doubled, and nothing from the runs before it in front of it.
            Assert.Equal(ThisSyncsRows, File.ReadAllLines(outputPath));
            Assert.Contains("$deltatoken=D2", DeltaState.Load(deltaPath)!.DeltaLink);
        }
        finally
        {
            try { Unseal(vault); } catch { }
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
