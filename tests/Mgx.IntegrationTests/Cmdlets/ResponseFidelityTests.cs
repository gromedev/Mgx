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
    public void A_bom_prefixed_json_body_parses()
    {
        var wire = new MockHttpHandler();
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var body = System.Text.Encoding.UTF8.GetBytes("""{"id":"u1","displayName":"BOM"}""");
        wire.QueueBytes(HttpStatusCode.OK, [.. bom, .. body], "application/json");
        InjectTransport(wire);
        try
        {
            using var ps = CreateShell();
            ps.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/users/u1");
            var output = ps.Invoke();

            Assert.Empty(ps.Streams.Error);
            var item = Assert.Single(output);
            Assert.Equal("BOM", ((System.Collections.Hashtable)item.BaseObject)["displayName"]);
        }
        finally { ResetTransport(); }
    }

    [Fact]
    public void A_utf16_body_is_decoded_by_its_declared_charset()
    {
        var wire = new MockHttpHandler();
        wire.QueueBytes(HttpStatusCode.OK,
            System.Text.Encoding.Unicode.GetBytes("plain utf-16 text"),
            "text/plain; charset=utf-16");
        InjectTransport(wire);
        try
        {
            using var ps = CreateShell();
            ps.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/users/u1").AddParameter("Raw", true);
            var output = ps.Invoke();

            var item = Assert.Single(output);
            Assert.Equal("plain utf-16 text", item.BaseObject);
        }
        finally { ResetTransport(); }
    }

    [Fact]
    public void A_malformed_export_page_is_an_error_record_not_a_crash()
    {
        var wire = new MockHttpHandler();
        wire.QueueResponse(HttpStatusCode.OK, "not a collection envelope");
        InjectTransport(wire);
        var outFile = Path.Combine(Path.GetTempPath(), $"mgx-export-{Guid.NewGuid():N}.jsonl");
        try
        {
            using var ps = CreateShell();
            ps.AddCommand("Export-MgxCollection")
              .AddParameter("Uri", "/users")
              .AddParameter("OutputFile", outFile);
            ps.Invoke();

            var error = Assert.Single(ps.Streams.Error);
            Assert.StartsWith("MalformedJsonResponse", error.FullyQualifiedErrorId);
        }
        finally
        {
            File.Delete(outFile);
            ResetTransport();
        }
    }

    [Fact]
    public void A_304_from_a_conditional_download_is_not_an_error()
    {
        var wire = new MockHttpHandler();
        wire.QueueEmpty((HttpStatusCode)304);
        InjectTransport(wire);
        Base.GetField("s_ownsHttpClient", Static)!.SetValue(null, true);
        Base.GetField("s_cachedTotalTimeoutSeconds", Static)!.SetValue(null,
            new ResilientGraphClientOptions().TotalTimeoutSeconds);
        try
        {
            using var ps = CreateShell();
            ps.AddScript("""
                Get-MgxContent '/me/drive/items/x/content' -Headers @{ 'If-None-Match' = '"etag"' }
                """);
            var output = ps.Invoke();

            Assert.Empty(output);
            Assert.Empty(ps.Streams.Error);
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
