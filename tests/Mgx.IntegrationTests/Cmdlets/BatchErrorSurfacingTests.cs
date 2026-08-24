using System.Net;
using System.Reflection;
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
    private static readonly Type Base = typeof(Mgx.Cmdlets.Base.MgxCmdletBase);
    private const BindingFlags Static = BindingFlags.NonPublic | BindingFlags.Static;

    private static void InjectTransport(HttpMessageHandler wire)
    {
        Base.GetField("s_graphHttpClient", Static)!.SetValue(null, new HttpClient(wire));
        Base.GetField("s_cachedAuthFingerprint", Static)!.SetValue(null,
            Mgx.Cmdlets.Base.MgxCmdletBase.BuildAuthFingerprint(
                new { TenantId = "test-tenant-00000000-0000-0000-0000-000000000000" }, null));
        Base.GetField("s_ownsHttpClient", Static)!.SetValue(null, false);
        Base.GetField("s_graphEndpoint", Static)!.SetValue(null, "https://graph.microsoft.com");
        Base.GetField("s_clientOptions", Static)!.SetValue(null,
            new ResilientGraphClientOptions { NoRateLimit = true, MaxRetryAttempts = 1 });
        ResiliencePipelineFactory.Reset();
    }

    private static void ResetTransport()
    {
        Base.GetField("s_graphHttpClient", Static)!.SetValue(null, null);
        Base.GetField("s_cachedAuthFingerprint", Static)!.SetValue(null, null);
        ResiliencePipelineFactory.Reset();
    }

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
        InjectTransport(wire);
        try
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
        finally { ResetTransport(); }
    }

    [Fact]
    public void ErrorAction_Stop_stops_on_a_failed_item()
    {
        var wire = new MockHttpHandler();
        wire.QueueResponse(HttpStatusCode.OK, OneOkOneNotFound);
        InjectTransport(wire);
        try
        {
            using var ps = CreateShell();
            ps.AddCommand("Invoke-MgxBatchRequest")
              .AddParameter("Uri", new[] { "/users/u1", "/users/u2" })
              .AddParameter("Method", "GET")
              .AddParameter("ErrorAction", "Stop");
            var ex = Assert.ThrowsAny<Exception>(() => ps.Invoke());
            Assert.Contains("u2", ex.Message);
        }
        finally { ResetTransport(); }
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
    public void Items_a_failed_chunk_never_sent_each_write_an_error_record()
    {
        InjectTransport(new FirstChunkOnlyHandler());
        try
        {
            using var ps = CreateShell();
            ps.AddCommand("Invoke-MgxBatchRequest")
              .AddParameter("Uri", Enumerable.Range(1, 25).Select(i => $"/users/u{i}").ToArray())
              .AddParameter("Method", "GET");
            ps.Invoke();

            var errors = ps.Streams.Error.ToList();
            Assert.Equal(5, errors.Count(e => e.FullyQualifiedErrorId.StartsWith("BatchItemNotSent")));
            Assert.Equal(1, errors.Count(e => e.FullyQualifiedErrorId.StartsWith("BatchChunkFailed")));
        }
        finally { ResetTransport(); }
    }
}
