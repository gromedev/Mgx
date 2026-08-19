using System.Management.Automation;
using System.Net;
using System.Reflection;
using Mgx.Cmdlets.Base;
using Mgx.Engine.Http;
using Mgx.Engine.Pagination;

namespace Mgx.IntegrationTests;

/// <summary>
/// A checkpoint is a promise that ItemsCollected items are durably somewhere. Every test here
/// breaks that promise a different way and asserts the sync notices. Once the delta token
/// advances there is no re-fetch, so an item skipped here is gone permanently.
/// </summary>
[Collection("Pipeline")]
public class DeltaCheckpointIntegrityTests
{
    private const string DeltaLink1 = "https://graph.microsoft.com/v1.0/users/delta?$deltatoken=D1";
    private const string Page2Url = "https://graph.microsoft.com/v1.0/users/delta?$skiptoken=B2";

    private const string ChangesPage1 = """
    {"value":[{"id":"b1"},{"id":"b2"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/users/delta?$skiptoken=B2"}
    """;
    private const string ChangesPage2 = """
    {"value":[{"id":"b3"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/users/delta?$skiptoken=B3"}
    """;
    private const string ChangesPage3 = """
    {"value":[{"id":"b4"}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/users/delta?$deltatoken=D2"}
    """;
    private const string ChangesPage2Final = """
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

    private sealed record Env(string Dir, string DeltaPath, string CheckpointPath, string OutputPath);

    private static Env NewEnv(string checkpointName = "run.checkpoint")
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-integrity-{Guid.NewGuid():N}")).FullName;
        var env = new Env(dir,
            Path.Combine(dir, "state.json"),
            Path.Combine(dir, checkpointName),
            Path.Combine(dir, "out.jsonl"));
        // Delta state pins requestUrl to DeltaLink1 for every run below.
        new DeltaState
        {
            DeltaLink = DeltaLink1,
            Resource = "/users/delta",
            GraphEndpoint = "https://graph.microsoft.com",
            Select = "",
            ApiVersion = "v1.0",
        }.Save(env.DeltaPath);
        return env;
    }

    private static void Sync(Env env)
    {
        using var ps = Shell();
        ps.AddCommand("Sync-MgxDelta")
          .AddParameter("Uri", "/users/delta")
          .AddParameter("DeltaPath", env.DeltaPath)
          .AddParameter("CheckpointPath", env.CheckpointPath)
          .AddParameter("OutputFile", env.OutputPath);
        try { ps.Invoke(); }
        catch (CmdletInvocationException) { }
    }

    private static string[] Ids(string outputPath) =>
        File.Exists(outputPath) ? File.ReadAllLines(outputPath) : [];

    // ---------------------------------------------------------------- D5

    /// <summary>
    /// A temp shorter than the checkpoint promises. Adoption declines, and because the output
    /// exists the checkpoint is kept and the run resumes at NextLink - past items that are in
    /// neither file. The token then advances over them.
    /// </summary>
    [Fact]
    public void A_torn_temp_does_not_let_the_token_advance_past_what_it_lost()
    {
        var env = NewEnv();
        try
        {
            var handler = new MockHttpHandler();
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);                  // run 1: b1,b2
            handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
            handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);                  // run 2 re-enumerates
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2Final);
            InjectMock(handler);

            Sync(env);
            var temp = Directory.GetFiles(env.Dir, "out.jsonl.*.tmp").Single();
            Assert.Equal(2, PaginationCheckpoint.Load(env.CheckpointPath)!.ItemsCollected);

            // The writer flushed two items; the machine lost power and only one reached the
            // platter. The checkpoint still promises two.
            File.WriteAllLines(temp, ["{\"id\":\"b1\"}"]);
            var before = handler.Requests.Count;

            Sync(env);

            // Resuming at NextLink would step over b2, which is now in no file at all.
            Assert.Contains("$deltatoken=D1", handler.Requests[before].RequestUri!.ToString());
            Assert.Equal(
                ["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}"],
                Ids(env.OutputPath));
        }
        finally { CleanupMock(); try { Directory.Delete(env.Dir, true); } catch { } }
    }

    // ---------------------------------------------------------------- D6

    /// <summary>
    /// PaginationCheckpoint.Save writes "{checkpointPath}.tmp" and renames. With a checkpoint
    /// named after the output - which the collision check permits, it only forbids equal paths -
    /// that staging file matches the output's own "{output}.*.tmp" glob, so a crash mid-save
    /// leaves a file that adoption will happily copy into the data.
    /// </summary>
    [Fact]
    public void The_checkpoints_own_staging_file_is_never_adopted_as_data()
    {
        // -CheckpointPath named after -OutputFile is permitted: the collision check only forbids
        // the two being the same file. PaginationCheckpoint.Save then stages through
        // "out.jsonl.ckpt.tmp", which matches the output's own "out.jsonl.*.tmp" glob.
        var env = NewEnv("out.jsonl.ckpt");
        try
        {
            var handler = new MockHttpHandler();
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
            handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2Final);
            InjectMock(handler);

            Sync(env);
            var temp = Directory.GetFiles(env.Dir, "out.jsonl.*.tmp")
                .Single(f => !f.EndsWith(".ckpt.tmp", StringComparison.Ordinal));

            // A save that died between writing the staging file and renaming it. It is newer
            // than the real temp, so newest-wins selection prefers it.
            var staged = $"{env.CheckpointPath}.tmp";
            File.WriteAllText(staged, File.ReadAllText(env.CheckpointPath));
            File.SetLastWriteTimeUtc(staged, File.GetLastWriteTimeUtc(temp).AddMinutes(5));

            Sync(env);

            Assert.Equal(
                ["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}"],
                Ids(env.OutputPath));
        }
        finally { CleanupMock(); try { Directory.Delete(env.Dir, true); } catch { } }
    }

    // ---------------------------------------------------------------- D7

    /// <summary>
    /// File.Exists is presence, not content. An output truncated to nothing between runs still
    /// reads as a valid baseline, so the run appends the tail to an empty file and the token
    /// advances over everything the checkpoint said was already there.
    /// </summary>
    [Fact]
    public void A_truncated_output_is_not_mistaken_for_the_baseline_it_describes()
    {
        var env = NewEnv();
        try
        {
            var handler = new MockHttpHandler();
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);                  // run 1: b1,b2
            handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
            handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);                  // run 2: adopts, adds b3
            handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
            handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);                  // run 3 re-enumerates
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage3);
            InjectMock(handler);

            Sync(env);
            Sync(env);
            // Run 2 resumed, so it wrote straight to the output and left no temp behind.
            Assert.Empty(Directory.GetFiles(env.Dir, "out.jsonl.*.tmp"));
            Assert.Equal(3, PaginationCheckpoint.Load(env.CheckpointPath)!.ItemsCollected);

            File.WriteAllText(env.OutputPath, "");   // emptied underneath the checkpoint
            var before = handler.Requests.Count;

            Sync(env);

            // Appending the tail to an empty file would advance the token over b1..b3.
            Assert.Contains("$deltatoken=D1", handler.Requests[before].RequestUri!.ToString());
            Assert.Equal(
                ["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}", "{\"id\":\"b4\"}"],
                Ids(env.OutputPath));
        }
        finally { CleanupMock(); try { Directory.Delete(env.Dir, true); } catch { } }
    }

    // ---------------------------------------------------------------- D8

    /// <summary>
    /// An interrupted run leaves the output ahead of its checkpoint: everything it wrote after
    /// the last save is on disk but uncounted. Those items are re-fetched on resume, so unless
    /// the output is cut back to the length the checkpoint recorded they are written twice.
    /// </summary>
    [Fact]
    public void Items_written_after_the_last_checkpoint_are_not_duplicated_on_resume()
    {
        var env = NewEnv();
        try
        {
            var handler = new MockHttpHandler();
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);                  // run 1: b1,b2
            handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
            handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);                  // run 2: adopts, adds b3
            handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
            handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage3);                  // run 3: b4
            InjectMock(handler);

            Sync(env);
            Sync(env);
            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}"], Ids(env.OutputPath));

            // Run 2 got partway into page 3 before it died: b4 reached the file, the checkpoint
            // still ends at b3, and page 3 will be fetched again.
            File.AppendAllLines(env.OutputPath, ["{\"id\":\"b4\"}"]);

            Sync(env);

            var ids = Ids(env.OutputPath);
            Assert.Equal(
                ["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}", "{\"id\":\"b4\"}"],
                ids);
            Assert.Equal(ids.Length, ids.Distinct().Count());
        }
        finally { CleanupMock(); try { Directory.Delete(env.Dir, true); } catch { } }
    }

    // ---------------------------------------------------------------- D9

    /// <summary>
    /// Adoption ends in a move followed by a delete. A temp that outlives its own adoption -
    /// the delete failed, or the process died in between - must not be merged a second time.
    /// The run below fails before completing a page, so the only checkpoint on disk afterwards
    /// is the one adoption itself left; if that still names the temp, the next run adopts it again.
    /// </summary>
    [Fact]
    public void A_temp_that_survives_its_own_adoption_is_not_merged_twice()
    {
        var env = NewEnv();
        try
        {
            var handler = new MockHttpHandler();
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);                  // run 1: b1,b2
            handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
            handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
            handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);  // run 2: adopts, then dies
            handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2Final);             // run 3: b3
            InjectMock(handler);

            Sync(env);
            var temp = Directory.GetFiles(env.Dir, "out.jsonl.*.tmp").Single();
            var tempBytes = File.ReadAllBytes(temp);

            Sync(env);
            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}"], Ids(env.OutputPath));
            Assert.True(File.Exists(env.CheckpointPath), "the run failed, so its position survives");

            // The delete never happened.
            File.WriteAllBytes(temp, tempBytes);

            Sync(env);

            Assert.Equal(
                ["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}"],
                Ids(env.OutputPath));
        }
        finally { CleanupMock(); try { Directory.Delete(env.Dir, true); } catch { } }
    }

    // ---------------------------------------------------------------- D10

    /// <summary>
    /// Temp selection is newest-by-write-time with nothing tying a temp to the run that wrote it.
    /// A newer file from an unrelated run wins over the one the checkpoint is actually about.
    /// </summary>
    [Fact]
    public void Adoption_takes_the_temp_its_checkpoint_describes_not_the_newest_one()
    {
        var env = NewEnv();
        try
        {
            var handler = new MockHttpHandler();
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
            handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2Final);
            InjectMock(handler);

            Sync(env);                                   // leaves its own temp with b1,b2
            var own = Directory.GetFiles(env.Dir, "out.jsonl.*.tmp").Single();

            // Something else drops a newer file matching the same glob.
            var decoy = $"{env.OutputPath}.zzzzzzzz.tmp";
            File.WriteAllLines(decoy, ["{\"id\":\"decoy1\"}", "{\"id\":\"decoy2\"}", "{\"id\":\"decoy3\"}"]);
            File.SetLastWriteTimeUtc(decoy, File.GetLastWriteTimeUtc(own).AddMinutes(5));

            Sync(env);

            var ids = Ids(env.OutputPath);
            Assert.DoesNotContain(ids, l => l.Contains("decoy"));
            Assert.Contains("{\"id\":\"b1\"}", ids);
            Assert.Contains("{\"id\":\"b2\"}", ids);
        }
        finally { CleanupMock(); try { Directory.Delete(env.Dir, true); } catch { } }
    }

    // ---------------------------------------------------------------- D11

    /// <summary>
    /// The item loop calls TryGetProperty("@removed") on every element. That throws
    /// InvalidOperationException on anything that is not a JSON object, which escapes as a
    /// terminating error naming neither the endpoint nor the item.
    /// </summary>
    [Fact]
    public void A_non_object_item_in_a_page_does_not_terminate_the_run()
    {
        var env = NewEnv();
        try
        {
            var handler = new MockHttpHandler();
            handler.QueueResponse(HttpStatusCode.OK,
                """{"value":[{"id":"b1"},"not-an-object",{"id":"b2"}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/users/delta?$deltatoken=D2"}""");
            InjectMock(handler);

            Exception? escaped = null;
            using (var ps = Shell())
            {
                ps.AddCommand("Sync-MgxDelta")
                  .AddParameter("Uri", "/users/delta")
                  .AddParameter("DeltaPath", env.DeltaPath)
                  .AddParameter("CheckpointPath", env.CheckpointPath)
                  .AddParameter("OutputFile", env.OutputPath);
                try { ps.Invoke(); }
                catch (Exception ex) { escaped = ex; }
            }

            Assert.Null(escaped);
            var ids = Ids(env.OutputPath);
            Assert.Contains("{\"id\":\"b1\"}", ids);
            Assert.Contains("{\"id\":\"b2\"}", ids);
        }
        finally { CleanupMock(); try { Directory.Delete(env.Dir, true); } catch { } }
    }
}
