using System.Net;
using System.Reflection;
using Mgx.Engine.Http;

namespace Mgx.IntegrationTests;

/// <summary>
/// Responses that are not the JSON entity the caller expected - empty bodies, HTML error
/// pages from a proxy, truncated JSON - surface as verbose output or error records, never
/// as an unhandled exception. (Corpus: GraphSDK-1425/2088, non-JSON responses.)
/// </summary>
[Collection("Pipeline")]
public class ResponseFidelityTests
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
        Base.GetField("s_graphHttpClient", Static)!.SetValue(null, null);
        Base.GetField("s_cachedAuthFingerprint", Static)!.SetValue(null, null);
        ResiliencePipelineFactory.Reset();
    }

    private static System.Management.Automation.PowerShell CreateShell()
    {
        var ps = System.Management.Automation.PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.InvokeMgxRequest).Assembly);
        ps.Invoke();
        ps.Commands.Clear();
        ps.AddScript("function Get-MgContext { [PSCustomObject]@{ TenantId = 'test-tenant-00000000-0000-0000-0000-000000000000' } }");
        ps.Invoke();
        ps.Commands.Clear();
        return ps;
    }

    [Fact]
    public void A_204_on_a_get_emits_nothing_and_no_error()
    {
        var wire = new MockHttpHandler();
        wire.QueueEmpty(HttpStatusCode.NoContent);
        InjectTransport(wire);
        try
        {
            using var ps = CreateShell();
            ps.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/users/u1");
            var output = ps.Invoke();

            Assert.Empty(output);
            Assert.Empty(ps.Streams.Error);
        }
        finally { ResetTransport(); }
    }

    [Fact]
    public void A_200_with_a_zero_length_body_emits_nothing_and_no_error()
    {
        var wire = new MockHttpHandler();
        wire.QueueEmpty(HttpStatusCode.OK);
        InjectTransport(wire);
        try
        {
            using var ps = CreateShell();
            ps.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/users/u1");
            var output = ps.Invoke();

            Assert.Empty(output);
            Assert.Empty(ps.Streams.Error);
        }
        finally { ResetTransport(); }
    }

    [Fact]
    public void An_html_body_is_an_error_record_naming_the_content_type()
    {
        var wire = new MockHttpHandler();
        wire.QueueResponse(HttpStatusCode.OK, "<html>proxy says no</html>", contentType: "text/html");
        InjectTransport(wire);
        try
        {
            using var ps = CreateShell();
            ps.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/users/u1");
            var output = ps.Invoke();

            Assert.Empty(output);
            var error = Assert.Single(ps.Streams.Error);
            Assert.StartsWith("NonJsonResponse", error.FullyQualifiedErrorId);
            Assert.Contains("text/html", error.Exception.Message);
        }
        finally { ResetTransport(); }
    }

    [Fact]
    public void Raw_passes_a_non_json_body_through_as_text()
    {
        var wire = new MockHttpHandler();
        wire.QueueResponse(HttpStatusCode.OK, "plain text payload", contentType: "text/plain");
        InjectTransport(wire);
        try
        {
            using var ps = CreateShell();
            ps.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/users/u1").AddParameter("Raw", true);
            var output = ps.Invoke();

            var item = Assert.Single(output);
            Assert.Equal("plain text payload", item.BaseObject);
            Assert.Empty(ps.Streams.Error);
        }
        finally { ResetTransport(); }
    }

    [Fact]
    public void Malformed_json_is_an_error_record_with_a_body_snippet()
    {
        var wire = new MockHttpHandler();
        wire.QueueResponse(HttpStatusCode.OK, "{\"id\": \"u1\", \"displayNa");
        InjectTransport(wire);
        try
        {
            using var ps = CreateShell();
            ps.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/users/u1");
            var output = ps.Invoke();

            Assert.Empty(output);
            var error = Assert.Single(ps.Streams.Error);
            Assert.StartsWith("MalformedJsonResponse", error.FullyQualifiedErrorId);
            Assert.Contains("displayNa", error.Exception.Message);
        }
        finally { ResetTransport(); }
    }

    [Fact]
    public void A_malformed_page_during_enumeration_is_an_error_record_not_a_crash()
    {
        var wire = new MockHttpHandler();
        wire.QueueResponse(HttpStatusCode.OK, "not a collection envelope at all");
        InjectTransport(wire);
        try
        {
            using var ps = CreateShell();
            ps.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/users").AddParameter("All", true);
            ps.Invoke();

            var error = Assert.Single(ps.Streams.Error);
            Assert.StartsWith("MalformedJsonResponse", error.FullyQualifiedErrorId);
        }
        finally { ResetTransport(); }
    }

    [Fact]
    public void A_write_answered_with_html_is_an_error_record()
    {
        var wire = new MockHttpHandler();
        wire.QueueResponse(HttpStatusCode.OK, "<html>gateway</html>", contentType: "text/html");
        InjectTransport(wire);
        try
        {
            using var ps = CreateShell();
            ps.AddCommand("Invoke-MgxRequest")
              .AddParameter("Uri", "/users")
              .AddParameter("Method", "POST")
              .AddParameter("Body", new System.Collections.Hashtable { ["displayName"] = "x" })
              .AddParameter("Confirm", false);
            var output = ps.Invoke();

            Assert.Empty(output);
            var error = Assert.Single(ps.Streams.Error);
            Assert.StartsWith("NonJsonResponse", error.FullyQualifiedErrorId);
        }
        finally { ResetTransport(); }
    }
}
