using System.Net;
using Mgx.Engine.Http;

namespace Mgx.IntegrationTests;

/// <summary>
/// A second piped item against one -OutFile is refused BEFORE its content is fetched -
/// previously the whole body was downloaded and then thrown away with the refusal.
/// </summary>
[Collection("Pipeline")]
public class OutFileGuardTests
{
    [Fact]
    public void A_second_piped_item_is_refused_without_fetching_it()
    {
        var wire = new MockHttpHandler();
        wire.QueueBytes(HttpStatusCode.OK, [1, 2, 3, 4], "application/octet-stream");
        wire.QueueBytes(HttpStatusCode.OK, [5, 6, 7, 8], "application/octet-stream");

        // Get-MgxContent refuses a transport mgx does not own, so this one claims ownership.
        using var transport = MgxTransportScope.Inject(wire, owned: true);

        var outFile = Path.Combine(Path.GetTempPath(), $"mgx-outfile-{Guid.NewGuid():N}.bin");
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
            ps.AddScript($$"""
                @(
                    [Hashtable]@{ id = 'item1'; parentReference = @{ driveId = 'd1' } }
                    [Hashtable]@{ id = 'item2'; parentReference = @{ driveId = 'd1' } }
                ) | Get-MgxContent -OutFile '{{outFile}}'
                """);
            ps.Invoke();

            var error = Assert.Single(ps.Streams.Error);
            Assert.StartsWith("OutFileWithMultipleInputs", error.FullyQualifiedErrorId);

            // The first item was fetched and written; the second was refused pre-fetch.
            Assert.Single(wire.CapturedRequests);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(outFile));
        }
        finally
        {
            File.Delete(outFile);
        }
    }
}
