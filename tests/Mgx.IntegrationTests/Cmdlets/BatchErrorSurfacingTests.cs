using System.Net;
using Mgx.Engine.Http;

namespace Mgx.IntegrationTests;

/// <summary>
/// A failed batch item is an error the pipeline can see: it lands in $Error, counts for
/// -ErrorVariable, and stops the pipeline under -ErrorAction Stop - with or without a
/// dead-letter file. (Corpus: M365DSC-7198, batch semantics.)
/// </summary>
[Collection("Pipeline")]
public class BatchErrorSurfacingTests
{
    private static System.Management.Automation.PowerShell CreateShell()
    {
        var ps = System.Management.Automation.PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Batch.InvokeMgxBatchRequest).Assembly);
        ps.Invoke();
        ps.Commands.Clear();
        ps.AddScript("function Get-MgContext { [PSCustomObject]@{ TenantId = 'test-tenant-00000000-0000-0000-0000-000000000000' } }");
        ps.Invoke();
        ps.Commands.Clear();
        return ps;
    }

    private const string OneOkOneNotFound = """
    { "responses": [
        { "id": "1", "status": 200, "body": { "id": "u1" } },
        { "id": "2", "status": 404, "body": { "error": { "code": "Request_ResourceNotFound", "message": "u2 does not exist" } } }
    ] }
    """;

    [Fact]
    public void A_failed_item_writes_an_error_record_without_a_dead_letter_path()
    {
        var wire = new MockHttpHandler();
        wire.QueueResponse(HttpStatusCode.OK, OneOkOneNotFound);
        using (MgxTransportScope.Inject(wire))
        {
            using var ps = CreateShell();
            ps.AddCommand("Invoke-MgxBatchRequest")
              .AddParameter("Uri", new[] { "/users/u1", "/users/u2" })
              .AddParameter("Method", "GET");
            var output = ps.Invoke();

            Assert.Equal(2, output.Count);
            var error = Assert.Single(ps.Streams.Error);
            Assert.StartsWith("BatchItemError", error.FullyQualifiedErrorId);
            Assert.Contains("u2", error.Exception.Message);
            Assert.Equal(System.Management.Automation.ErrorCategory.ObjectNotFound,
                error.CategoryInfo.Category);
        }
    }

    [Fact]
    public void ErrorAction_Stop_stops_on_a_failed_item()
    {
        var wire = new MockHttpHandler();
        wire.QueueResponse(HttpStatusCode.OK, OneOkOneNotFound);
        using (MgxTransportScope.Inject(wire))
        {
            using var ps = CreateShell();
            ps.AddCommand("Invoke-MgxBatchRequest")
              .AddParameter("Uri", new[] { "/users/u1", "/users/u2" })
              .AddParameter("Method", "GET")
              .AddParameter("ErrorAction", "Stop");
            var ex = Assert.ThrowsAny<Exception>(() => ps.Invoke());
            Assert.Contains("u2", ex.Message);
        }
    }

    /// <summary>First POST answers its chunk in full; every later POST is refused outright.</summary>
    private sealed class FirstChunkOnlyHandler : HttpMessageHandler
    {
        private int _posts;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _posts++;
            if (_posts == 1)
            {
                var body = "{ \"responses\": [" + string.Join(",", Enumerable.Range(1, 20).Select(i =>
                    $"{{ \"id\": \"{i}\", \"status\": 200, \"body\": {{ \"id\": \"u{i}\" }} }}")) + "] }";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                RequestMessage = request,
                Content = new StringContent(
                    "{\"error\":{\"code\":\"BadRequest\",\"message\":\"batch rejected\"}}",
                    System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    [Fact]
    public void Stop_preference_cannot_cut_the_dead_letter_file_short()
    {
        using var transport = MgxTransportScope.Inject(new FirstChunkOnlyHandler());
        var deadLetter = Path.Combine(Path.GetTempPath(), $"mgx-dl-{Guid.NewGuid():N}.jsonl");
        try
        {
            using var ps = CreateShell();
            ps.AddCommand("Invoke-MgxBatchRequest")
              .AddParameter("Uri", Enumerable.Range(1, 25).Select(i => $"/users/u{i}").ToArray())
              .AddParameter("Method", "GET")
              .AddParameter("DeadLetterPath", deadLetter)
              .AddParameter("ErrorAction", "Stop");
            try { ps.Invoke(); } catch { /* Stop terminates - expected */ }

            // First chunk answered 20 items (all 200 in this fixture), second chunk's POST was
            // refused, so its 5 items failed. Every failure must be on disk even though Stop
            // terminated at the first error record.
            Assert.True(File.Exists(deadLetter), "dead-letter file was not written");
            var lines = File.ReadAllLines(deadLetter);
            Assert.Equal(5, lines.Length);
        }
        finally
        {
            File.Delete(deadLetter);
        }
    }

    [Fact]
    public void Items_of_a_refused_chunk_each_write_an_error_record()
    {
        using (MgxTransportScope.Inject(new FirstChunkOnlyHandler()))
        {
            using var ps = CreateShell();
            ps.AddCommand("Invoke-MgxBatchRequest")
              .AddParameter("Uri", Enumerable.Range(1, 25).Select(i => $"/users/u{i}").ToArray())
              .AddParameter("Method", "GET");
            ps.Invoke();

            var errors = ps.Streams.Error.ToList();
            // The second chunk's five items went out and were refused, so each reads as a failed
            // request. None of them is "not sent": that is reserved for a chunk never POSTed.
            Assert.Equal(5, errors.Count(e => e.FullyQualifiedErrorId.StartsWith("BatchItemError")));
            Assert.DoesNotContain(errors, e => e.FullyQualifiedErrorId.StartsWith("BatchItemNotSent"));
            var chunk = Assert.Single(errors, e => e.FullyQualifiedErrorId.StartsWith("BatchChunkFailed"));
            // The chunk died on a 400; the record carries the classified category, not NotSpecified.
            Assert.Equal(System.Management.Automation.ErrorCategory.InvalidArgument,
                chunk.CategoryInfo.Category);
            // Nothing was left unsent here - the refused chunk was the last one. The record has
            // to say what did happen to its five operations, not only what did not.
            Assert.Equal("5 of 25 operations failed, 0 were not sent", chunk.TargetObject);
        }
    }

    /// <summary>One chunk, both items answered, the second sub-response missing its status.</summary>
    private const string OneOkOneWithoutAStatus = """
    { "responses": [
        { "id": "1", "status": 204, "body": null },
        { "id": "2", "body": null }
    ] }
    """;

    /// <summary>
    /// "Not sent" is the cmdlet's word for a write that certainly did not reach the server, and
    /// it carries a NotSent flag on the output, an error record telling the caller another chunk
    /// failed, and a dead-letter line the help documents as re-pipeable. A PATCH the server
    /// answered must reach none of those.
    /// </summary>
    [Fact]
    public void A_write_answered_without_a_status_is_not_offered_back_for_resubmission()
    {
        var wire = new MockHttpHandler();
        wire.QueueResponse(HttpStatusCode.OK, OneOkOneWithoutAStatus);
        var deadLetter = Path.Combine(Path.GetTempPath(), $"mgx-dl-{Guid.NewGuid():N}.jsonl");
        try
        {
            using (MgxTransportScope.Inject(wire))
            {
                using var ps = CreateShell();
                ps.AddCommand("Invoke-MgxBatchRequest")
                  .AddParameter("Uri", new[] { "/users/u1", "/users/u2" })
                  .AddParameter("Method", "PATCH")
                  .AddParameter("Body", new System.Collections.Hashtable { ["x"] = 1 })
                  .AddParameter("DeadLetterPath", deadLetter);
                var output = ps.Invoke();

                var second = Assert.IsType<System.Collections.Hashtable>(output[1].BaseObject);
                Assert.Equal("/users/u2", second["Url"]);
                Assert.NotEqual(0, Assert.IsType<int>(second["Status"]));
                Assert.False(second.ContainsKey("NotSent"),
                    "a write the server answered was flagged as one that never went out");

                Assert.DoesNotContain(ps.Streams.Error,
                    e => e.FullyQualifiedErrorId.StartsWith("BatchItemNotSent", StringComparison.Ordinal));
            }

            var line = Assert.Single(File.ReadAllLines(deadLetter), l => l.Contains("/users/u2", StringComparison.Ordinal));
            // Compact, as the writer emits it: the dead-letter serializer does not indent, so a
            // needle with a space after the colon matches nothing the file can ever hold.
            Assert.DoesNotContain("\"Status\":0", line, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(deadLetter)) File.Delete(deadLetter);
        }
    }
}
