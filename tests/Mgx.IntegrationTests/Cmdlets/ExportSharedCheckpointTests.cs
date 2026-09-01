using System.Management.Automation;
using System.Net;
using System.Text;
using Mgx.Engine.Pagination;

namespace Mgx.IntegrationTests;

/// <summary>
/// A checkpoint's recorded length is a byte offset into "the output", and the recovery path
/// applies it to whatever -OutputFile the run names. Nothing in a checkpoint used to say which
/// file those bytes were counted in, so one -CheckpointPath reused by two exports cut a file
/// the checkpoint knew nothing about - mid-line, with the resumed pages appended onto the torn
/// byte, and no warning on any stream.
/// </summary>
[Collection("Pipeline")]
public class ExportSharedCheckpointTests
{
    private const string UsersPage1 = """
    {"value":[{"id":"u1"},{"id":"u2"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/users?$skiptoken=P2"}
    """;
    private const string UsersPage2 = """
    {"value":[{"id":"u3"}]}
    """;
    private const string GroupsPage1 = """
    {"value":[{"id":"g1"},{"id":"g2"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/groups?$skiptoken=P2"}
    """;
    private const string GroupsPage2 = """
    {"value":[{"id":"g3"}]}
    """;
    private const string ServerError = """{"error":{"code":"InternalServerError","message":"boom"}}""";

    private static readonly string[] OldExport =
        ["{\"id\":\"old-0000001\"}", "{\"id\":\"old-0000002\"}", "{\"id\":\"old-0000003\"}"];

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _steps = new();
        private readonly object _lock = new();

        public void Queue(HttpStatusCode status, string body)
        {
            lock (_lock) _steps.Enqueue((status, body));
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            (HttpStatusCode Status, string Body) step;
            lock (_lock)
                step = _steps.Count > 0 ? _steps.Dequeue() : (HttpStatusCode.InternalServerError, ServerError);
            return Task.FromResult(new HttpResponseMessage(step.Status)
            {
                RequestMessage = request,
                Content = new StringContent(step.Body, Encoding.UTF8, "application/json")
            });
        }
    }

    private static long? Export(string uri, string outputPath, string checkpointPath)
    {
        using var ps = PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection).Assembly);
        ps.Invoke();
        ps.Commands.Clear();

        ps.AddCommand("Export-MgxCollection")
          .AddParameter("Uri", uri)
          .AddParameter("OutputFile", outputPath)
          .AddParameter("CheckpointPath", checkpointPath)
          .AddParameter("All");
        try
        {
            foreach (var r in ps.Invoke())
                if (r?.BaseObject is Mgx.Cmdlets.Models.MgxExportResult summary)
                    return summary.ItemCount;
        }
        catch (CmdletInvocationException) { }
        return null;
    }

    private static void ExportNoCheckpoint(string uri, string outputPath)
    {
        using var ps = PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection).Assembly);
        ps.Invoke();
        ps.Commands.Clear();
        ps.AddCommand("Export-MgxCollection")
          .AddParameter("Uri", uri)
          .AddParameter("OutputFile", outputPath)
          .AddParameter("All");
        ps.Invoke();
    }

    private static string[] ExportWarnings(string uri, string outputPath, string checkpointPath)
    {
        using var ps = PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection).Assembly);
        ps.Invoke();
        ps.Commands.Clear();

        ps.AddCommand("Export-MgxCollection")
          .AddParameter("Uri", uri)
          .AddParameter("OutputFile", outputPath)
          .AddParameter("CheckpointPath", checkpointPath)
          .AddParameter("All");
        try { ps.Invoke(); }
        catch (CmdletInvocationException) { }
        return [.. ps.Streams.Warning.Select(w => w.Message)];
    }

    private static string NewDir() =>
        Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-export-shared-{Guid.NewGuid():N}")).FullName;

    /// <summary>
    /// What each refusal says. A checkpoint recording another export's output is a second
    /// export over one -CheckpointPath, and naming that is what the caller acts on. One
    /// recording no output at all is not: it was believed while the files beside it
    /// corroborated it, and a single export whose temp has since gone, or whose output has been
    /// replaced, reaches the same refusal. Reporting a different export there names a cause the
    /// caller can go and check and find nothing behind.
    /// </summary>
    [Fact]
    public void A_refusal_reports_the_cause_it_can_show()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            File.WriteAllLines(output, OldExport);

            // Written before outputs were recorded, counting more bytes than this output holds.
            new PaginationCheckpoint
            {
                Resource = "https://graph.microsoft.com/v1.0/users?$top=999",
                NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=P2",
                ItemsCollected = 2,
                PageItemsAlreadyWritten = 0,
                TempFile = null,
                OutputFile = null,
                DataLength = new FileInfo(output).Length + 4096,
            }.Save(checkpoint);

            var uncorroborated = ExportWarnings("/users", output, checkpoint);
            Assert.Contains(uncorroborated, w => w.Contains("no longer corroborate"));
            Assert.DoesNotContain(uncorroborated, w => w.Contains("belongs to a different export"));

            // The same position, recording an output this run is not writing to.
            new PaginationCheckpoint
            {
                Resource = "https://graph.microsoft.com/v1.0/users?$top=999",
                NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=P2",
                ItemsCollected = 2,
                PageItemsAlreadyWritten = 0,
                TempFile = null,
                OutputFile = Path.Combine(dir, "elsewhere.jsonl"),
                DataLength = 24,
            }.Save(checkpoint);

            Assert.Contains(ExportWarnings("/users", output, checkpoint),
                w => w.Contains("belongs to a different export"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// Two exports of the same resource to different output files, sharing one checkpoint path.
    /// The second run's output must not be cut back to the first run's byte count.
    /// </summary>
    [Fact]
    public void A_checkpoint_from_another_export_does_not_cut_this_ones_output()
    {
        var dir = NewDir();
        var outA = Path.Combine(dir, "outA.jsonl");
        var outB = Path.Combine(dir, "outB.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            // Export A dies on page 2, twice: the second run promotes the temp, so the
            // checkpoint it leaves records a length with no temp to explain it.
            handler.Queue(HttpStatusCode.OK, UsersPage1);
            Export("/users", outA, checkpoint);
            Export("/users", outA, checkpoint);

            var cp = PaginationCheckpoint.Load(checkpoint)!;
            Assert.Null(cp.TempFile);
            Assert.Equal(new FileInfo(outA).Length, cp.DataLength);

            // Export B: same resource, its own output, which already holds an earlier export.
            File.WriteAllLines(outB, OldExport);
            handler.Queue(HttpStatusCode.OK, UsersPage1);
            handler.Queue(HttpStatusCode.OK, UsersPage2);
            var reported = Export("/users", outB, checkpoint);

            var lines = File.ReadAllLines(outB);
            Assert.All(lines, l => Assert.StartsWith("{\"id\":\"", l));
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"], lines);
            Assert.Equal(3, reported);

            // A's own output is untouched by B.
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}"], File.ReadAllLines(outA));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// A checkpoint left by an export of a DIFFERENT resource, against the same output name.
    /// The resource is compared before anything is cut, so a run that then fails leaves the
    /// output it never replaced exactly as it found it.
    /// </summary>
    [Fact]
    public void A_checkpoint_for_another_resource_does_not_destroy_an_output_the_run_never_replaces()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            // An interrupted export of /groups, resumed once so its checkpoint names no temp.
            handler.Queue(HttpStatusCode.OK, GroupsPage1);
            Export("/groups", output, checkpoint);
            Export("/groups", output, checkpoint);
            Assert.Equal(2, File.ReadAllLines(output).Length);

            // A later export replaces the output without touching that checkpoint.
            File.WriteAllLines(output, OldExport);

            // An export of /users to the same file, which fails on its first page.
            var reported = Export("/users", output, checkpoint);

            Assert.Null(reported);
            Assert.Equal(OldExport, File.ReadAllLines(output));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// A checkpoint that is not this export's is refused, and the refusal used to depend on
    /// deleting it - an unchecked delete, so a checkpoint in a directory this account cannot
    /// write survived, the loop below decided to append from it merely existing, and the other
    /// export's remaining pages were appended onto a file that had never held its first ones.
    /// </summary>
    [Fact]
    public void A_refused_checkpoint_that_cannot_be_deleted_appends_nothing_foreign()
    {
        // The checkpoint sits in a directory the run cannot write, which is what makes its
        // deletion fail. Windows expresses that through ACLs, not a mode.
        if (OperatingSystem.IsWindows()) return;

        var dir = NewDir();
        var vault = Directory.CreateDirectory(Path.Combine(dir, "vault")).FullName;
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(vault, "run.checkpoint");
        try
        {
            File.WriteAllLines(output, OldExport);
            new PaginationCheckpoint
            {
                Resource = "https://graph.microsoft.com/v1.0/users?$top=999",
                NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=P2",
                ItemsCollected = 2,
                PageItemsAlreadyWritten = 0,
                TempFile = null,
                OutputFile = Path.Combine(dir, "elsewhere.jsonl"),
                DataLength = new FileInfo(output).Length
            }.Save(checkpoint);
            File.SetUnixFileMode(vault,
                UnixFileMode.UserRead | UnixFileMode.UserExecute);

            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);
            handler.Queue(HttpStatusCode.OK, UsersPage1);
            handler.Queue(HttpStatusCode.OK, UsersPage2);
            var reported = Export("/users", output, checkpoint);

            var lines = File.ReadAllLines(output);
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"], lines);
            Assert.Equal(3, reported);
            Assert.DoesNotContain(lines, l => l.Contains("old-"));
        }
        finally
        {
            try
            {
                File.SetUnixFileMode(vault,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch { }
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// What the refusal leaves behind. The checkpoint and the staging file beside it are the
    /// other export's resume position, and deleting them cost that export its progress while
    /// this run went on to recreate the same collision at its first page boundary.
    /// </summary>
    [Fact]
    public void A_refused_checkpoint_and_its_staging_file_are_left_where_they_are()
    {
        var dir = NewDir();
        var outA = Path.Combine(dir, "outA.jsonl");
        var outB = Path.Combine(dir, "outB.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            // Export A dies on page 2 and leaves its position behind.
            handler.Queue(HttpStatusCode.OK, UsersPage1);
            Export("/users", outA, checkpoint);
            var before = File.ReadAllBytes(checkpoint);
            // A save that died between writing the staging file and renaming it.
            var staged = checkpoint + ".tmp";
            File.WriteAllBytes(staged, before);

            // Export B, its own output, sharing A's checkpoint path, failing on its first page.
            Export("/users", outB, checkpoint);

            Assert.True(File.Exists(checkpoint), "the other export's resume position was deleted");
            Assert.Equal(before, File.ReadAllBytes(checkpoint));
            Assert.True(File.Exists(staged), "the other export's staging file was deleted");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// Two exports of the same resource to files that share a name in different directories,
    /// over one checkpoint path. The name is all a leaf comparison sees, so the second export
    /// adopted the first one's position and cut its file to the first one's byte count.
    /// </summary>
    [Fact]
    public void A_checkpoint_for_the_same_name_in_another_directory_does_not_cut_this_ones_output()
    {
        var dir = NewDir();
        var outA = Path.Combine(Directory.CreateDirectory(Path.Combine(dir, "a")).FullName, "users.jsonl");
        var outB = Path.Combine(Directory.CreateDirectory(Path.Combine(dir, "b")).FullName, "users.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            // Export A dies on page 2, twice: the second run promotes the temp, so the
            // checkpoint it leaves records a length with no temp to explain it.
            handler.Queue(HttpStatusCode.OK, UsersPage1);
            Export("/users", outA, checkpoint);
            Export("/users", outA, checkpoint);

            var cp = PaginationCheckpoint.Load(checkpoint)!;
            Assert.Null(cp.TempFile);
            Assert.Equal(new FileInfo(outA).Length, cp.DataLength);

            // Export B: same resource, same file name, its own directory, and its own earlier
            // export already sitting there.
            File.WriteAllLines(outB, OldExport);
            handler.Queue(HttpStatusCode.OK, UsersPage1);
            handler.Queue(HttpStatusCode.OK, UsersPage2);
            var reported = Export("/users", outB, checkpoint);

            var lines = File.ReadAllLines(outB);
            Assert.All(lines, l => Assert.StartsWith("{\"id\":\"", l));
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"], lines);
            Assert.Equal(3, reported);
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}"], File.ReadAllLines(outA));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// A checkpoint for the same path under different query options - another -Top, another
    /// -Filter - counts a different enumeration. Comparing paths and not queries let it through
    /// the guard, so the output was cut to its byte count before the exact comparison that
    /// refuses ever ran, and a run that then failed left the cut behind.
    /// </summary>
    [Fact]
    public void A_checkpoint_whose_query_differs_does_not_cut_the_output_before_it_is_refused()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            File.WriteAllLines(output, OldExport);
            var before = File.ReadAllBytes(output);
            new PaginationCheckpoint
            {
                // Same path, half the page: an export of /users this one is not making.
                Resource = "https://graph.microsoft.com/v1.0/users?$top=500",
                NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=P2",
                ItemsCollected = 2,
                PageItemsAlreadyWritten = 0,
                TempFile = null,
                OutputFile = output,
                DataLength = 24,
            }.Save(checkpoint);
            var checkpointBefore = File.ReadAllBytes(checkpoint);

            // This export fails on its first page, so anything the output loses, it loses here.
            var reported = Export("/users", output, checkpoint);

            Assert.Null(reported);
            Assert.Equal(before, File.ReadAllBytes(output));
            Assert.Equal(checkpointBefore, File.ReadAllBytes(checkpoint));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// How far "it is left as it is" has to reach. The refused checkpoint's items are in the
    /// temp it names, and the stale-temp sweep a few lines later deleted every temp beside the
    /// output - including that one. The export it belongs to then came back to a position
    /// pointing at a file that is gone and enumerated the collection again from the first page,
    /// which for a long export is the whole cost the refusal was written to avoid.
    /// </summary>
    [Fact]
    public void A_refused_checkpoints_temp_survives_the_run_that_refused_it()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            // Export A dies on page 2: its two items are in its temp, the output was never
            // promoted, and the checkpoint names both.
            handler.Queue(HttpStatusCode.OK, UsersPage1);
            Export("/users", output, checkpoint);

            var cp = PaginationCheckpoint.Load(checkpoint)!;
            Assert.NotNull(cp.TempFile);
            var temp = Path.Combine(dir, cp.TempFile);
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}"], File.ReadAllLines(temp));
            var before = File.ReadAllBytes(checkpoint);

            // Export B: another collection, the same -OutputFile and -CheckpointPath, failing
            // before its first page boundary - so nothing it does on a boundary or at
            // completion is what answers here.
            Assert.Null(Export("/groups", output, checkpoint));

            Assert.Equal(before, File.ReadAllBytes(checkpoint));
            Assert.True(File.Exists(temp),
                "the refused checkpoint's items were swept away by the run that refused it");

            // A, back where it left off, rather than at page 1.
            handler.Queue(HttpStatusCode.OK, UsersPage2);
            Assert.Equal(3, Export("/users", output, checkpoint));
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"],
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
    /// ".tmp" - so a refused checkpoint naming "users.jsonl.{guid}.tmp" beside an output called
    /// "users" names a file this sweep could not touch either way. Skipping on it bought that
    /// file nothing and cost this output its own orphans, which the pre-length adoption path
    /// then picks up on a line count alone.
    /// </summary>
    [Fact]
    public void A_refused_temp_this_sweep_could_never_reach_does_not_spare_this_outputs_orphans()
    {
        var dir = NewDir();
        var mine = Path.Combine(dir, "users");
        var theirs = Path.Combine(dir, "users.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            // The export to "users" collects page one into its temp and dies on page two.
            handler.Queue(HttpStatusCode.OK, UsersPage1);
            Export("/users", mine, checkpoint);
            var orphan = Assert.Single(Directory.GetFiles(dir, "users.*.tmp"));

            // The export next door takes the shared checkpoint over: it refuses what it finds,
            // saves its own position at its first page boundary, and dies on page two. The
            // first export's temp is now an orphan - nothing on disk describes it.
            handler.Queue(HttpStatusCode.OK, UsersPage1);
            Export("/users", theirs, checkpoint);
            var theirTemp = Assert.Single(Directory.GetFiles(dir, "users.jsonl.*.tmp"));

            // "users" again. It refuses a checkpoint naming a temp beside a different output,
            // and that name must not stand in the way of its own sweep.
            handler.Queue(HttpStatusCode.OK, UsersPage1);
            handler.Queue(HttpStatusCode.OK, UsersPage2);
            Assert.Equal(3, Export("/users", mine, checkpoint));

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
}
