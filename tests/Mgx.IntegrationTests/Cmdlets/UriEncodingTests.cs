using System.Net;
using System.Reflection;
using Mgx.Engine.Http;

namespace Mgx.IntegrationTests;

/// <summary>
/// What the caller wrote is what goes on the wire: a '#' in a path stays in the path
/// instead of vanishing as a URI fragment, hostile filter values are escaped once, and
/// pre-encoded input is not encoded twice. (Corpus: GraphSDK-1947, GraphSDK-2709/2942,
/// M365DSC-5354.)
/// </summary>
[Collection("Pipeline")]
public class UriEncodingTests
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

    private static string WireUriFor(MockHttpHandler wire, Action<System.Management.Automation.PowerShell> configure)
    {
        using var ps = System.Management.Automation.PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.InvokeMgxRequest).Assembly);
        ps.Invoke();
        ps.Commands.Clear();
        ps.AddScript("function Get-MgContext { [PSCustomObject]@{ TenantId = 'test-tenant-00000000-0000-0000-0000-000000000000' } }");
        ps.Invoke();
        ps.Commands.Clear();
        configure(ps);
        ps.Invoke();
        var captured = wire.CapturedRequests;
        Assert.NotEmpty(captured);
        return captured[0].Uri;
    }

    [Fact]
    public void A_hash_in_a_path_reaches_the_wire_instead_of_becoming_a_fragment()
    {
        var wire = new MockHttpHandler();
        wire.SetDefaultResponse(HttpStatusCode.OK, TestData.SingleUser);
        InjectTransport(wire);
        try
        {
            var uri = WireUriFor(wire, ps => ps.AddCommand("Invoke-MgxRequest")
                .AddParameter("Uri", "/me/drive/root:/reports/a#b.txt"));

            Assert.Contains("/reports/a%23b.txt", uri);
            Assert.DoesNotContain("#", uri);
        }
        finally { ResetTransport(); }
    }

    [Fact]
    public void A_raw_filter_is_escaped_once()
    {
        var wire = new MockHttpHandler();
        wire.SetDefaultResponse(HttpStatusCode.OK, TestData.EmptyCollection);
        InjectTransport(wire);
        try
        {
            var uri = WireUriFor(wire, ps => ps.AddCommand("Invoke-MgxRequest")
                .AddParameter("Uri", "/users")
                .AddParameter("Filter", "displayName eq 'O''Brien & Söhne'"));

            Assert.Contains("$filter=displayName%20eq%20%27O%27%27Brien%20%26%20S%C3%B6hne%27", uri);
        }
        finally { ResetTransport(); }
    }

    [Fact]
    public void A_pre_encoded_filter_is_not_encoded_twice()
    {
        var wire = new MockHttpHandler();
        wire.SetDefaultResponse(HttpStatusCode.OK, TestData.EmptyCollection);
        InjectTransport(wire);
        try
        {
            var uri = WireUriFor(wire, ps => ps.AddCommand("Invoke-MgxRequest")
                .AddParameter("Uri", "/users")
                .AddParameter("Filter", "displayName%20eq%20%27Bob%27"));

            Assert.Contains("$filter=displayName%20eq%20%27Bob%27", uri);
            Assert.DoesNotContain("%25", uri);
        }
        finally { ResetTransport(); }
    }

    [Fact]
    public void A_literal_percent_that_is_not_a_triplet_is_escaped()
    {
        var wire = new MockHttpHandler();
        wire.SetDefaultResponse(HttpStatusCode.OK, TestData.EmptyCollection);
        InjectTransport(wire);
        try
        {
            var uri = WireUriFor(wire, ps => ps.AddCommand("Invoke-MgxRequest")
                .AddParameter("Uri", "/users")
                .AddParameter("Filter", "displayName eq '50% off'"));

            Assert.Contains("50%25%20off", uri);
        }
        finally { ResetTransport(); }
    }

    [Fact]
    public void A_piped_drive_item_id_is_escaped_into_the_content_path()
    {
        // Drive item ids can carry '!' and ':'; SharePoint site ids carry ','. The path is
        // built from pipeline data, so the injection point escapes rather than trusts.
        var wire = new MockHttpHandler();
        wire.QueueBytes(HttpStatusCode.OK, [1, 2, 3], "application/octet-stream");
        InjectTransport(wire);
        // Get-MgxContent refuses borrowed transports (the SDK's RedirectHandler would
        // auto-follow the content 302); this injected client is mgx-owned. An owned client
        // is rebuilt when the cached timeout disagrees with options, so keep them equal.
        Base.GetField("s_ownsHttpClient", Static)!.SetValue(null, true);
        Base.GetField("s_cachedTotalTimeoutSeconds", Static)!.SetValue(null,
            new ResilientGraphClientOptions().TotalTimeoutSeconds);
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
            ps.AddScript("""
                [Hashtable]@{
                    id = '01ABC!DEF:XYZ'
                    parentReference = @{ driveId = 'b!x_y' }
                } | Get-MgxContent
                """);
            ps.Invoke();

            var errText = string.Join(" | ", ps.Streams.Error.Select(e => e.FullyQualifiedErrorId + ": " + e.Exception.Message));
            var captured = wire.CapturedRequests;
            Assert.True(captured.Count > 0, $"no request sent; errors: {errText}");
            Assert.Contains("/drives/b%21x_y/items/01ABC%21DEF%3AXYZ/content", captured[0].Uri);
        }
        finally { ResetTransport(); }
    }

    [Fact]
    public void Every_hostile_filter_round_trips_through_encoding()
    {
        // The corpus generalizes GraphSDK-2709/2942: the wire value must decode back to
        // exactly what the caller wrote, whatever it contains.
        foreach (var filter in HostileInputs.FilterValues)
        {
            var wire = new MockHttpHandler();
            wire.SetDefaultResponse(HttpStatusCode.OK, TestData.EmptyCollection);
            InjectTransport(wire);
            try
            {
                var uri = WireUriFor(wire, ps => ps.AddCommand("Invoke-MgxRequest")
                    .AddParameter("Uri", "/users")
                    .AddParameter("Filter", filter));
                // OriginalString keeps a raw '#' that the transport would then drop as a
                // fragment - so round-tripping alone is not proof. No raw '#', '&' or '+'
                // may survive in the encoded value; then the decode must give back the input.
                var query = uri[(uri.IndexOf("$filter=", StringComparison.Ordinal) + "$filter=".Length)..];
                query = query.Split('&')[0];
                Assert.DoesNotContain('#', query);
                Assert.DoesNotContain('+', query);
                Assert.Equal(filter, System.Uri.UnescapeDataString(query));
            }
            finally { ResetTransport(); }
        }
    }

    [Fact]
    public void A_top_already_in_the_uri_is_not_sent_twice()
    {
        var wire = new MockHttpHandler();
        wire.SetDefaultResponse(HttpStatusCode.OK, TestData.EmptyCollection);
        InjectTransport(wire);
        try
        {
            var uri = WireUriFor(wire, ps => ps.AddCommand("Invoke-MgxRequest")
                .AddParameter("Uri", "/users?$top=5")
                .AddParameter("All", true));

            Assert.Single(System.Text.RegularExpressions.Regex.Matches(uri, "top="));
            Assert.Contains("$top=5", uri);
        }
        finally { ResetTransport(); }
    }

    [Fact]
    public void A_filter_already_in_the_uri_wins_over_the_parameter_with_a_warning()
    {
        var wire = new MockHttpHandler();
        wire.SetDefaultResponse(HttpStatusCode.OK, TestData.EmptyCollection);
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
            ps.AddCommand("Invoke-MgxRequest")
                .AddParameter("Uri", "/users?$filter=accountEnabled eq true")
                .AddParameter("Filter", "displayName eq 'x'");
            ps.Invoke();

            var uri = wire.CapturedRequests[0].Uri;
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(uri, "filter="));
            Assert.Contains("accountEnabled", uri);
            Assert.DoesNotContain("displayName", uri);
            // Deferring must be loud: the caller's -Filter was not sent.
            Assert.Contains(ps.Streams.Warning, w => w.Message.Contains("-Filter"));
        }
        finally { ResetTransport(); }
    }

    [Fact]
    public void An_encoded_option_name_in_the_uri_still_defers_the_parameter()
    {
        var wire = new MockHttpHandler();
        wire.SetDefaultResponse(HttpStatusCode.OK, TestData.EmptyCollection);
        InjectTransport(wire);
        try
        {
            var uri = WireUriFor(wire, ps => ps.AddCommand("Invoke-MgxRequest")
                .AddParameter("Uri", "/users?%24top=10")
                .AddParameter("All", true));

            // %24top IS $top to the server; a second $top would draw a 400.
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(uri, "top="));
        }
        finally { ResetTransport(); }
    }

    [Fact]
    public void A_caller_count_in_the_uri_does_not_arm_the_count_degradation_retry()
    {
        // Endpoint 400s; if mgx thinks its auto-$count caused it, the "retry without
        // count" rebuild is byte-identical and burns a duplicate request.
        var wire = new MockHttpHandler();
        wire.SetDefaultResponse(HttpStatusCode.BadRequest,
            """{ "error": { "code": "BadRequest", "message": "no" } }""");
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
            ps.AddScript("Invoke-MgxRequest '/users?$count=true' -Filter \"displayName eq 'x'\" -ErrorAction SilentlyContinue");
            ps.Invoke();

            // One request (plus zero byte-identical degradation retries): the URI's own
            // $count was never mgx's to drop.
            Assert.Single(wire.CapturedRequests);
        }
        finally { ResetTransport(); }
    }

    [Fact]
    public void Every_hostile_path_segment_survives_id_templating()
    {
        // The PathSegments corpus, driven through the one path-injection mechanism a
        // caller has: {id} templating. The captured request path must carry the fully
        // escaped segment - no raw '#', '%', space, or comma damage.
        foreach (var segment in HostileInputs.PathSegments)
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
                ps.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/items/{id}");
                ps.Invoke(new[] { segment });

                var sent = Assert.Single(wire.CapturedRequests);
                Assert.Contains($"/items/{System.Uri.EscapeDataString(segment)}", sent.Uri);
                Assert.DoesNotContain('#', sent.Uri);
            }
            finally { ResetTransport(); }
        }
    }

    /// <summary>
    /// An absolute -Uri concatenated onto the versioned base URL would silently produce
    /// /v1.0/https:/... on the wire. Get-MgxContent and Sync-MgxDelta already refuse it;
    /// the other three URI-taking cmdlets refuse it the same way.
    /// </summary>
    [Theory]
    [InlineData("Invoke-MgxRequest -Uri ' https://graph.microsoft.com/v1.0/users'")]
    [InlineData("Invoke-MgxRequest -Uri 'https://graph.microsoft.com/v1.0/users'")]
    [InlineData("[PSCustomObject]@{ id = 'g1' } | Expand-MgxRelation -Uri 'https://graph.microsoft.com/v1.0/groups/{id}/members' -As members")]
    [InlineData("Export-MgxCollection -Uri 'https://graph.microsoft.com/v1.0/users' -OutputFile ([IO.Path]::GetTempFileName())")]
    public void An_absolute_uri_is_refused_not_mangled(string command)
    {
        using var ps = System.Management.Automation.PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.InvokeMgxRequest).Assembly);
        ps.Invoke();
        ps.Commands.Clear();
        ps.AddScript(command);
        var output = ps.Invoke();

        Assert.Empty(output);
        var error = Assert.Single(ps.Streams.Error);
        Assert.StartsWith("AbsoluteUriNotAllowed", error.FullyQualifiedErrorId);
        Assert.Contains("must be a relative path", error.Exception.Message);
    }
}
