using System.Management.Automation;
using System.Net;
using System.Reflection;
using System.Text;
using Mgx.Cmdlets.Base;
using Mgx.Engine.Http;

namespace Mgx.IntegrationTests;

/// <summary>
/// -Top is documented as capping the total result set. -All follows every nextLink. Together
/// they meant -Top was discarded: the run walked the whole collection and handed back all of
/// it. Silent, and expensive twice over, because -Top also sets the page size - asking a
/// 100,000-object tenant for 150 items paged it 150 at a time to return all 100,000.
/// </summary>
[Collection("Pipeline")]
public class TopWithAllTests
{
    /// <summary>
    /// Pages of <paramref name="pageSize"/> items carrying a nextLink, up to a hard ceiling far
    /// above what any test asks for. The ceiling exists only so an uncapped run ends: without
    /// the fix -All ignores -Top and keeps asking, and a test that hangs reports nothing.
    /// </summary>
    private sealed class PagedHandler(int pageSize, int maxPages) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        private int _next;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var items = string.Join(",",
                Enumerable.Range(_next, pageSize).Select(i => $"{{\"id\":\"u{i}\"}}"));
            _next += pageSize;
            var link = RequestCount >= maxPages
                ? ""
                : $",\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/users?$skiptoken={_next}\"";
            var body = $"{{\"value\":[{items}]{link}}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private static void InjectMock(HttpMessageHandler handler)
    {
        ResiliencePipelineFactory.Reset();
        var t = typeof(MgxCmdletBase);
        t.GetField("s_graphHttpClient", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, new HttpClient(handler));
        t.GetField("s_cachedAuthFingerprint", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, MgxCmdletBase.BuildAuthFingerprint(
                new { TenantId = "test-tenant-00000000-0000-0000-0000-000000000000" }, null));
        t.GetField("s_ownsHttpClient", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, false);
        t.GetField("s_graphEndpoint", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public)!
            .SetValue(null, "https://graph.microsoft.com");
        t.GetField("s_clientOptions", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public)!
            .SetValue(null, new ResilientGraphClientOptions { NoRateLimit = true, MaxRetryAttempts = 1 });
        ResiliencePipelineFactory.Reset();
    }

    private static void CleanupMock()
    {
        var t = typeof(MgxCmdletBase);
        t.GetField("s_graphHttpClient", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, null);
        t.GetField("s_cachedAuthFingerprint", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, null);
        t.GetField("s_cachedAuthContextRef", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, null);
        ResiliencePipelineFactory.Reset();
    }

    private static (int Count, int Requests) Enumerate(int top, bool all, int pageSize)
    {
        var handler = new PagedHandler(pageSize, maxPages: 40);
        InjectMock(handler);
        try
        {
            using var ps = PowerShell.Create();
            ps.AddCommand("Import-Module")
              .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection).Assembly);
            ps.Invoke();
            ps.Commands.Clear();
            ps.AddScript("function Get-MgContext { [PSCustomObject]@{ TenantId = 'test-tenant-00000000-0000-0000-0000-000000000000' } }");
            ps.Invoke();
            ps.Commands.Clear();

            var cmd = ps.AddCommand("Invoke-MgxRequest")
                        .AddParameter("Uri", "/users")
                        .AddParameter("Top", top)
                        .AddParameter("PageSize", pageSize)
                        .AddParameter("WarningAction", ActionPreference.SilentlyContinue);
            if (all) cmd.AddParameter("All");
            var output = ps.Invoke();
            return (output.Count, handler.RequestCount);
        }
        finally { CleanupMock(); }
    }

    [Fact]
    public void All_stops_at_Top_rather_than_walking_the_whole_collection()
    {
        // Forty pages of 50 are available; the caller asked for 150. Anything above 150 means
        // the cap was discarded and the run kept walking.
        var (count, _) = Enumerate(top: 150, all: true, pageSize: 50);

        Assert.Equal(150, count);
    }

    [Fact]
    public void All_with_Top_stops_requesting_once_it_has_enough()
    {
        // Not just "returns the right number": the cost is the point. Three pages of 50 covers
        // 150, and a fourth request would be work done for output the caller never sees.
        var (_, requests) = Enumerate(top: 150, all: true, pageSize: 50);

        Assert.Equal(3, requests);
    }

    [Fact]
    public void Top_alone_still_caps_the_total()
    {
        var (count, _) = Enumerate(top: 75, all: false, pageSize: 50);

        Assert.Equal(75, count);
    }
}
