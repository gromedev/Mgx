using System.Net;
using Mgx.Engine.Http;

namespace Mgx.IntegrationTests;

/// <summary>
/// Header values from a -Headers hashtable reach the wire as HTTP header text.
/// </summary>
[Collection("Pipeline")]
public class HeaderWireTests
{
    [Fact]
    public void An_array_header_value_joins_as_an_http_list_not_a_type_name()
    {
        var wire = new MockHttpHandler();
        wire.SetDefaultResponse(HttpStatusCode.OK, TestData.SingleUser);
        using (MgxTransportScope.Inject(wire))
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
    }
}
