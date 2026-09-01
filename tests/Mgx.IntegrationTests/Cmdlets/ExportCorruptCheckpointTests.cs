using System.Management.Automation;
using System.Net;
using System.Text;
using Mgx.Engine.Pagination;

namespace Mgx.IntegrationTests;

/// <summary>
/// A checkpoint file that cannot be read is not a resume. PaginationCheckpoint.Load answers
/// null for a truncated file, an empty one, a field of the wrong JSON type and a file the
/// account cannot open - and an export that decided to append from the checkpoint merely
/// EXISTING wrote a second complete copy of the collection onto the first, reporting the
/// item count of one pass while the file held two.
/// </summary>
[Collection("Pipeline")]
public class ExportCorruptCheckpointTests
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

    private static long? Export(string outputPath, string? checkpointPath)
    {
        using var ps = PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection).Assembly);
        ps.Invoke();
        ps.Commands.Clear();

        var cmd = ps.AddCommand("Export-MgxCollection")
                    .AddParameter("Uri", "/users")
                    .AddParameter("OutputFile", outputPath)
                    .AddParameter("All");
        if (checkpointPath != null) cmd.AddParameter("CheckpointPath", checkpointPath);
        try
        {
            foreach (var r in ps.Invoke())
                if (r?.BaseObject is Mgx.Cmdlets.Models.MgxExportResult summary)
                    return summary.ItemCount;
        }
        catch (CmdletInvocationException) { }
        return null;
    }

    private static string NewDir() =>
        Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-export-corrupt-{Guid.NewGuid():N}")).FullName;

    private static void ExportsOnceOver(string checkpointContent)
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            // A completed export, with no checkpoint of its own.
            handler.Queue(HttpStatusCode.OK, Page1);
            handler.Queue(HttpStatusCode.OK, Page2);
            Assert.Equal(3, Export(output, checkpointPath: null));

            File.WriteAllText(checkpoint, checkpointContent);

            // Same export again, now pointed at a checkpoint nothing can read.
            handler.Queue(HttpStatusCode.OK, Page1);
            handler.Queue(HttpStatusCode.OK, Page2);
            var reported = Export(output, checkpoint);

            var lines = File.ReadAllLines(output);
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"], lines);
            Assert.Equal(3, reported);
            Assert.Equal(lines.Length, reported);
            Assert.False(File.Exists(checkpoint));
            Assert.Empty(Directory.GetFiles(dir, "out.jsonl.*.tmp"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void A_truncated_checkpoint_does_not_append_a_whole_second_export() =>
        ExportsOnceOver("""{"resource":"https://graph.microsoft.com/v1.0/users?$top=999","nextLi""");

    [Fact]
    public void An_empty_checkpoint_does_not_append_a_whole_second_export() =>
        ExportsOnceOver("");

    [Fact]
    public void A_checkpoint_with_a_field_of_the_wrong_type_does_not_append_a_whole_second_export() =>
        ExportsOnceOver("""
        {"resource":"https://graph.microsoft.com/v1.0/users?$top=999",
         "nextLink":"https://graph.microsoft.com/v1.0/users?$skiptoken=P2",
         "itemsCollected":"two","pageItemsAlreadyWritten":0,"tempFile":null,"dataLength":24}
        """);

    /// <summary>
    /// The same branch, reached by a file that is torn rather than closed: the crash that left
    /// it got between the write and the rename. This is the one shape of "does not load" that
    /// needs nothing of the filesystem to produce, so it is the shape that can be asserted
    /// where a Unix file mode cannot go - Windows denies a read through an ACL, and the test
    /// below returns before its first assertion there.
    /// </summary>
    [Fact]
    public void A_checkpoint_this_run_cannot_parse_is_left_for_a_run_that_can()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            File.WriteAllText(checkpoint,
                """{"resource":"https://graph.microsoft.com/v1.0/users?$top=999","nextLi""");
            var before = File.ReadAllBytes(checkpoint);

            // The export fails on its first page, so the success path - which deletes the
            // checkpoint whatever it holds - is not what this measures.
            Assert.Null(Export(output, checkpoint));

            Assert.True(File.Exists(checkpoint),
                "a position this run could not read was deleted anyway");
            Assert.Equal(before, File.ReadAllBytes(checkpoint));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// "Cannot be read" is a lock or a denying ACL as often as it is a torn file. Deleting the
    /// checkpoint on that reading changed nothing about this run - a load that answers null
    /// already forces a fresh export - while destroying a position the next run, or another
    /// account, could still have resumed from.
    /// </summary>
    [Fact]
    public void A_checkpoint_this_run_cannot_read_is_left_for_a_run_that_can()
    {
        // A file the account cannot open. Windows expresses that through ACLs, not a mode.
        if (OperatingSystem.IsWindows()) return;

        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            new PaginationCheckpoint
            {
                Resource = "https://graph.microsoft.com/v1.0/users?$top=999",
                NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=P2",
                ItemsCollected = 2,
                PageItemsAlreadyWritten = 0,
                TempFile = null,
                OutputFile = output,
                DataLength = 24,
            }.Save(checkpoint);
            var before = File.ReadAllBytes(checkpoint);
            File.SetUnixFileMode(checkpoint, UnixFileMode.None);

            // The export fails on its first page, so nothing that runs on success can be what
            // removes the checkpoint.
            Assert.Null(Export(output, checkpoint));

            var survived = File.Exists(checkpoint);
            if (survived)
            {
                File.SetUnixFileMode(checkpoint, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                Assert.Equal(before, File.ReadAllBytes(checkpoint));
            }
            Assert.True(survived, "a position this run could not read was deleted anyway");
        }
        finally
        {
            try { File.SetUnixFileMode(checkpoint, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch { }
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
