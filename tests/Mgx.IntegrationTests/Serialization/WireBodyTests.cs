using System.Net;
using System.Reflection;
using Mgx.Engine.Http;

namespace Mgx.IntegrationTests;

/// <summary>
/// The JSON that actually goes on the wire for a -Body argument, asserted byte for byte -
/// not whether serialization merely succeeds. (Corpus: GraphSDK-3361 scalar/array,
/// GraphSDK-3654 PSObject wrappers, M365DSC-5306/7175 malformed bodies.)
/// </summary>
[Collection("Pipeline")]
public class WireBodyTests
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

    /// <summary>Runs a POST built by the given script and returns the wire body text.</summary>
    private static string WireBodyFor(string bodyExpression)
    {
        var wire = new MockHttpHandler();
        wire.SetDefaultResponse(HttpStatusCode.NoContent);
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
            ps.AddScript($"Invoke-MgxRequest -Uri /widgets -Method POST -Confirm:$false -Body $({bodyExpression})");
            ps.Invoke();

            var errors = string.Join(" | ", ps.Streams.Error.Select(e => e.Exception.Message));
            var captured = wire.CapturedRequests;
            Assert.True(captured.Count > 0, $"no request sent; errors: {errors}");
            return captured[0].BodyText!;
        }
        finally { ResetTransport(); }
    }

    [Fact]
    public void An_enum_serializes_as_its_camel_case_name()
        => Assert.Equal("""{"day":"monday"}""", WireBodyFor("@{ day = [System.DayOfWeek]::Monday }"));

    [Fact]
    public void A_byte_array_serializes_as_base64()
        => Assert.Equal("""{"key":"AQID"}""", WireBodyFor("@{ key = [byte[]](1,2,3) }"));

    [Fact]
    public void A_timespan_serializes_as_an_iso_duration()
        => Assert.Equal("""{"d":"PT1H30M"}""", WireBodyFor("@{ d = [TimeSpan]::new(1,30,0) }"));

    [Fact]
    public void A_kindless_datetime_is_pinned_to_utc()
        => Assert.Equal("""{"at":"2026-01-05T10:00:00Z"}""",
            WireBodyFor("@{ at = [DateTime]::new(2026,1,5,10,0,0) }"));

    [Fact]
    public void Non_ascii_text_is_sent_as_utf8_not_escape_sequences()
        => Assert.Equal("""{"name":"Müller & Söhne <A>"}""",
            WireBodyFor("@{ name = 'Müller & Söhne <A>' }"));

    [Fact]
    public void A_single_element_array_stays_an_array()
        => Assert.Equal("""{"members":["a"]}""", WireBodyFor("@{ members = @('a') }"));

    [Fact]
    public void A_script_property_and_an_alias_property_serialize_like_convertto_json()
    {
        var body = WireBodyFor("""
            $o = [PSCustomObject]@{ displayName = 'x' }
            $o | Add-Member -MemberType ScriptProperty -Name mailNickname -Value { 'nick-' + $this.displayName } -PassThru |
                 Add-Member -MemberType AliasProperty -Name alias -Value displayName -PassThru
            """);
        var doc = System.Text.Json.JsonDocument.Parse(body);
        Assert.Equal("x", doc.RootElement.GetProperty("displayName").GetString());
        Assert.Equal("nick-x", doc.RootElement.GetProperty("mailNickname").GetString());
        Assert.Equal("x", doc.RootElement.GetProperty("alias").GetString());
    }

    [Fact]
    public void A_wrapped_and_an_unwrapped_hashtable_produce_identical_wire_json()
    {
        var direct = WireBodyFor("@{ a = 1; nested = @{ b = $true } }");
        var wrapped = WireBodyFor("[PSObject]@{ a = 1; nested = @{ b = $true } }");
        Assert.Equal(direct, wrapped);
    }
}
