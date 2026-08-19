using System.Management.Automation;
using System.Net;
using System.Reflection;
using Mgx.Cmdlets.Base;
using Mgx.Engine.Http;
using Mgx.Engine.Pagination;

namespace Mgx.IntegrationTests;

/// <summary>
/// End-to-end recovery for a JSONL delta sync, driven through the cmdlet against a mock
/// transport. OrphanAdoptionTests and CheckpointRetentionTests call TryAdoptOrphanedTemp
/// directly; both shipped bugs in this area lived in the CALLER, in the interaction between
/// adoption, the checkpoint-deletion branch and the temp-vs-direct write decision, so the
/// state machine needs coverage that actually runs it.
///
/// The invariant under test: a checkpoint that survives a run must never describe items that
/// exist nowhere. Once the delta token advances there is no re-fetch, so an item skipped here
/// is gone permanently.
/// </summary>
[Collection("Pipeline")]
public class DeltaRecoveryTests
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

    private static void InjectMock(MockHttpHandler handler)
    {
        ResiliencePipelineFactory.Reset();
        var t = typeof(MgxCmdletBase);
        t.GetField("s_graphHttpClient", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, new HttpClient(handler));
        t.GetField("s_cachedAuthFingerprint", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, MgxCmdletBase.BuildAuthFingerprint(
                new { TenantId = "test-tenant-00000000-0000-0000-0000-000000000000" }, null));
        t.GetField("s_ownsHttpClient", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, false);
        t.GetField("s_graphEndpoint", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public)!
            .SetValue(null, "https://graph.microsoft.com");
        t.GetField("s_clientOptions", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public)!
            .SetValue(null, new ResilientGraphClientOptions { NoRateLimit = true, MaxRetryAttempts = 1 });
        ResiliencePipelineFactory.Reset();
    }

    private static void CleanupMock()
    {
        var t = typeof(MgxCmdletBase);
        t.GetField("s_graphHttpClient", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, null);
        t.GetField("s_cachedAuthFingerprint", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, null);
        t.GetField("s_cachedAuthContextRef", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, null);
        ResiliencePipelineFactory.Reset();
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

    private static void Sync(string deltaPath, string checkpointPath, string outputPath)
    {
        using var ps = Shell();
        ps.AddCommand("Sync-MgxDelta")
          .AddParameter("Uri", "/users/delta")
          .AddParameter("DeltaPath", deltaPath)
          .AddParameter("CheckpointPath", checkpointPath)
          .AddParameter("OutputFile", outputPath);
        // A run that dies is the subject here, so a terminating error is an expected outcome.
        try { ps.Invoke(); }
        catch (System.Management.Automation.CmdletInvocationException) { }
    }

    /// <summary>
    /// The steady-state failure: a completed run, then a run that dies on a transient 500
    /// partway through. The dead run's temp holds the pages the checkpoint counts. If that
    /// temp is discarded, the next run sees checkpoint + output + no temp - which is the
    /// ROUTINE "nothing to adopt" state - resumes in append mode, and the token advances
    /// past pages that were never written anywhere.
    /// </summary>
    [Fact]
    public void A_transient_error_midrun_does_not_strand_the_pages_the_checkpoint_counts()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-recovery-{Guid.NewGuid():N}")).FullName;
        var deltaPath = Path.Combine(dir, "state.json");
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var outputPath = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, Baseline);                        // run 1
        handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);                    // run 2, page 1
        handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);    // run 2, page 2
        handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);    // ... and its retry
        handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);                    // run 3 resumes

        InjectMock(handler);
        try
        {
            Sync(deltaPath, checkpointPath, outputPath);
            Assert.Equal(["{\"id\":\"a1\"}", "{\"id\":\"a2\"}"], File.ReadAllLines(outputPath));
            Assert.False(File.Exists(checkpointPath), "a completed run deletes its checkpoint");

            Sync(deltaPath, checkpointPath, outputPath);
            var checkpoint = PaginationCheckpoint.Load(checkpointPath);
            Assert.NotNull(checkpoint);
            Assert.Equal(2, checkpoint.ItemsCollected);
            Assert.Contains("skiptoken=B2", checkpoint.NextLink);
            // b1 and b2 are counted by the checkpoint but are not in the output, so they must
            // still be somewhere: the temp is the only place they can be.
            Assert.Single(Directory.GetFiles(dir, "out.jsonl.*.tmp"));
            Assert.Equal(
                ["{\"id\":\"b1\"}", "{\"id\":\"b2\"}"],
                File.ReadAllLines(Directory.GetFiles(dir, "out.jsonl.*.tmp")[0]));

            Sync(deltaPath, checkpointPath, outputPath);
            Assert.Equal(
                ["{\"id\":\"a1\"}", "{\"id\":\"a2\"}", "{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}"],
                File.ReadAllLines(outputPath));
            Assert.Contains("$deltatoken=D2", DeltaState.Load(deltaPath)!.DeltaLink);
            Assert.False(File.Exists(checkpointPath));
            Assert.Empty(Directory.GetFiles(dir, "out.jsonl.*.tmp"));
        }
        finally { CleanupMock(); try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// The same failure with no output from an earlier run. Here the checkpoint IS stale in the
    /// sense its warning means - the output is genuinely missing - but the temp still holds the
    /// items, so adoption creates the output from it rather than the checkpoint being dropped.
    /// </summary>
    [Fact]
    public void A_first_run_that_dies_midway_still_recovers_its_pages()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-recovery-{Guid.NewGuid():N}")).FullName;
        var deltaPath = Path.Combine(dir, "state.json");
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var outputPath = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
        handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
        handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
        handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);

        InjectMock(handler);
        try
        {
            Sync(deltaPath, checkpointPath, outputPath);
            Assert.False(File.Exists(outputPath), "a fresh run promotes its temp only on success");
            Assert.True(File.Exists(checkpointPath));

            Sync(deltaPath, checkpointPath, outputPath);
            Assert.Equal(
                ["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}"],
                File.ReadAllLines(outputPath));
            Assert.Contains("$deltatoken=D2", DeltaState.Load(deltaPath)!.DeltaLink);
            Assert.Empty(Directory.GetFiles(dir, "out.jsonl.*.tmp"));
        }
        finally { CleanupMock(); try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// -Latest means "baseline from now". After an interrupted enumeration that is the same
    /// catastrophe the code already guards against after a state discard: the collected items
    /// are abandoned and the token jumps past every change that preceded it, with no way back.
    /// Without delta state on disk none of the existing guards fire, so the checkpoint is the
    /// only signal that this is not a fresh run.
    /// </summary>
    [Fact]
    public void Latest_is_ignored_while_a_resume_checkpoint_exists()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-recovery-{Guid.NewGuid():N}")).FullName;
        var deltaPath = Path.Combine(dir, "state.json");
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var outputPath = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);                    // run 1, page 1
        handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);    // run 1, page 2
        handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);    // ... and its retry
        handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);                    // run 2 resumes

        InjectMock(handler);
        try
        {
            Sync(deltaPath, checkpointPath, outputPath);
            Assert.True(File.Exists(checkpointPath));
            Assert.False(File.Exists(deltaPath), "run 1 never completed, so no token was saved");

            using (var ps = Shell())
            {
                ps.AddCommand("Sync-MgxDelta").AddParameter("Uri", "/users/delta")
                  .AddParameter("DeltaPath", deltaPath).AddParameter("CheckpointPath", checkpointPath)
                  .AddParameter("OutputFile", outputPath).AddParameter("Latest", true);
                ps.Invoke();
                Assert.Contains(ps.Streams.Warning, w => w.Message.Contains("-Latest ignored"));
            }

            // The interrupted enumeration finished instead of being baselined away.
            Assert.Equal(
                ["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}"],
                File.ReadAllLines(outputPath));
            var token = DeltaState.Load(deltaPath)!.DeltaLink;
            Assert.Contains("$deltatoken=D2", token);
            Assert.DoesNotContain("token=latest", token);
        }
        finally { CleanupMock(); try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// A run that dies before any page boundary leaves no checkpoint, so there is nothing to
    /// resume and nothing the temp is needed for. It must not be left behind: a stale temp is
    /// adoptable by a later run whose checkpoint happens to match the output name.
    /// </summary>
    [Fact]
    public void A_run_that_dies_with_no_checkpoint_leaves_no_temp()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-recovery-{Guid.NewGuid():N}")).FullName;
        var deltaPath = Path.Combine(dir, "state.json");
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var outputPath = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
        handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);

        InjectMock(handler);
        try
        {
            Sync(deltaPath, checkpointPath, outputPath);
            Assert.False(File.Exists(checkpointPath), "no page completed, so no position to save");
            Assert.Empty(Directory.GetFiles(dir, "out.jsonl.*.tmp"));
            Assert.False(File.Exists(outputPath));
        }
        finally { CleanupMock(); try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// A temp file carries no identity: TryAdoptOrphanedTemp globs "{output}.*.tmp", takes the
    /// newest, and checks only that it has enough lines. The Resource check added in 48ffe87
    /// validates the CHECKPOINT, so a CURRENT checkpoint plus a temp orphaned by an unrelated
    /// run still merges that run's rows into a healthy output - and one success makes them
    /// permanent. -FullSync and the -Property/-Filter/-Prefer discards all leave such orphans.
    /// A fresh run has nothing to resume, so every leftover temp is an orphan and must go.
    /// </summary>
    [Fact]
    public void A_fresh_run_clears_temps_orphaned_by_an_earlier_enumeration()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-recovery-{Guid.NewGuid():N}")).FullName;
        var deltaPath = Path.Combine(dir, "state.json");
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var outputPath = Path.Combine(dir, "out.jsonl");

        // An orphan from an enumeration that no longer exists, long enough to satisfy any
        // later checkpoint's line count.
        var foreign = $"{outputPath}.deadbeef.tmp";
        File.WriteAllLines(foreign, Enumerable.Range(0, 50).Select(i => $"{{\"id\":\"foreign{i}\"}}"));

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, Baseline);

        InjectMock(handler);
        try
        {
            Sync(deltaPath, checkpointPath, outputPath);

            Assert.False(File.Exists(foreign), "the orphan must not survive a fresh run");
            Assert.Empty(Directory.GetFiles(dir, "out.jsonl.*.tmp"));
            var lines = File.ReadAllLines(outputPath);
            Assert.Equal(["{\"id\":\"a1\"}", "{\"id\":\"a2\"}"], lines);
            Assert.DoesNotContain(lines, l => l.Contains("foreign"));
        }
        finally { CleanupMock(); try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// Resuming from a mid-page checkpoint must land exactly on the seam. PageItemsAlreadyWritten
    /// counts every item of the in-flight page that is already in the output, including the ones
    /// an earlier resume skipped rather than wrote, so the count and the file agree.
    /// </summary>
    [Fact]
    public void Resuming_from_a_midpage_checkpoint_repeats_no_items()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-recovery-{Guid.NewGuid():N}")).FullName;
        var deltaPath = Path.Combine(dir, "state.json");
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var outputPath = Path.Combine(dir, "out.jsonl");
        var page2Url = "https://graph.microsoft.com/v1.0/users/delta?$skiptoken=P2";

        // Stopped at totalProcessed = 1000: all 999 of page 1, plus item 0 of page 2.
        var written = Enumerable.Range(0, 999).Select(i => $"{{\"id\":\"p{i}\"}}")
            .Concat(["{\"id\":\"q0\"}"]).ToArray();
        File.WriteAllLines(outputPath, written);
        new PaginationCheckpoint
        {
            Resource = "https://graph.microsoft.com/v1.0/users/delta?$top=999",
            NextLink = page2Url,
            ItemsCollected = 1000,
            PageItemsAlreadyWritten = 1,
            TempFile = null,
            DataLength = new FileInfo(outputPath).Length,
        }.Save(checkpointPath);

        var items = Enumerable.Range(0, 999).Select(i => $"{{\"id\":\"q{i}\"}}");
        var page2 = "{\"value\":[" + string.Join(",", items)
            + "],\"@odata.deltaLink\":\"https://graph.microsoft.com/v1.0/users/delta?$deltatoken=D9\"}";

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, page2);

        InjectMock(handler);
        try
        {
            Sync(deltaPath, checkpointPath, outputPath);

            var lines = File.ReadAllLines(outputPath);
            Assert.Equal(999 + 999, lines.Length);
            Assert.Equal(lines.Length, lines.Distinct().Count());
            Assert.Equal("{\"id\":\"q0\"}", lines[999]);
            Assert.Equal("{\"id\":\"q1\"}", lines[1000]);
        }
        finally { CleanupMock(); try { Directory.Delete(dir, true); } catch { } }
    }
}
