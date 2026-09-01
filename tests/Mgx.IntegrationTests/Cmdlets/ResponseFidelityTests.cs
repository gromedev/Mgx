using System.Net;
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
        using (MgxTransportScope.Inject(wire))
        {
            using var ps = CreateShell();
            ps.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/users/u1");
            var output = ps.Invoke();

            Assert.Empty(output);
            Assert.Empty(ps.Streams.Error);
        }
    }

    [Fact]
    public void A_200_with_a_zero_length_body_emits_nothing_and_no_error()
    {
        var wire = new MockHttpHandler();
        wire.QueueEmpty(HttpStatusCode.OK);
        using (MgxTransportScope.Inject(wire))
        {
            using var ps = CreateShell();
            ps.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/users/u1");
            var output = ps.Invoke();

            Assert.Empty(output);
            Assert.Empty(ps.Streams.Error);
        }
    }

    [Fact]
    public void An_html_body_is_an_error_record_naming_the_content_type()
    {
        var wire = new MockHttpHandler();
        wire.QueueResponse(HttpStatusCode.OK, "<html>proxy says no</html>", contentType: "text/html");
        using (MgxTransportScope.Inject(wire))
        {
            using var ps = CreateShell();
            ps.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/users/u1");
            var output = ps.Invoke();

            Assert.Empty(output);
            var error = Assert.Single(ps.Streams.Error);
            Assert.StartsWith("NonJsonResponse", error.FullyQualifiedErrorId);
            Assert.Contains("text/html", error.Exception.Message);
        }
    }

    [Fact]
    public void Raw_passes_a_non_json_body_through_as_text()
    {
        var wire = new MockHttpHandler();
        wire.QueueResponse(HttpStatusCode.OK, "plain text payload", contentType: "text/plain");
        using (MgxTransportScope.Inject(wire))
        {
            using var ps = CreateShell();
            ps.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/users/u1").AddParameter("Raw", true);
            var output = ps.Invoke();

            var item = Assert.Single(output);
            Assert.Equal("plain text payload", item.BaseObject);
            Assert.Empty(ps.Streams.Error);
        }
    }

    [Fact]
    public void Malformed_json_is_an_error_record_with_a_body_snippet()
    {
        var wire = new MockHttpHandler();
        wire.QueueResponse(HttpStatusCode.OK, "{\"id\": \"u1\", \"displayNa");
        using (MgxTransportScope.Inject(wire))
        {
            using var ps = CreateShell();
            ps.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/users/u1");
            var output = ps.Invoke();

            Assert.Empty(output);
            var error = Assert.Single(ps.Streams.Error);
            Assert.StartsWith("MalformedJsonResponse", error.FullyQualifiedErrorId);
            Assert.Contains("displayNa", error.Exception.Message);
        }
    }

    [Fact]
    public void A_malformed_page_during_enumeration_is_an_error_record_not_a_crash()
    {
        var wire = new MockHttpHandler();
        wire.QueueResponse(HttpStatusCode.OK, "not a collection envelope at all");
        using (MgxTransportScope.Inject(wire))
        {
            using var ps = CreateShell();
            ps.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/users").AddParameter("All", true);
            ps.Invoke();

            var error = Assert.Single(ps.Streams.Error);
            Assert.StartsWith("MalformedJsonResponse", error.FullyQualifiedErrorId);
        }
    }

    [Fact]
    public void A_bom_prefixed_json_body_parses()
    {
        var wire = new MockHttpHandler();
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var body = System.Text.Encoding.UTF8.GetBytes("""{"id":"u1","displayName":"BOM"}""");
        wire.QueueBytes(HttpStatusCode.OK, [.. bom, .. body], "application/json");
        using (MgxTransportScope.Inject(wire))
        {
            using var ps = CreateShell();
            ps.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/users/u1");
            var output = ps.Invoke();

            Assert.Empty(ps.Streams.Error);
            var item = Assert.Single(output);
            Assert.Equal("BOM", ((System.Collections.Hashtable)item.BaseObject)["displayName"]);
        }
    }

    /// <summary>
    /// The fan-out read is the same read. It deserialized the raw bytes, so the BOM that the
    /// single-entity path accepts lost every entity to a per-ID FanOutError, and the declared
    /// charset, the empty body and -Raw were decided differently there than everywhere else.
    /// </summary>
    [Fact]
    public void A_bom_prefixed_body_parses_on_the_entity_fan_out_too()
    {
        var wire = new MockHttpHandler();
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var body = System.Text.Encoding.UTF8.GetBytes("""{"id":"u1","displayName":"BOM"}""");
        // Both IDs are answered the same way: the two requests race, so the queue order
        // cannot say which entity gets which response.
        wire.QueueBytes(HttpStatusCode.OK, [.. bom, .. body], "application/json");
        wire.QueueBytes(HttpStatusCode.OK, [.. bom, .. body], "application/json");
        using (MgxTransportScope.Inject(wire))
        {
            using var ps = CreateShell();
            ps.AddScript("'u1','u2' | Invoke-MgxRequest -Uri '/users/{id}'");
            var output = ps.Invoke();

            Assert.Empty(ps.Streams.Error);
            Assert.Equal(2, output.Count);
            Assert.All(output, item =>
                Assert.Equal("BOM", ((System.Collections.Hashtable)item.BaseObject)["displayName"]));
        }
    }

    [Fact]
    public void An_entity_fan_out_body_is_decoded_by_its_declared_charset()
    {
        var wire = new MockHttpHandler();
        var body = System.Text.Encoding.Unicode.GetBytes("""{"id":"u1","displayName":"charset"}""");
        wire.QueueBytes(HttpStatusCode.OK, body, "application/json; charset=utf-16");
        wire.QueueBytes(HttpStatusCode.OK, body, "application/json; charset=utf-16");
        using (MgxTransportScope.Inject(wire))
        {
            using var ps = CreateShell();
            ps.AddScript("'u1','u2' | Invoke-MgxRequest -Uri '/users/{id}'");
            var output = ps.Invoke();

            Assert.Empty(ps.Streams.Error);
            Assert.Equal(2, output.Count);
            Assert.All(output, item =>
                Assert.Equal("charset", ((System.Collections.Hashtable)item.BaseObject)["displayName"]));
        }
    }

    [Fact]
    public void Raw_passes_a_non_json_fan_out_body_through_as_text()
    {
        var wire = new MockHttpHandler();
        wire.QueueResponse(HttpStatusCode.OK, "plain text payload", contentType: "text/plain");
        wire.QueueResponse(HttpStatusCode.OK, "plain text payload", contentType: "text/plain");
        using (MgxTransportScope.Inject(wire))
        {
            using var ps = CreateShell();
            ps.AddScript("'u1','u2' | Invoke-MgxRequest -Uri '/users/{id}' -Raw");
            var output = ps.Invoke();

            Assert.Empty(ps.Streams.Error);
            Assert.Equal(2, output.Count);
            Assert.All(output, item => Assert.Equal("plain text payload", item.BaseObject));
        }
    }

    /// <summary>
    /// The relation fan-out reads a body the same way, and deserialized the raw bytes: a
    /// byte-order mark lost the relation to a per-URL error and left the object carrying null.
    /// </summary>
    [Fact]
    public void A_bom_prefixed_relation_body_is_attached()
    {
        var wire = new MockHttpHandler();
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var body = System.Text.Encoding.UTF8.GetBytes("""{"id":"m1","displayName":"BOM"}""");
        wire.QueueBytes(HttpStatusCode.OK, [.. bom, .. body], "application/json");
        using (MgxTransportScope.Inject(wire))
        {
            using var ps = CreateShell();
            ps.AddScript(
                "[pscustomobject]@{ id = 'u1' } | Expand-MgxRelation -Uri '/users/{id}/manager' -As manager -Flatten");
            var output = ps.Invoke();

            Assert.Empty(ps.Streams.Error);
            var item = Assert.Single(output);
            var manager = Assert.IsType<System.Collections.Hashtable>(
                item.Properties["manager"].Value);
            Assert.Equal("BOM", manager["displayName"]);
        }
    }

    /// <summary>
    /// A relation is not always an entity. Graph answers /$count with the number in the body
    /// and text/plain on it, in both the bare and the charset-carrying form, and the count is
    /// what the caller asked for - Expand-MgxRelation has no -Raw to receive it any other way.
    /// </summary>
    [Theory]
    [InlineData("text/plain")]
    [InlineData("text/plain; charset=utf-8")]
    [InlineData("application/json")]
    public void A_count_relation_attaches_the_count_whatever_type_it_declares(string contentType)
    {
        var wire = new MockHttpHandler();
        wire.QueueBytes(HttpStatusCode.OK, System.Text.Encoding.UTF8.GetBytes("5"), contentType);
        using (MgxTransportScope.Inject(wire))
        {
            using var ps = CreateShell();
            ps.AddScript(
                "[pscustomobject]@{ id = 'g1' } | Expand-MgxRelation -Uri '/groups/{id}/members/$count'"
                + " -As memberCount -Flatten -ConsistencyLevel eventual");
            var output = ps.Invoke();

            Assert.Empty(ps.Streams.Error);
            var item = Assert.Single(output);
            var count = Assert.IsType<System.Collections.Hashtable>(
                item.Properties["memberCount"].Value);
            Assert.Equal(5L, count["Value"]);
        }
    }

    /// <summary>The ordinary relation, read as it always was.</summary>
    [Fact]
    public void A_json_relation_body_is_attached_as_the_entity()
    {
        var wire = new MockHttpHandler();
        wire.QueueResponse(HttpStatusCode.OK, """{"id":"m1","displayName":"Ann"}""");
        using (MgxTransportScope.Inject(wire))
        {
            using var ps = CreateShell();
            ps.AddScript(
                "[pscustomobject]@{ id = 'u1' } | Expand-MgxRelation -Uri '/users/{id}/manager' -As manager -Flatten");
            var output = ps.Invoke();

            Assert.Empty(ps.Streams.Error);
            var item = Assert.Single(output);
            var manager = Assert.IsType<System.Collections.Hashtable>(
                item.Properties["manager"].Value);
            Assert.Equal("m1", manager["id"]);
            Assert.Equal("Ann", manager["displayName"]);
        }
    }

    /// <summary>
    /// The other half of reading a body by what it carries: a proxy's HTML page is no relation
    /// in any encoding, and stays a per-URL error naming what came back instead.
    /// </summary>
    [Fact]
    public void An_html_relation_body_is_an_error_naming_the_content_type()
    {
        var wire = new MockHttpHandler();
        wire.QueueResponse(HttpStatusCode.OK, "<html>proxy says no</html>", contentType: "text/html");
        using (MgxTransportScope.Inject(wire))
        {
            using var ps = CreateShell();
            ps.AddScript(
                "[pscustomobject]@{ id = 'u1' } | Expand-MgxRelation -Uri '/users/{id}/manager' -As manager -Flatten");
            var output = ps.Invoke();

            var error = Assert.Single(ps.Streams.Error);
            Assert.StartsWith("ExpandRelationError", error.FullyQualifiedErrorId, StringComparison.Ordinal);
            Assert.Contains("text/html", error.Exception.Message, StringComparison.Ordinal);
            var item = Assert.Single(output);
            Assert.Null(item.Properties["manager"].Value);
        }
    }

    [Fact]
    public void A_utf16_body_is_decoded_by_its_declared_charset()
    {
        var wire = new MockHttpHandler();
        wire.QueueBytes(HttpStatusCode.OK,
            System.Text.Encoding.Unicode.GetBytes("plain utf-16 text"),
            "text/plain; charset=utf-16");
        using (MgxTransportScope.Inject(wire))
        {
            using var ps = CreateShell();
            ps.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/users/u1").AddParameter("Raw", true);
            var output = ps.Invoke();

            var item = Assert.Single(output);
            Assert.Equal("plain utf-16 text", item.BaseObject);
        }
    }

    /// <summary>
    /// A charset .NET knows and refuses to construct. Encoding.GetEncoding answers
    /// NotSupportedException for utf-7 rather than the ArgumentException every unknown name
    /// raises, so the fallback to UTF-8 was one exception type short and a response header
    /// ended the pipeline with a .NET deprecation message.
    /// </summary>
    [Theory]
    [InlineData("application/json; charset=utf-7")]
    [InlineData("application/json; charset=\"UTF-7\"")]
    [InlineData("application/json; charset=windows-1252")]
    public void A_charset_dotnet_will_not_construct_falls_back_to_utf8(string contentType)
    {
        var wire = new MockHttpHandler();
        wire.QueueBytes(HttpStatusCode.OK,
            System.Text.Encoding.UTF8.GetBytes("{\"displayName\":\"charset\"}"),
            contentType);
        using (MgxTransportScope.Inject(wire))
        {
            using var ps = CreateShell();
            ps.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/users/u1");
            var output = ps.Invoke();

            Assert.Empty(ps.Streams.Error);
            var item = Assert.Single(output);
            Assert.Equal("charset", ((System.Collections.Hashtable)item.BaseObject)["displayName"]);
        }
    }

    /// <summary>Same header on the write path, which reads the body the same way.</summary>
    [Fact]
    public void A_charset_dotnet_will_not_construct_does_not_end_a_write()
    {
        var wire = new MockHttpHandler();
        wire.QueueBytes(HttpStatusCode.OK,
            System.Text.Encoding.UTF8.GetBytes("{\"displayName\":\"charset\"}"),
            "application/json; charset=utf-7");
        using (MgxTransportScope.Inject(wire))
        {
            using var ps = CreateShell();
            ps.AddCommand("Invoke-MgxRequest")
              .AddParameter("Uri", "/users/u1")
              .AddParameter("Method", "PATCH")
              .AddParameter("Body", "{}")
              .AddParameter("Confirm", false);
            var output = ps.Invoke();

            Assert.Empty(ps.Streams.Error);
            var item = Assert.Single(output);
            Assert.Equal("charset", ((System.Collections.Hashtable)item.BaseObject)["displayName"]);
        }
    }

    [Fact]
    public void A_malformed_export_page_is_an_error_record_not_a_crash()
    {
        var wire = new MockHttpHandler();
        wire.QueueResponse(HttpStatusCode.OK, "not a collection envelope");
        using var transport = MgxTransportScope.Inject(wire);
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
        }
    }

    [Fact]
    public void A_304_from_a_conditional_download_is_not_an_error()
    {
        var wire = new MockHttpHandler();
        wire.QueueEmpty((HttpStatusCode)304);
        // Get-MgxContent refuses a transport mgx does not own, so this one claims ownership.
        using (MgxTransportScope.Inject(wire, owned: true))
        {
            using var ps = CreateShell();
            ps.AddScript("""
                Get-MgxContent '/me/drive/items/x/content' -Headers @{ 'If-None-Match' = '"etag"' }
                """);
            var output = ps.Invoke();

            Assert.Empty(output);
            Assert.Empty(ps.Streams.Error);
        }
    }

    [Fact]
    public void A_write_answered_with_html_is_an_error_record()
    {
        var wire = new MockHttpHandler();
        wire.QueueResponse(HttpStatusCode.OK, "<html>gateway</html>", contentType: "text/html");
        using (MgxTransportScope.Inject(wire))
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
    }
}
