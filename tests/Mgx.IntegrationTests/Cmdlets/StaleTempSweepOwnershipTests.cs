using System.Management.Automation;
using System.Net;
using System.Text;

namespace Mgx.IntegrationTests;

/// <summary>
/// The stale-temp sweep runs on every fresh export, and it searched with "{output}.*.tmp" -
/// a glob whose '*' spans dots. An export to "users" therefore swept the temp of an
/// interrupted export to "users.jsonl", whose checkpoint still named it. That checkpoint then
/// resolves to a missing file and the interrupted run re-enumerates from page one, with
/// nothing to say that another mgx run deleted what it had collected.
/// </summary>
[Collection("Pipeline")]
public class StaleTempSweepOwnershipTests
{
    private const string Page1 = """
    {"value":[{"id":"u1"},{"id":"u2"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/users?$skiptoken=P2"}
    """;
    private const string Page2 = """
    {"value":[{"id":"u3"}]}
    """;
    private const string ServerError = """{"error":{"code":"InternalServerError","message":"boom"}}""";

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

    private static PowerShell Shell()
    {
        var ps = PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection).Assembly);
        ps.Invoke();
        ps.Commands.Clear();
        return ps;
    }

    private static void Export(string output, string? checkpoint, bool all, int top = 0)
    {
        using var ps = Shell();
        var cmd = ps.AddCommand("Export-MgxCollection")
                    .AddParameter("Uri", "/users")
                    .AddParameter("OutputFile", output);
        if (checkpoint != null) cmd.AddParameter("CheckpointPath", checkpoint);
        if (all) cmd.AddParameter("All");
        if (top > 0) cmd.AddParameter("Top", top);
        try { ps.Invoke(); }
        catch (CmdletInvocationException) { }
    }

    [Fact]
    public void A_fresh_export_does_not_sweep_a_longer_named_exports_temp()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-sweep-owner-{Guid.NewGuid():N}")).FullName;
        var theirs = Path.Combine(dir, "users.jsonl");
        var mine = Path.Combine(dir, "users");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            // An export to "users.jsonl" collects page 1 into its temp and dies on page 2.
            handler.Queue(HttpStatusCode.OK, Page1);
            Export(theirs, checkpoint, all: true);
            var temp = Assert.Single(Directory.GetFiles(dir, "users.jsonl.*.tmp"));
            Assert.False(File.Exists(theirs));

            // An unrelated export to "users" in the same directory.
            handler.Queue(HttpStatusCode.OK, Page1);
            Export(mine, checkpoint: null, all: false, top: 1);
            Assert.True(File.Exists(temp), "another export's temp file was deleted by this export's sweep");

            // The interrupted export resumes onto what it had already collected.
            handler.Queue(HttpStatusCode.OK, Page2);
            Export(theirs, checkpoint, all: true);
            Assert.Equal(
                ["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"],
                File.ReadAllLines(theirs));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The sweep deletes files it did not create, on the strength of their names, so the name
    /// has to be one a run could have given its own temp: the output's, a dot, 32 hex digits,
    /// ".tmp". Reaching wider took "users.jsonl.backup.tmp" - somebody else's file, in a
    /// directory mgx was only ever asked to write one output into.
    /// </summary>
    [Fact]
    public void A_fresh_export_does_not_sweep_a_tmp_no_run_could_have_written()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-sweep-shape-{Guid.NewGuid():N}")).FullName;
        var output = Path.Combine(dir, "users.jsonl");
        var backup = Path.Combine(dir, "users.jsonl.backup.tmp");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            var kept = new[] { "{\"id\":\"kept-1\"}", "{\"id\":\"kept-2\"}" };
            File.WriteAllLines(backup, kept);

            handler.Queue(HttpStatusCode.OK, Page1);
            handler.Queue(HttpStatusCode.OK, Page2);
            Export(output, checkpoint: null, all: true);

            Assert.Equal(
                ["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"],
                File.ReadAllLines(output));
            Assert.True(File.Exists(backup), "a file mgx never wrote was deleted by the sweep");
            Assert.Equal(kept, File.ReadAllLines(backup));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
