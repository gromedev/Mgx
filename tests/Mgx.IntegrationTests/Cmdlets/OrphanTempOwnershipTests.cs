using System.Management.Automation;
using System.Net;
using System.Reflection;
using System.Text;
using Mgx.Cmdlets.Base;

namespace Mgx.IntegrationTests;

/// <summary>
/// Which temp files belong to an output. The recovery paths search for them with
/// "{output}.*.tmp", which Directory.EnumerateFiles reads as a glob - and '*' spans dots, so
/// an export to "users" matched a file left by an export to "users.jsonl". Adopting one is how
/// another export's rows end up in this one's output.
/// </summary>
[Collection("Pipeline")]
public class OrphanTempOwnershipTests
{
    private const string Page1 = """
    {"value":[{"id":"u1"},{"id":"u2"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/users?$skiptoken=P2"}
    """;
    private const string Page2 = """
    {"value":[{"id":"u3"}]}
    """;
    private const string ServerError = """{"error":{"code":"InternalServerError","message":"boom"}}""";

    private static readonly MethodInfo Adopt =
        typeof(MgxCmdletBase).GetMethod(
            "TryAdoptOrphanedTemp", BindingFlags.Static | BindingFlags.NonPublic)!;

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

    private static string NewDir() =>
        Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-temp-owner-{Guid.NewGuid():N}")).FullName;

    /// <summary>
    /// Whether this directory's filesystem answers to a name in any case. Where it does not,
    /// two spellings are two files and refusing the other one's temp is the right answer, so
    /// the case test below has nothing to assert.
    /// </summary>
    private static bool NamesAreCaseInsensitive(string dir)
    {
        var probe = Path.Combine(dir, $"Case-{Guid.NewGuid():N}");
        File.WriteAllText(probe, "");
        try { return File.Exists(Path.Combine(dir, Path.GetFileName(probe).ToLowerInvariant())); }
        finally { try { File.Delete(probe); } catch { } }
    }

    private static void Export(string output, string checkpoint)
    {
        using var ps = PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection).Assembly);
        ps.Invoke();
        ps.Commands.Clear();
        ps.AddCommand("Export-MgxCollection")
          .AddParameter("Uri", "/users")
          .AddParameter("OutputFile", output)
          .AddParameter("CheckpointPath", checkpoint)
          .AddParameter("All");
        try { ps.Invoke(); }
        catch (CmdletInvocationException) { }
    }

    /// <summary>
    /// Plain names, no glob characters anywhere: "users" and "users.jsonl" are two outputs in
    /// one directory, and only the second one's export wrote that temp.
    /// </summary>
    [Fact]
    public void A_temp_left_by_a_longer_named_export_is_not_adopted()
    {
        var dir = NewDir();
        try
        {
            var mine = Path.Combine(dir, "users");
            var theirs = Path.Combine(dir, $"users.jsonl.{Guid.NewGuid():N}.tmp");
            File.WriteAllLines(theirs, ["{\"id\":\"THEIRS-1\"}", "{\"id\":\"THEIRS-2\"}"]);

            Assert.False((bool)Adopt.Invoke(null, [mine, 2L])!);

            Assert.False(File.Exists(mine));
            Assert.True(File.Exists(theirs));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// The same thing through the cmdlet, from the state that reaches adoption: a checkpoint
    /// written before the temp name and length were recorded, and no output file.
    /// </summary>
    [Fact]
    public void An_export_does_not_adopt_a_longer_named_exports_temp()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "users");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var theirs = Path.Combine(dir, $"users.jsonl.{Guid.NewGuid():N}.tmp");
            File.WriteAllLines(theirs, ["{\"id\":\"THEIRS-1\"}", "{\"id\":\"THEIRS-2\"}"]);
            File.WriteAllText(checkpoint, """
            {"resource":"https://graph.microsoft.com/v1.0/users?$top=999",
             "nextLink":"https://graph.microsoft.com/v1.0/users?$skiptoken=P2",
             "itemsCollected":2,"pageItemsAlreadyWritten":0}
            """);

            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);
            handler.Queue(HttpStatusCode.OK, Page1);
            handler.Queue(HttpStatusCode.OK, Page2);

            using var ps = PowerShell.Create();
            ps.AddCommand("Import-Module")
              .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection).Assembly);
            ps.Invoke();
            ps.Commands.Clear();
            ps.AddCommand("Export-MgxCollection")
              .AddParameter("Uri", "/users")
              .AddParameter("OutputFile", output)
              .AddParameter("CheckpointPath", checkpoint)
              .AddParameter("All");
            ps.Invoke();

            var lines = File.ReadAllLines(output);
            Assert.DoesNotContain(lines, l => l.Contains("THEIRS"));
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"], lines);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// A file the caller keeps beside their output. The glob that finds temps reaches it, and
    /// nothing about the name says mgx wrote it - a run's own temp is the output's name, a dot,
    /// 32 hex digits and ".tmp". Adoption copies what it takes into the output and then deletes
    /// it, so a backup taken by hand was consumed as an interrupted export's items.
    /// </summary>
    [Fact]
    public void A_file_that_is_not_a_run_temp_is_not_adopted()
    {
        var dir = NewDir();
        try
        {
            var output = Path.Combine(dir, "users.jsonl");
            var backup = Path.Combine(dir, "users.jsonl.backup.tmp");
            File.WriteAllLines(backup, ["{\"id\":\"BACKUP-1\"}", "{\"id\":\"BACKUP-2\"}"]);

            Assert.False((bool)Adopt.Invoke(null, [output, 2L])!);

            Assert.False(File.Exists(output));
            Assert.True(File.Exists(backup));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// The same thing through the cmdlet, from the state that reaches adoption: a checkpoint
    /// written before the temp name and length were recorded, and no output file.
    /// </summary>
    [Fact]
    public void An_export_does_not_adopt_a_file_it_never_wrote()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "users.jsonl");
        var backup = Path.Combine(dir, "users.jsonl.backup.tmp");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            File.WriteAllLines(backup, ["{\"id\":\"BACKUP-1\"}", "{\"id\":\"BACKUP-2\"}"]);
            File.WriteAllText(checkpoint, """
            {"resource":"https://graph.microsoft.com/v1.0/users?$top=999",
             "nextLink":"https://graph.microsoft.com/v1.0/users?$skiptoken=P2",
             "itemsCollected":2,"pageItemsAlreadyWritten":0}
            """);

            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);
            handler.Queue(HttpStatusCode.OK, Page1);
            handler.Queue(HttpStatusCode.OK, Page2);

            Export(output, checkpoint);

            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"],
                File.ReadAllLines(output));
            Assert.Equal(["{\"id\":\"BACKUP-1\"}", "{\"id\":\"BACKUP-2\"}"],
                File.ReadAllLines(backup));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// The same export, resumed with -OutputFile spelled differently. On a filesystem that
    /// answers to either spelling the temp is this export's own, and the glob that finds it
    /// matches case-insensitively - so a predicate comparing ordinally threw away the run's
    /// own work and re-enumerated the collection from the first page without saying so.
    /// </summary>
    [Fact]
    public void An_export_adopts_its_own_temp_when_the_output_is_spelled_differently()
    {
        var dir = NewDir();
        try
        {
            if (!NamesAreCaseInsensitive(dir)) return;

            var upper = Path.Combine(dir, "Users.jsonl");
            var lower = Path.Combine(dir, "users.jsonl");
            var checkpoint = Path.Combine(dir, "run.checkpoint");

            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            // Page 1 reaches the temp; page 2 fails, so the temp and its checkpoint survive.
            handler.Queue(HttpStatusCode.OK, Page1);
            Export(upper, checkpoint);
            Assert.Single(Directory.GetFiles(dir, "Users.jsonl.*.tmp"));

            // The resume, typed the other way. Only the page the dead run never reached.
            handler.Queue(HttpStatusCode.OK, Page2);
            Export(lower, checkpoint);

            Assert.Equal(
                ["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"],
                File.ReadAllLines(lower));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// A second export running against the same output right now. Adoption picks the newest
    /// matching temp with only a line count to go on, and the file a live run is writing into
    /// is always the newest one - so recovering a pre-2.1.0 checkpoint copied that run's rows
    /// into this run's deliverable and unlinked the file underneath it, reported on the warning
    /// stream as a successful recovery with nothing on the error stream. The sweep beside it
    /// spares the same file; adoption has to.
    /// </summary>
    [Fact]
    public void An_export_does_not_adopt_a_temp_a_running_export_still_holds()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "users.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        var live = Path.Combine(dir, $"users.jsonl.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(checkpoint, """
            {"resource":"https://graph.microsoft.com/v1.0/users?$top=999",
             "nextLink":"https://graph.microsoft.com/v1.0/users?$skiptoken=P2",
             "itemsCollected":2,"pageItemsAlreadyWritten":0}
            """);

            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);
            handler.Queue(HttpStatusCode.OK, Page1);
            handler.Queue(HttpStatusCode.OK, Page2);

            // The other run, holding its temp open the way an export does, two rows flushed.
            using (var writer = new StreamWriter(live, append: false))
            {
                writer.WriteLine("{\"id\":\"OTHER-1\"}");
                writer.WriteLine("{\"id\":\"OTHER-2\"}");
                writer.Flush();

                Export(output, checkpoint);

                Assert.True(File.Exists(live), "a running export's temp was unlinked under it");
                var lines = File.ReadAllLines(output);
                Assert.DoesNotContain(lines, l => l.Contains("OTHER"));
                Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"], lines);

                writer.WriteLine("{\"id\":\"OTHER-3\"}");
            }

            Assert.Equal(
                ["{\"id\":\"OTHER-1\"}", "{\"id\":\"OTHER-2\"}", "{\"id\":\"OTHER-3\"}"],
                File.ReadAllLines(live));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// What reaches the caller's file. The checkpoint predates the recorded length, so adoption
    /// has only a line count to go on - and a temp cut inside the row that count reaches hands
    /// the fragment back as a line like any other. It went into the JSONL output as an item,
    /// was reported as a recovery, and the rest of the enumeration was appended behind it,
    /// leaving a row nothing can parse in the middle of the deliverable.
    /// </summary>
    [Fact]
    public void An_export_does_not_adopt_a_temp_cut_inside_the_row_the_checkpoint_counts()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "users.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            File.WriteAllText(Path.Combine(dir, $"users.jsonl.{Guid.NewGuid():N}.tmp"),
                "{\"id\":\"t1\"}\n{\"id\":\"t2\"}\n{\"id\":\"to");
            File.WriteAllText(checkpoint, """
            {"resource":"https://graph.microsoft.com/v1.0/users?$top=999",
             "nextLink":"https://graph.microsoft.com/v1.0/users?$skiptoken=P2",
             "itemsCollected":3,"pageItemsAlreadyWritten":0}
            """);

            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);
            handler.Queue(HttpStatusCode.OK, Page1);
            handler.Queue(HttpStatusCode.OK, Page2);

            Export(output, checkpoint);

            var lines = File.ReadAllLines(output);
            foreach (var line in lines)
                System.Text.Json.JsonDocument.Parse(line).Dispose();
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"], lines);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
