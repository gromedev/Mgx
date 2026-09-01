using System.Collections;
using System.Globalization;
using System.Net;
using System.Text.Json.Serialization;
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
    /// <summary>Runs a POST built by the given script and returns the wire body text.</summary>
    private static string WireBodyFor(string bodyExpression)
    {
        var wire = new MockHttpHandler();
        wire.SetDefaultResponse(HttpStatusCode.NoContent);
        using (MgxTransportScope.Inject(wire))
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
    }

    [Fact]
    public void An_enum_serializes_as_its_camel_case_name()
        => Assert.Equal("""{"day":"monday"}""", WireBodyFor("@{ day = [System.DayOfWeek]::Monday }"));

    [Fact]
    public void A_flags_enum_combination_joins_its_names_without_a_space()
        => Assert.Equal("""{"opt":"ignoreCase,multiline"}""",
            WireBodyFor("@{ opt = [System.Text.RegularExpressions.RegexOptions]'IgnoreCase, Multiline' }"));

    [Fact]
    public void A_single_flag_serializes_as_its_camel_case_name()
        => Assert.Equal("""{"opt":"multiline"}""",
            WireBodyFor("@{ opt = [System.Text.RegularExpressions.RegexOptions]::Multiline }"));

    /// <summary>Read and Fetch share a value; Enum.ToString and STJ pick different names for it.</summary>
    [Flags]
    private enum AliasedFlags { Read = 1, Fetch = 1, Write = 2 }

    private enum AliasedPlain { Read = 1, Fetch = 1, Write = 2 }

    [Flags]
    private enum WireFlags { None = 0, Read = 1, Write = 2 }

    /// <summary>Read carries a wire name Enum.ToString cannot see.</summary>
    [Flags]
    private enum RenamedFlags
    {
        [JsonStringEnumMemberName("read-only")] Read = 1,
        Write = 2,
    }

    /// <summary>BC covers B and C, so 7 has a composite member and a bit left over.</summary>
    [Flags]
    private enum CompositeFlags
    {
        A = 1,
        B = 2,
        C = 4,
        [JsonStringEnumMemberName("b-c")] BC = 6,
    }

    private enum PlatformEnum { IOS, Android }

    private sealed class FlagsKeyedHolder
    {
        public Dictionary<WireFlags, int> Scores { get; } = new() { [WireFlags.Read | WireFlags.Write] = 1 };
    }

    private sealed class RenamedKeyedHolder
    {
        public Dictionary<RenamedFlags, int> Scores { get; } = new() { [RenamedFlags.Read | RenamedFlags.Write] = 1 };
    }

    private static string Serialize(object body)
        => Mgx.Cmdlets.Cmdlets.InvokeMgxRequest.SerializeBody(body);

    [Fact]
    public void An_aliased_flag_gets_the_same_name_as_the_same_enum_without_flags()
    {
        // Resolving a single name from Enum.ToString would silently rename it: the two
        // members share a value, and Enum.ToString and STJ's name cache disagree on which
        // one wins. Only the multi-name form belongs to the flags path.
        var withFlags = Serialize(new Hashtable { ["v"] = AliasedFlags.Read });
        var withoutFlags = Serialize(new Hashtable { ["v"] = AliasedPlain.Read });
        Assert.Equal(withoutFlags, withFlags);
    }

    [Fact]
    public void A_flags_value_with_no_names_stays_a_number_under_any_culture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            // sv-SE writes a negative sign as U+2212, so a number recognized by inspecting
            // Enum.ToString would be quoted as a name instead and Graph would refuse it.
            CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
            Assert.Equal("""{"v":-3}""", Serialize(new Hashtable { ["v"] = (WireFlags)(-3) }));
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public void A_flags_enum_used_as_a_dictionary_key_joins_its_names_too()
        => Assert.Equal("""{"thing":{"Scores":{"read,write":1}}}""",
            Serialize(new Hashtable { ["thing"] = new FlagsKeyedHolder() }));

    [Fact]
    public void An_aliased_flag_in_a_combination_gets_the_name_it_gets_alone()
        // Enum.ToString calls this value Fetch and STJ calls it Read, so a combination read
        // from Enum.ToString sent "fetch,write" where the member alone was sent "read".
        => Assert.Equal("""{"v":"read,write"}""",
            Serialize(new Hashtable { ["v"] = AliasedFlags.Read | AliasedFlags.Write }));

    [Fact]
    public void A_renamed_flag_keeps_its_wire_name_in_a_combination()
        => Assert.Equal("""{"v":"read-only,write"}""",
            Serialize(new Hashtable { ["v"] = RenamedFlags.Read | RenamedFlags.Write }));

    [Fact]
    public void A_renamed_combination_names_a_dictionary_key_the_same_way()
        => Assert.Equal("""{"thing":{"Scores":{"read-only,write":1}}}""",
            Serialize(new Hashtable { ["thing"] = new RenamedKeyedHolder() }));

    [Fact]
    public void A_member_covering_several_bits_wins_over_the_bits()
        // 7 is A|B|C, and BC is a member: components are taken largest first, so BC is chosen
        // over B and C, and written after A because the names go out in ascending value order.
        => Assert.Equal("""{"v":"a,b-c"}""", Serialize(new Hashtable { ["v"] = (CompositeFlags)7 }));

    [Fact]
    public void A_composite_member_on_its_own_stays_a_single_name()
        => Assert.Equal("""{"v":"b-c"}""", Serialize(new Hashtable { ["v"] = CompositeFlags.BC }));

    [Fact]
    public void A_leading_acronym_is_lower_cased_whole()
        // The -Body help documents this: Graph spells the same value "iOS", so a caller who
        // needs that has to pass the string. Pinned so the help and the wire cannot drift.
        => Assert.Equal("""{"v":"ios"}""", Serialize(new Hashtable { ["v"] = PlatformEnum.IOS }));

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
        using (MgxTransportScope.Inject(wire))
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
        using (MgxTransportScope.Inject(wire))
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
    }
}
