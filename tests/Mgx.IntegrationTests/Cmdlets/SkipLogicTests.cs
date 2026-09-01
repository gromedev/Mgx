using System.Net;
using Mgx.Engine.Http;

namespace Mgx.IntegrationTests;

/// <summary>
/// -SkipNotFound / -SkipForbidden behave identically on all four paths that implement
/// them: single request, GET fan-out, write fan-out, and relation expansion. The decision
/// is shared code now; these hold each path to it.
/// </summary>
[Collection("Pipeline")]
public class SkipLogicTests
{
    private const string NotFoundBody =
        """{ "error": { "code": "Request_ResourceNotFound", "message": "missing" } }""";

    private static (int Output, List<System.Management.Automation.ErrorRecord> Errors, List<string> Warnings)
        Run(string script)
    {
        using var ps = System.Management.Automation.PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.InvokeMgxRequest).Assembly);
        ps.Invoke();
        ps.Commands.Clear();
        ps.AddScript("function Get-MgContext { [PSCustomObject]@{ TenantId = 'test-tenant-00000000-0000-0000-0000-000000000000' } }");
        ps.Invoke();
        ps.Commands.Clear();
        ps.AddScript(script);
        var output = ps.Invoke();
        return (output.Count, ps.Streams.Error.ToList(),
            ps.Streams.Warning.Select(w => w.Message).ToList());
    }

    [Theory]
    [InlineData("Invoke-MgxRequest -Uri /users/u1 -SkipNotFound")]
    [InlineData("'u1','u2' | Invoke-MgxRequest -Uri '/users/{id}' -SkipNotFound")]
    [InlineData("'u1','u2' | Invoke-MgxRequest -Uri '/users/{id}' -Method PATCH -Body @{ x = 1 } -Confirm:$false -SkipNotFound")]
    [InlineData("[pscustomobject]@{ id = 'u1' } | Expand-MgxRelation -Uri '/users/{id}/manager' -As manager -SkipNotFound")]
    public void A_404_is_skipped_without_an_error_on_every_path(string script)
    {
        var wire = new MockHttpHandler();
        wire.SetDefaultResponse(HttpStatusCode.NotFound, NotFoundBody);
        using (MgxTransportScope.Inject(wire))
        {
            var (_, errors, _) = Run(script);
            Assert.Empty(errors);
        }
    }

    [Theory]
    [InlineData("Invoke-MgxRequest -Uri /users/u1")]
    [InlineData("'u1','u2' | Invoke-MgxRequest -Uri '/users/{id}'")]
    [InlineData("'u1','u2' | Invoke-MgxRequest -Uri '/users/{id}' -Method PATCH -Body @{ x = 1 } -Confirm:$false")]
    [InlineData("[pscustomobject]@{ id = 'u1' } | Expand-MgxRelation -Uri '/users/{id}/manager' -As manager")]
    public void The_same_404_is_an_error_without_the_switch(string script)
    {
        var wire = new MockHttpHandler();
        wire.SetDefaultResponse(HttpStatusCode.NotFound, NotFoundBody);
        using (MgxTransportScope.Inject(wire))
        {
            var (_, errors, _) = Run(script);
            Assert.NotEmpty(errors);
        }
    }

    [Fact]
    public void Skipped_items_are_counted_in_one_warning_summary()
    {
        var wire = new MockHttpHandler();
        wire.SetDefaultResponse(HttpStatusCode.NotFound, NotFoundBody);
        using (MgxTransportScope.Inject(wire))
        {
            var (output, errors, warnings) = Run(
                "'u1','u2','u3' | Invoke-MgxRequest -Uri '/users/{id}' -SkipNotFound");
            Assert.Equal(0, output);
            Assert.Empty(errors);
            Assert.Contains(warnings, w => w.Contains("Skipped 3"));
        }
    }
}
