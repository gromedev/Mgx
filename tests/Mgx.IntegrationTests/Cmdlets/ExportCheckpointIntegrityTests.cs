using System.Management.Automation;
using System.Net;
using System.Reflection;
using System.Text;
using Mgx.Cmdlets.Base;
using Mgx.Engine.Http;
using Mgx.Engine.Pagination;

namespace Mgx.IntegrationTests;

/// <summary>
/// An export writes a fresh run to a temp and moves it over the output, so a checkpoint from
/// an interrupted export counts items that are in the temp, not in the output. Recovery has to
/// know which file holds them: an output left over from an EARLIER export satisfies "the output
/// exists" while holding none of them.
/// </summary>
[Collection("Pipeline")]
public class ExportCheckpointIntegrityTests
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
        public List<string> Requests { get; } = [];

        public void Queue(HttpStatusCode status, string body)
        {
            lock (_lock) _steps.Enqueue((status, body));
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            (HttpStatusCode Status, string Body) step;
            lock (_lock)
            {
                Requests.Add(request.RequestUri!.ToString());
                step = _steps.Count > 0 ? _steps.Dequeue() : (HttpStatusCode.InternalServerError, ServerError);
            }
            return Task.FromResult(new HttpResponseMessage(step.Status)
            {
                RequestMessage = request,
                Content = new StringContent(step.Body, Encoding.UTF8, "application/json")
            });
        }
    }

    private static void InjectMock(HttpMessageHandler handler)
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

    private static long? Export(string outputPath, string? checkpointPath, bool all, int top = 0)
    {
        using var ps = PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection).Assembly);
        ps.Invoke();
        ps.Commands.Clear();
        ps.AddScript("function Get-MgContext { [PSCustomObject]@{ TenantId = 'test-tenant-00000000-0000-0000-0000-000000000000' } }");
        ps.Invoke();
        ps.Commands.Clear();

        var cmd = ps.AddCommand("Export-MgxCollection")
                    .AddParameter("Uri", "/users")
                    .AddParameter("OutputFile", outputPath);
        if (checkpointPath != null) cmd.AddParameter("CheckpointPath", checkpointPath);
        if (all) cmd.AddParameter("All");
        if (top > 0) cmd.AddParameter("Top", top);
        try
        {
            foreach (var r in ps.Invoke())
                if (r?.BaseObject is Mgx.Cmdlets.Models.MgxExportResult summary)
                    return summary.ItemCount;
        }
        catch (CmdletInvocationException) { }
        return null;
    }

    private static string[] Lines(string path) => File.Exists(path) ? File.ReadAllLines(path) : [];

    private static string NewDir() =>
        Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-export-ckpt-{Guid.NewGuid():N}")).FullName;

    /// <summary>
    /// A narrow export, then a full one that dies, then the documented resume. The dead run's
    /// first page went to a temp and its checkpoint counted those items; the output still holds
    /// only the narrow export's. Resuming has to not treat that output as though it already
    /// held them.
    /// </summary>
    [Fact]
    public void A_resume_does_not_count_an_earlier_exports_output_as_this_ones_items()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            InjectMock(handler);

            // Run 1: a one-item probe export. Completes, no checkpoint.
            handler.Queue(HttpStatusCode.OK, Page1);
            Assert.Equal(1, Export(output, checkpointPath: null, all: false, top: 1));
            Assert.Equal(["{\"id\":\"u1\"}"], Lines(output));

            // Run 2: the full export. Page 1 is written and checkpointed; page 2 fails.
            handler.Queue(HttpStatusCode.OK, Page1);
            handler.Queue(HttpStatusCode.InternalServerError, ServerError);
            Export(output, checkpoint, all: true);

            var cp = PaginationCheckpoint.Load(checkpoint);
            Assert.NotNull(cp);
            Assert.Equal(2, cp!.ItemsCollected);   // it counted u1 and u2...
            Assert.Equal(["{\"id\":\"u1\"}"], Lines(output));  // ...which are not in the output

            // Run 3: the documented resume.
            handler.Queue(HttpStatusCode.OK, Page1);
            handler.Queue(HttpStatusCode.OK, Page2);
            var reported = Export(output, checkpoint, all: true);

            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"], Lines(output));
            Assert.Equal(3, reported);
            Assert.Empty(Directory.GetFiles(dir, "out.jsonl.*.tmp"));
        }
        finally
        {
            CleanupMock();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The ordinary re-baseline: the same export run again over its own previous output. A
    /// resume must not leave the previous run's rows in front of this run's.
    /// </summary>
    [Fact]
    public void A_resume_replaces_the_previous_export_rather_than_appending_to_it()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            InjectMock(handler);

            handler.Queue(HttpStatusCode.OK, Page1);
            handler.Queue(HttpStatusCode.OK, Page2);
            Assert.Equal(3, Export(output, checkpointPath: null, all: true));

            handler.Queue(HttpStatusCode.OK, Page1);
            handler.Queue(HttpStatusCode.InternalServerError, ServerError);
            Export(output, checkpoint, all: true);

            handler.Queue(HttpStatusCode.OK, Page1);
            handler.Queue(HttpStatusCode.OK, Page2);
            var reported = Export(output, checkpoint, all: true);

            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"], Lines(output));
            Assert.Equal(3, reported);
        }
        finally
        {
            CleanupMock();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// A surviving temp is adopted rather than abandoned, even though an output from an earlier
    /// export is sitting next to it. The temp is what a hard kill leaves - the process dies
    /// before the cleanup that an ordinary failure runs - so it is staged directly here.
    /// </summary>
    [Fact]
    public void A_surviving_temp_is_promoted_over_an_earlier_exports_output()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            InjectMock(handler);

            handler.Queue(HttpStatusCode.OK, Page1);
            Assert.Equal(1, Export(output, checkpointPath: null, all: false, top: 1));

            // What a kill -9 leaves partway through a fresh checkpointed run.
            var temp = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
            var bytes = "{\"id\":\"u1\"}\n{\"id\":\"u2\"}\n";
            File.WriteAllText(temp, bytes);
            new PaginationCheckpoint
            {
                Resource = "https://graph.microsoft.com/v1.0/users?$top=999",
                NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=P2",
                ItemsCollected = 2,
                PageItemsAlreadyWritten = 0,
                TempFile = Path.GetFileName(temp),
                DataLength = new FileInfo(temp).Length
            }.Save(checkpoint);

            handler.Queue(HttpStatusCode.OK, Page2);
            Export(output, checkpoint, all: true);

            // The temp's items replaced the earlier export's output; they are not behind it.
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"], Lines(output));
            Assert.False(File.Exists(temp));
        }
        finally
        {
            CleanupMock();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// A checkpoint written before any of this was recorded says nothing about where its items
    /// are. It must still resume the way it always did rather than be treated as describing a
    /// zero-length file.
    /// </summary>
    [Fact]
    public void A_checkpoint_without_a_recorded_length_still_resumes()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            InjectMock(handler);

            File.WriteAllText(output, "{\"id\":\"u1\"}\n{\"id\":\"u2\"}\n");
            new PaginationCheckpoint
            {
                Resource = "https://graph.microsoft.com/v1.0/users?$top=999",
                NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=P2",
                ItemsCollected = 2,
                PageItemsAlreadyWritten = 0
                // TempFile and DataLength absent, as an older release wrote them
            }.Save(checkpoint);

            handler.Queue(HttpStatusCode.OK, Page2);
            Export(output, checkpoint, all: true);

            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"], Lines(output));
        }
        finally
        {
            CleanupMock();
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
