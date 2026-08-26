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

    /// <summary>Runs a POST and returns (errors, requestCount) - for bodies that must refuse.</summary>
    private static (List<System.Management.Automation.ErrorRecord> Errors, int Requests) RefusalFor(string bodyExpression)
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
            return (ps.Streams.Error.ToList(), wire.CapturedRequests.Count);
        }
        finally { ResetTransport(); }
    }

    [Fact]
    public void A_secure_string_in_a_body_refuses_naming_the_property_path()
    {
        var (errors, requests) = RefusalFor("@{ profile = @{ password = (ConvertTo-SecureString 'x' -AsPlainText -Force) } }");
        Assert.Equal(0, requests);
        var error = Assert.Single(errors);
        Assert.StartsWith("InvalidBodyValue", error.FullyQualifiedErrorId);
        Assert.Contains("-Body.profile.password", error.Exception.Message);
        Assert.Contains("SecureString", error.Exception.Message);
    }

    [Fact]
    public void A_script_block_in_a_body_refuses()
    {
        var (errors, requests) = RefusalFor("@{ handler = { Get-Date } }");
        Assert.Equal(0, requests);
        var error = Assert.Single(errors);
        Assert.StartsWith("InvalidBodyValue", error.FullyQualifiedErrorId);
        Assert.Contains("ScriptBlock", error.Exception.Message);
    }

    [Fact]
    public void NaN_in_a_body_refuses_naming_the_property_path()
    {
        var (errors, requests) = RefusalFor("@{ metrics = @{ score = [double]::NaN } }");
        Assert.Equal(0, requests);
        var error = Assert.Single(errors);
        Assert.StartsWith("InvalidBodyValue", error.FullyQualifiedErrorId);
        Assert.Contains("-Body.metrics.score", error.Exception.Message);
    }

    [Fact]
    public void A_self_referencing_body_is_a_clean_error_not_a_stack_overflow()
    {
        var (errors, requests) = RefusalFor("$h = @{}; $h.self = $h; $h");
        Assert.Equal(0, requests);
        var error = Assert.Single(errors);
        Assert.StartsWith("InvalidBodyValue", error.FullyQualifiedErrorId);
        Assert.Contains("self-referencing", error.Exception.Message);
    }

    [Fact]
    public void Scalar_wire_forms_hold_for_the_remaining_corpus_types()
    {
        // Exact wire values for the types the wrapper-equivalence theory covers only
        // relationally: identical-on-both-sides is not the same as correct.
        Assert.Equal("""{"v":79228162514264337593543950335}""",
            WireBodyFor("@{ v = [decimal]::MaxValue }"));
        Assert.Equal("""{"v":"11111111-2222-3333-4444-555555555555"}""",
            WireBodyFor("@{ v = [Guid]'11111111-2222-3333-4444-555555555555' }"));
        Assert.Equal("""{"v":9223372036854775807}""",
            WireBodyFor("@{ v = [long]::MaxValue }"));
        Assert.Equal("""{"v":"2026-01-05T10:00:00+02:00"}""",
            WireBodyFor("@{ v = [DateTimeOffset]::new(2026,1,5,10,0,0,[TimeSpan]::FromHours(2)) }"));
        Assert.Equal("""{"v":[]}""", WireBodyFor("@{ v = @() }"));
    }

    [Fact]
    public void A_refused_batch_body_fails_that_item_and_sends_the_rest()
    {
        var wire = new MockHttpHandler();
        wire.QueueResponse(HttpStatusCode.OK,
            """{ "responses": [ { "id": "1", "status": 204 } ] }""");
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
            ps.AddScript("""
                @(
                    @{ Url = '/users/u1'; Method = 'PATCH'; Body = @{ s = (ConvertTo-SecureString 'x' -AsPlainText -Force) } }
                    @{ Url = '/users/u2'; Method = 'PATCH'; Body = @{ displayName = 'ok' } }
                ) | Invoke-MgxBatchRequest -Confirm:$false
                """);
            ps.Invoke();

            var error = Assert.Single(ps.Streams.Error,
                e => e.FullyQualifiedErrorId.StartsWith("InvalidBatchItemBody"));
            Assert.Contains("SecureString", error.Exception.Message);
            Assert.Single(wire.CapturedRequests); // the good item still went
        }
        finally { ResetTransport(); }
    }
}
