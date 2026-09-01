using System.Net;
using System.Reflection;
using System.Text;
using Mgx.Cmdlets.Base;
using Mgx.Engine.Http;

namespace Mgx.IntegrationTests;

/// <summary>
/// A cmdlet builds one ResilientGraphClient over the shared transport and pages through it for
/// the life of its enumeration. Anything that swaps the transport - Connect-MgGraph to another
/// identity, Set-MgxOption, Enable-MgxResilience, Remove-Module - must leave that client able
/// to fetch its next page. The replaced HttpClient used to be disposed TotalTimeoutSeconds
/// later, a per-request timeout used as a resource lifetime, so a long-running operation died
/// mid-stream with ObjectDisposedException.
/// </summary>
[Collection("Pipeline")]
public class TransportSwapTests
{
    private sealed class PageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("""{"value":[{"id":"u1"}]}""",
                    Encoding.UTF8, "application/json")
            });
    }

    private static readonly BindingFlags Statics = BindingFlags.NonPublic | BindingFlags.Static;

    private static ResilientGraphClientOptions ShortTimeoutOptions()
    {
        var options = new ResilientGraphClientOptions { NoRateLimit = true, MaxRetryAttempts = 1 };
        // A legal value (the range is 1..3600), set so the old disposal timer would fire
        // inside the test rather than five minutes after it.
        typeof(ResilientGraphClientOptions)
            .GetField("_totalTimeoutSeconds", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(options, 1);
        return options;
    }

    [Fact]
    public async Task A_client_a_running_cmdlet_holds_survives_a_transport_reset()
    {
        ResiliencePipelineFactory.Reset();
        var t = typeof(MgxCmdletBase);
        var options = ShortTimeoutOptions();
        var transport = new HttpClient(new PageHandler());
        t.GetField("s_graphHttpClient", Statics)!.SetValue(null, transport);
        t.GetField("s_ownsHttpClient", Statics)!.SetValue(null, true);
        t.GetField("s_clientOptions", Statics | BindingFlags.Public)!.SetValue(null, options);

        try
        {
            // What a cmdlet holds for the whole of its enumeration.
            using var held = new ResilientGraphClient(transport, options);

            var page1 = await held.SendAsync(HttpMethod.Get, "https://graph.microsoft.com/v1.0/users");
            Assert.Equal(HttpStatusCode.OK, page1.StatusCode);

            // Another thread swaps the transport out from under it.
            MgxCmdletBase.ResetHttpClient();
            await Task.Delay(TimeSpan.FromSeconds(2.5));

            var page2 = await held.SendAsync(HttpMethod.Get, "https://graph.microsoft.com/v1.0/users?$skiptoken=P2");
            Assert.Equal(HttpStatusCode.OK, page2.StatusCode);
        }
        finally
        {
            t.GetField("s_graphHttpClient", Statics)!.SetValue(null, null);
            t.GetField("s_ownsHttpClient", Statics)!.SetValue(null, false);
            t.GetField("s_clientOptions", Statics | BindingFlags.Public)!
                .SetValue(null, ResilientGraphClientOptions.Default);
            t.GetField("s_cachedAuthFingerprint", Statics)!.SetValue(null, null);
            t.GetField("s_cachedAuthContextRef", Statics)!.SetValue(null, null);
            ResiliencePipelineFactory.Reset();
        }
    }
}
