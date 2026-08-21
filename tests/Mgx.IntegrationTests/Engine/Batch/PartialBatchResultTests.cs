using System.Net;
using Mgx.Engine.Http;
using Mgx.Engine.Models;

namespace Mgx.IntegrationTests;

/// <summary>
/// A batch larger than Graph's 20-request limit is split into chunks and sent one after another.
/// If a later chunk's POST fails outright, the earlier chunks have already been applied - the
/// writes happened, on the server. Losing their results loses the only record of what landed,
/// which is exactly what a caller needs in order to know what to do next.
/// </summary>
[Collection("Pipeline")]
public class PartialBatchResultTests
{
    private static string BatchResponse(int firstId, int count, int status) =>
        "{ \"responses\": [" + string.Join(",", Enumerable.Range(firstId, count).Select(i =>
            $"{{ \"id\": \"{i}\", \"status\": {status}, \"body\": {{ \"id\": \"u{i}\" }} }}")) + "] }";

    /// <summary>First POST answers a full chunk; every later POST is a hard failure.</summary>
    private sealed class FirstChunkOnlyHandler : HttpMessageHandler
    {
        public int Posts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Posts++;
            if (Posts == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(BatchResponse(1, 20, 200),
                        System.Text.Encoding.UTF8, "application/json")
                });
            }
            // Not a per-item failure inside a 200 envelope - the POST itself fails.
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
    public async Task A_failed_chunk_does_not_discard_the_chunks_that_already_succeeded()
    {
        ResiliencePipelineFactory.Reset();
        var wire = new FirstChunkOnlyHandler();
        using var httpClient = new HttpClient(wire);
        using var client = new ResilientGraphClient(httpClient,
            new ResilientGraphClientOptions { NoRateLimit = true, MaxRetryAttempts = 1 });
        var batchClient = new GraphBatchClient(client);

        // 30 operations = two chunks. The first is applied; the second is refused.
        var ops = Enumerable.Range(1, 30)
            .Select(i => new BatchOperation($"/users/u{i}", "PATCH", null))
            .ToList();

        var result = await batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken.None);

        // The 20 writes in chunk one happened. Their outcomes must survive the second chunk's
        // failure, or the caller cannot tell an applied write from one that never ran.
        var known = result.Results.Count(r => r.Response is not null);
        Assert.True(known >= 20,
            $"only {known} of 30 operations came back; the first chunk's 20 results were discarded");
    }

    /// <summary>Counts requests; answers an empty batch envelope.</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(BatchResponse(1, 2, 200),
                    System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>
    /// The help says of -WhatIf: "Shows what would happen if the cmdlet runs. The cmdlet is not
    /// run." A read-only batch changes nothing on the server, but it still spends resource units,
    /// can be throttled, and emits objects into the pipeline - so running it is not "not run",
    /// and it is not what the caller asked for.
    /// </summary>
    [Fact]
    public void WhatIf_does_not_send_a_read_only_batch()
    {
        ResiliencePipelineFactory.Reset();
        var wire = new CountingHandler();
        var t = typeof(Mgx.Cmdlets.Base.MgxCmdletBase);
        t.GetField("s_graphHttpClient", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, new HttpClient(wire));
        t.GetField("s_cachedAuthFingerprint", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, Mgx.Cmdlets.Base.MgxCmdletBase.BuildAuthFingerprint(
                new { TenantId = "test-tenant-00000000-0000-0000-0000-000000000000" }, null));
        t.GetField("s_ownsHttpClient", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, false);
        t.GetField("s_graphEndpoint", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)!
            .SetValue(null, "https://graph.microsoft.com");
        t.GetField("s_clientOptions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)!
            .SetValue(null, new ResilientGraphClientOptions { NoRateLimit = true, MaxRetryAttempts = 1 });
        ResiliencePipelineFactory.Reset();

        try
        {
            using var ps = System.Management.Automation.PowerShell.Create();
            ps.AddCommand("Import-Module")
              .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Batch.InvokeMgxBatchRequest).Assembly);
            ps.Invoke();
            ps.Commands.Clear();
            ps.AddScript("function Get-MgContext { [PSCustomObject]@{ TenantId = 'test-tenant-00000000-0000-0000-0000-000000000000' } }");
            ps.Invoke();
            ps.Commands.Clear();

            ps.AddCommand("Invoke-MgxBatchRequest")
              .AddParameter("Uri", new[] { "/users/u1", "/users/u2" })
              .AddParameter("Method", "GET")
              .AddParameter("WhatIf", true);
            var output = ps.Invoke();

            Assert.Equal(0, wire.Requests);
            Assert.Empty(output);
        }
        finally
        {
            t.GetField("s_graphHttpClient", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.SetValue(null, null);
            t.GetField("s_cachedAuthFingerprint", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.SetValue(null, null);
            ResiliencePipelineFactory.Reset();
        }
    }
}
