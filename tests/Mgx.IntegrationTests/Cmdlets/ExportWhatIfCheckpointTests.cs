using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Management.Automation.Runspaces;
using System.Net;
using System.Security;
using System.Text;
using Mgx.Engine.Pagination;

namespace Mgx.IntegrationTests;

/// <summary>
/// -WhatIf on an export with a resume checkpoint. Recovery promotes temp files over outputs,
/// cuts outputs back to a recorded length and deletes checkpoints, and it ran above the
/// ShouldProcess gate - so the run that reported it would change nothing had already rewritten
/// the output and removed the position it was resuming from. The gate also has to name the
/// action a real run would take, which it cannot do from "a checkpoint file exists".
/// </summary>
[Collection("Pipeline")]
public class ExportWhatIfCheckpointTests
{
    private const string Page2 = """
    {"value":[{"id":"u3"}]}
    """;

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(Page2, Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>The lines -WhatIf writes go to the host, so a test that reads them needs one.</summary>
    private sealed class RecordingHost : PSHost
    {
        private readonly Guid _id = Guid.NewGuid();
        public RecordingHostUI Recorder { get; } = new();
        public override string Name => "MgxTestHost";
        public override Version Version => new(1, 0);
        public override Guid InstanceId => _id;
        public override PSHostUserInterface UI => Recorder;
        public override CultureInfo CurrentCulture => CultureInfo.InvariantCulture;
        public override CultureInfo CurrentUICulture => CultureInfo.InvariantCulture;
        public override void EnterNestedPrompt() { }
        public override void ExitNestedPrompt() { }
        public override void NotifyBeginApplication() { }
        public override void NotifyEndApplication() { }
        public override void SetShouldExit(int exitCode) { }
    }

    private sealed class RecordingHostUI : PSHostUserInterface
    {
        public List<string> Lines { get; } = [];
        public override PSHostRawUserInterface? RawUI => null;
        public override void Write(string value) => Lines.Add(value);
        public override void Write(ConsoleColor f, ConsoleColor b, string value) => Lines.Add(value);
        public override void WriteLine(string value) => Lines.Add(value);
        public override void WriteErrorLine(string value) => Lines.Add(value);
        public override void WriteDebugLine(string value) => Lines.Add(value);
        public override void WriteVerboseLine(string value) => Lines.Add(value);
        public override void WriteWarningLine(string value) => Lines.Add(value);
        public override void WriteProgress(long sourceId, ProgressRecord record) { }
        public override string ReadLine() => string.Empty;
        public override SecureString ReadLineAsSecureString() => new();
        public override Dictionary<string, PSObject> Prompt(
            string caption, string message, System.Collections.ObjectModel.Collection<FieldDescription> descriptions) => [];
        public override int PromptForChoice(
            string caption, string message,
            System.Collections.ObjectModel.Collection<ChoiceDescription> choices, int defaultChoice) => defaultChoice;
        public override PSCredential PromptForCredential(
            string caption, string message, string userName, string targetName) => PSCredential.Empty;
        public override PSCredential PromptForCredential(
            string caption, string message, string userName, string targetName,
            PSCredentialTypes allowedCredentialTypes, PSCredentialUIOptions options) => PSCredential.Empty;
    }

    private static string NewDir() =>
        Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-whatif-{Guid.NewGuid():N}")).FullName;

    /// <summary>Runs the export under -WhatIf and returns every line the gate wrote.</summary>
    private static (List<string> Lines, bool HadErrors) WhatIf(string output, string checkpoint)
    {
        var host = new RecordingHost();
        using var runspace = RunspaceFactory.CreateRunspace(host);
        runspace.Open();
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection).Assembly);
        ps.Invoke();
        ps.Commands.Clear();
        ps.AddCommand("Export-MgxCollection")
          .AddParameter("Uri", "/users")
          .AddParameter("OutputFile", output)
          .AddParameter("CheckpointPath", checkpoint)
          .AddParameter("All")
          .AddParameter("WhatIf", true);
        ps.Invoke();
        return (host.Recorder.Lines, ps.HadErrors);
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
    /// What a hard kill leaves partway through a fresh checkpointed export: the previous
    /// export's output, this run's items in a temp beside it, and a checkpoint naming both.
    /// A real run promotes the temp over the output and appends the rest; -WhatIf has to say
    /// so and touch none of the three. It also runs above the client, so it needs no session.
    /// </summary>
    [Fact]
    public void WhatIf_promotes_nothing_and_names_the_append_a_real_run_would_do()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        var temp = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(output, "{\"id\":\"old1\"}\n{\"id\":\"old2\"}\n{\"id\":\"old3\"}\n");
            File.WriteAllText(temp, "{\"id\":\"u1\"}\n{\"id\":\"u2\"}\n");
            new PaginationCheckpoint
            {
                Resource = "https://graph.microsoft.com/v1.0/users?$top=999",
                NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=P2",
                ItemsCollected = 2,
                PageItemsAlreadyWritten = 0,
                TempFile = Path.GetFileName(temp),
                OutputFile = output,
                DataLength = new FileInfo(temp).Length,
            }.Save(checkpoint);

            var outputBefore = File.ReadAllBytes(output);
            var tempBefore = File.ReadAllBytes(temp);
            var checkpointBefore = File.ReadAllBytes(checkpoint);

            // No transport and no Get-MgContext: reaching the client would end the run here.
            var (lines, hadErrors) = WhatIf(output, checkpoint);

            Assert.False(hadErrors, "-WhatIf asked for a Graph connection");
            Assert.Contains(lines, l => l.Contains("Append JSONL data") && l.Contains(output));
            Assert.Equal(outputBefore, File.ReadAllBytes(output));
            Assert.Equal(tempBefore, File.ReadAllBytes(temp));
            Assert.Equal(checkpointBefore, File.ReadAllBytes(checkpoint));
            Assert.False(File.Exists(output + ".adopt"));

            // And the run it described does exactly that.
            var handler = new CountingHandler();
            using var transport = MgxTransportScope.Inject(handler);
            Export(output, checkpoint);

            Assert.Equal(
                ["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"],
                File.ReadAllLines(output));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The same gate over a checkpoint that is another export's. A real run refuses it and
    /// exports from the beginning, so that is what -WhatIf has to report - and the checkpoint,
    /// which the refusal used to delete, is still there afterwards.
    /// </summary>
    [Fact]
    public void WhatIf_over_another_exports_checkpoint_names_an_export_and_deletes_nothing()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            File.WriteAllText(output, "{\"id\":\"old1\"}\n{\"id\":\"old2\"}\n");
            new PaginationCheckpoint
            {
                Resource = "https://graph.microsoft.com/v1.0/users?$top=999",
                NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=P2",
                ItemsCollected = 2,
                PageItemsAlreadyWritten = 0,
                TempFile = null,
                OutputFile = Path.Combine(dir, "elsewhere.jsonl"),
                DataLength = 12,
            }.Save(checkpoint);

            var outputBefore = File.ReadAllBytes(output);
            var checkpointBefore = File.ReadAllBytes(checkpoint);

            var (lines, hadErrors) = WhatIf(output, checkpoint);

            Assert.False(hadErrors, "-WhatIf asked for a Graph connection");
            Assert.Contains(lines, l => l.Contains("Export JSONL data") && l.Contains(output));
            Assert.DoesNotContain(lines, l => l.Contains("Append JSONL data"));
            Assert.Equal(outputBefore, File.ReadAllBytes(output));
            Assert.Equal(checkpointBefore, File.ReadAllBytes(checkpoint));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The same gate over a checkpoint whose temp another export is still writing. A real run
    /// takes nothing from that file and exports from the beginning, so reporting an append
    /// described a recovery that would not have happened - and the report is the only thing a
    /// caller running -WhatIf gets to act on.
    /// </summary>
    [Fact]
    public void WhatIf_over_a_temp_a_running_export_holds_names_an_export()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        var temp = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(output, "{\"id\":\"old1\"}\n{\"id\":\"old2\"}\n");

            // Opened the way an export opens its own temp, with two rows flushed and the run
            // that wrote them still going.
            using var live = new StreamWriter(temp, append: false);
            live.WriteLine("{\"id\":\"u1\"}");
            live.WriteLine("{\"id\":\"u2\"}");
            live.Flush();

            new PaginationCheckpoint
            {
                Resource = "https://graph.microsoft.com/v1.0/users?$top=999",
                NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=P2",
                ItemsCollected = 2,
                PageItemsAlreadyWritten = 0,
                TempFile = Path.GetFileName(temp),
                OutputFile = output,
                DataLength = new FileInfo(temp).Length,
            }.Save(checkpoint);

            var outputBefore = File.ReadAllBytes(output);
            var tempBefore = ReadShared(temp);
            var checkpointBefore = File.ReadAllBytes(checkpoint);

            var (lines, hadErrors) = WhatIf(output, checkpoint);

            Assert.False(hadErrors, "-WhatIf asked for a Graph connection");
            Assert.Contains(lines, l => l.Contains("Export JSONL data") && l.Contains(output));
            Assert.DoesNotContain(lines, l => l.Contains("Append JSONL data"));
            Assert.Equal(outputBefore, File.ReadAllBytes(output));
            Assert.Equal(tempBefore, ReadShared(temp));
            Assert.Equal(checkpointBefore, File.ReadAllBytes(checkpoint));
            Assert.False(File.Exists(output + ".adopt"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // Reads a file the test's own live writer still holds open: on Windows the reader
    // must offer ReadWrite sharing or the holder's write access denies the open.
    private static byte[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }
}
