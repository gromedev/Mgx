using System.Management.Automation;
using System.Net;
using System.Reflection;
using System.Text;
using Mgx.Cmdlets.Base;
using Mgx.Engine.Http;

namespace Mgx.IntegrationTests;

/// <summary>
/// The beta hint exists for one case: an endpoint that 404s on v1.0 because it only exists in
/// beta. Graph also 404s for a missing OBJECT on a perfectly good endpoint, and for a missing
/// drive item the hint pointed at beta over a request that fails there identically. The codes
/// overlap - Request_ResourceNotFound is returned both for a missing directory object and for a
/// beta-only segment, measured live - so only itemNotFound, whose meaning is unambiguous, is
/// suppressed.
/// </summary>
[Collection("Pipeline")]
public class BetaHintTests
{
    private sealed class SingleResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(status)
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
        // Get-MgxContent refuses to run over a transport mgx does not own (the SDK client
        // auto-follows redirects, defeating download-host validation), so the mock must claim
        // ownership or the cmdlet errors before any HTTP happens and the test is vacuous.
        t.GetField("s_ownsHttpClient", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, true);
        t.GetField("s_graphEndpoint", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public)!
            .SetValue(null, "https://graph.microsoft.com");
        var options = new ResilientGraphClientOptions { NoRateLimit = true, MaxRetryAttempts = 1 };
        t.GetField("s_clientOptions", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public)!
            .SetValue(null, options);
        // Owning the client makes GetClient() check the cached timeout; a mismatch forces a
        // rebuild that cannot succeed under a mock. Cache the value the options carry.
        t.GetField("s_cachedTotalTimeoutSeconds", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, options.TotalTimeoutSeconds);
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

    private static (List<string> Warnings, List<string> Errors) Run(
        string cmdlet, string uri, HttpStatusCode status, string body)
    {
        var handler = new SingleResponseHandler(status, body);
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

            ps.AddCommand(cmdlet)
              .AddParameter("Uri", uri)
              .AddParameter("ErrorAction", ActionPreference.Continue);
            string? invokeEx = null;
            try { ps.Invoke(); }
            catch (Exception ex) { invokeEx = $"{ex.GetType().Name}: {ex.Message}"; }

            var warnings = ps.Streams.Warning.Select(w => w.Message).ToList();
            var errors = ps.Streams.Error.Select(e => e.ToString()).ToList();
            if (invokeEx != null) errors.Add(invokeEx);
            if (errors.Count == 0 && warnings.Count == 0)
                errors.Add($"(silent; {handler.Requests.Count} HTTP requests: {string.Join(", ", handler.Requests)})");
            return (warnings, errors);
        }
        finally { CleanupMock(); }
    }

    private const string ItemNotFound =
        """{"error":{"code":"itemNotFound","message":"The resource could not be found."}}""";
    private const string ResourceNotFound =
        """{"error":{"code":"Request_ResourceNotFound","message":"Resource 'profile' does not exist."}}""";

    [Fact]
    public void A_missing_drive_item_does_not_suggest_beta()
    {
        var (warnings, errors) = Run(
            "Get-MgxContent", "/drives/b!x/items/01X/content",
            HttpStatusCode.NotFound, ItemNotFound);

        Assert.DoesNotContain(warnings, w => w.Contains("beta"));
        Assert.Contains(errors, e => e.Contains("itemNotFound"));
    }

    [Fact]
    public void An_ambiguous_404_still_suggests_beta()
    {
        // Request_ResourceNotFound cannot be told apart from a beta-only segment, so the
        // hedged hint stays. This pins the suppression to codes with known semantics: widening
        // it to this code would kill the hint's one legitimate case.
        var (warnings, _) = Run(
            "Invoke-MgxRequest", "/users/00000000-0000-0000-0000-000000000000/profile",
            HttpStatusCode.NotFound, ResourceNotFound);

        Assert.Contains(warnings, w => w.Contains("beta"));
    }
}
