using System.Net;
using System.Reflection;
using Mgx.Engine.Http;

namespace Mgx.IntegrationTests;

/// <summary>
/// Header values from a -Headers hashtable reach the wire as HTTP header text.
/// </summary>
[Collection("Pipeline")]
public class HeaderWireTests
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
        // Restore every static InjectTransport touched - a later test in the collection
        // that drives a cmdlet without injecting must not inherit this class's transport.
        Base.GetField("s_graphHttpClient", Static)!.SetValue(null, null);
        Base.GetField("s_cachedAuthFingerprint", Static)!.SetValue(null, null);
        Base.GetField("s_ownsHttpClient", Static)!.SetValue(null, false);
        Base.GetField("s_cachedTotalTimeoutSeconds", Static)!.SetValue(null, 0);
        Base.GetField("s_graphEndpoint", Static)!.SetValue(null, "https://graph.microsoft.com");
        Base.GetField("s_clientOptions", Static)!.SetValue(null, new ResilientGraphClientOptions());
        ResiliencePipelineFactory.Reset();
    }

    [Fact]
    public void An_array_header_value_joins_as_an_http_list_not_a_type_name()
    {
        var wire = new MockHttpHandler();
        wire.SetDefaultResponse(HttpStatusCode.OK, TestData.SingleUser);
        InjectTransport(wire);
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
            ps.AddScript("Invoke-MgxRequest -Uri /users/u1 -Headers @{ Prefer = @('outlook.timezone=\"UTC\"', 'IdType=\"ImmutableId\"') }");
            ps.Invoke();

            var captured = wire.CapturedRequests;
            Assert.NotEmpty(captured);
            var prefer = Assert.Contains("Prefer", captured[0].Headers);
            Assert.Equal("outlook.timezone=\"UTC\", IdType=\"ImmutableId\"", string.Join(", ", prefer));
            Assert.DoesNotContain("System.", string.Join(",", prefer));
        }
        finally { ResetTransport(); }
    }
}
