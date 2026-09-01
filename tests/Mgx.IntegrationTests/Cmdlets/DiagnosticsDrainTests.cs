using System.Net;
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
    [Fact]
    public void An_unexpected_exception_still_delivers_the_buffered_retry_history()
    {
        var wire = new MockHttpHandler();
        wire.QueueResponse(HttpStatusCode.ServiceUnavailable);
        wire.QueueException(new InvalidOperationException("boom"));

        using (MgxTransportScope.Inject(wire, options: new ResilientGraphClientOptions
        {
            NoRateLimit = true, MaxRetryAttempts = 3, MaxRetryAfterSeconds = 1
        }))
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
    }
}
