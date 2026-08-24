using System.Net;
using System.Reflection;
using Mgx.Engine.Http;

namespace Mgx.IntegrationTests;

/// <summary>
/// When a request dies on an exception type no catch filter expects, the buffered verbose
/// messages - the retry history that explains what led up to the failure - still reach the
/// caller instead of being discarded with the client.
/// </summary>
[Collection("Pipeline")]
public class DiagnosticsDrainTests
{
    private static readonly Type Base = typeof(Mgx.Cmdlets.Base.MgxCmdletBase);
    private const BindingFlags Static = BindingFlags.NonPublic | BindingFlags.Static;

    [Fact]
    public void An_unexpected_exception_still_delivers_the_buffered_retry_history()
    {
        var wire = new MockHttpHandler();
        wire.QueueResponse(HttpStatusCode.ServiceUnavailable);
        wire.QueueException(new InvalidOperationException("boom"));

        Base.GetField("s_graphHttpClient", Static)!.SetValue(null, new HttpClient(wire));
        Base.GetField("s_cachedAuthFingerprint", Static)!.SetValue(null,
            Mgx.Cmdlets.Base.MgxCmdletBase.BuildAuthFingerprint(
                new { TenantId = "test-tenant-00000000-0000-0000-0000-000000000000" }, null));
        Base.GetField("s_ownsHttpClient", Static)!.SetValue(null, false);
        Base.GetField("s_graphEndpoint", Static)!.SetValue(null, "https://graph.microsoft.com");
        Base.GetField("s_clientOptions", Static)!.SetValue(null,
            new ResilientGraphClientOptions { NoRateLimit = true, MaxRetryAttempts = 3, MaxRetryAfterSeconds = 1 });
        ResiliencePipelineFactory.Reset();
        try
        {
            using var ps = System.Management.Automation.PowerShell.Create();
            ps.AddCommand("Import-Module")
              .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.InvokeMgxRequest).Assembly);
            ps.Invoke();
            ps.Commands.Clear();
            ps.AddScript("function Get-MgContext { [PSCustomObject]@{ TenantId = 'test-tenant-00000000-0000-0000-0000-000000000000' } }");
            ps.Invoke();
            ps.Commands.Clear();

            ps.AddCommand("Invoke-MgxRequest")
              .AddParameter("Uri", "/users/u1")
              .AddParameter("Verbose", true);
            Assert.ThrowsAny<Exception>(() => ps.Invoke());

            Assert.Contains(ps.Streams.Verbose,
                v => v.Message.Contains("Retry attempt", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Base.GetField("s_graphHttpClient", Static)!.SetValue(null, null);
            Base.GetField("s_cachedAuthFingerprint", Static)!.SetValue(null, null);
            ResiliencePipelineFactory.Reset();
        }
    }
}
