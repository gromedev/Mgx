using System.Net;
using Mgx.Engine.Http;

namespace Mgx.IntegrationTests;

/// <summary>
/// The two error ids a write fan-out can report are a statement about what reached the server.
/// BulkWriteInfraError is for a write that got no HTTP status at all - the network, an open
/// circuit - and it is the one a caller may repeat. Everything else carries the status the
/// server answered with, a body that failed to read after that answer included: the write may
/// have been applied, and repeating it is a decision about duplicates rather than a free retry.
/// </summary>
[Collection("Pipeline")]
public class BulkWriteErrorSurfacingTests
{
    private static (List<System.Management.Automation.ErrorRecord> Errors, int Output) Run(string script)
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
        return (ps.Streams.Error.ToList(), output.Count);
    }

    /// <summary>
    /// A 201 whose body does not parse. The POST reached the server and the server applied it;
    /// only the read of what it created failed. Reported under the id reserved for a write that
    /// got no status, it would read as the one failure a caller can safely repeat - and each
    /// repeat creates the entity a second time.
    /// </summary>
    [Fact]
    public void A_body_that_does_not_parse_after_a_201_keeps_the_status_the_server_answered()
    {
        var wire = new MockHttpHandler();
        wire.SetDefaultResponse(HttpStatusCode.Created, "THIS IS NOT JSON");
        using (MgxTransportScope.Inject(wire))
        {
            var (errors, _) = Run(
                "'u1','u2' | Invoke-MgxRequest -Uri '/users/{id}' -Method POST -Body @{ x = 1 } -Confirm:$false");

            Assert.Equal(2, errors.Count);
            Assert.DoesNotContain(errors,
                e => e.FullyQualifiedErrorId.StartsWith("BulkWriteInfraError", StringComparison.Ordinal));
            Assert.All(errors, e =>
                Assert.StartsWith("BulkWriteError", e.FullyQualifiedErrorId, StringComparison.Ordinal));
            Assert.All(errors, e =>
                Assert.Contains("HTTP 201", e.Exception.Message, StringComparison.Ordinal));
        }
    }

    /// <summary>Fails every request before a status exists, the way a dead route does.</summary>
    private sealed class NeverConnectsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("no route to host");
    }

    /// <summary>
    /// The other half of the same statement: a write that never reached a status is the one that
    /// takes the infrastructure branch, and it still does.
    /// </summary>
    [Fact]
    public void A_write_that_reached_no_status_is_the_one_reported_as_infrastructure()
    {
        using (MgxTransportScope.Inject(new NeverConnectsHandler(), options: new ResilientGraphClientOptions
        {
            NoRateLimit = true,
            MaxRetryAttempts = 1
        }))
        {
            var (errors, _) = Run(
                "'u1','u2' | Invoke-MgxRequest -Uri '/users/{id}' -Method POST -Body @{ x = 1 } -Confirm:$false");

            Assert.NotEmpty(errors);
            Assert.All(errors, e =>
                Assert.StartsWith("BulkWriteInfraError", e.FullyQualifiedErrorId, StringComparison.Ordinal));
        }
    }
}
