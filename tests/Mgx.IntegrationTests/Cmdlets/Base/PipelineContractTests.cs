using System.Collections;
using System.Collections.Specialized;
using System.Management.Automation;
using System.Text.Json;
using Mgx.Cmdlets.Base;
using Mgx.Cmdlets.Cmdlets;
using Mgx.Cmdlets.Cmdlets.Batch;

namespace Mgx.IntegrationTests.Cmdlets;

/// <summary>
/// Every place that reads members off pipeline input has to handle dictionaries as well as
/// PSCustomObjects, and -Body serialization must see through the PSObject wrapper PowerShell
/// puts around values bound to object-typed parameters. PowerShell does none of this
/// automatically: a PSObject-wrapped string missed the string branch of the serializer and
/// went out as {} - a write that succeeded with an empty body and no error anywhere.
/// </summary>
public class PipelineContractTests
{
    private static PSObject PSCustomObject(params (string Name, object? Value)[] members)
    {
        var pso = new PSObject();
        foreach (var (name, value) in members)
            pso.Properties.Add(new PSNoteProperty(name, value));
        return pso;
    }

    #region TryGetMember / UnwrapPSObject

    [Fact]
    public void TryGetMember_reads_hashtable_keys()
    {
        var input = new Hashtable(StringComparer.OrdinalIgnoreCase) { ["id"] = "abc" };

        Assert.Equal("abc", MgxCmdletBase.TryGetMember(input, "id"));
    }

    [Fact]
    public void TryGetMember_reads_keys_of_a_PSObject_wrapped_hashtable()
    {
        // PowerShell wraps pipeline objects in a PSObject before binding
        var wrapped = PSObject.AsPSObject(
            new Hashtable(StringComparer.OrdinalIgnoreCase) { ["id"] = "abc" });

        Assert.Equal("abc", MgxCmdletBase.TryGetMember(wrapped, "id"));
    }

    [Fact]
    public void TryGetMember_reads_PSCustomObject_note_properties()
    {
        Assert.Equal("abc", MgxCmdletBase.TryGetMember(PSCustomObject(("id", "abc")), "id"));
    }

    [Fact]
    public void TryGetMember_reads_ordered_dictionaries()
    {
        var ordered = new OrderedDictionary { ["id"] = "abc" };

        Assert.Equal("abc", MgxCmdletBase.TryGetMember(ordered, "id"));
    }

    [Fact]
    public void TryGetMember_returns_null_for_absent_members_and_null_input()
    {
        Assert.Null(MgxCmdletBase.TryGetMember(new Hashtable(), "id"));
        Assert.Null(MgxCmdletBase.TryGetMember(PSCustomObject(("name", "x")), "id"));
        Assert.Null(MgxCmdletBase.TryGetMember(null, "id"));
        Assert.Null(MgxCmdletBase.TryGetMember(42, "id"));
    }

    [Fact]
    public void UnwrapPSObject_unwraps_real_dotnet_values_but_keeps_PSCustomObject()
    {
        var hashtable = new Hashtable();
        Assert.Same(hashtable, MgxCmdletBase.UnwrapPSObject(PSObject.AsPSObject(hashtable)));
        Assert.Equal("plain", MgxCmdletBase.UnwrapPSObject(PSObject.AsPSObject("plain")));

        // A PSCustomObject keeps its members on the PSObject; its BaseObject is an
        // empty marker, so unwrapping it would discard everything.
        var pso = PSCustomObject(("id", "abc"));
        Assert.Same(pso, MgxCmdletBase.UnwrapPSObject(pso));
    }

    #endregion

    #region Invoke-MgxRequest fan-out input

    [Fact]
    public void ResolvePipelineId_accepts_a_bare_id_string()
    {
        Assert.Equal("abc", InvokeMgxRequest.ResolvePipelineId("abc"));
        Assert.Equal("abc", InvokeMgxRequest.ResolvePipelineId(PSObject.AsPSObject("abc")));
    }

    [Fact]
    public void ResolvePipelineId_extracts_id_from_a_piped_hashtable()
    {
        // Regression: a hashtable used to bind whole to the [string] parameter, putting
        // the literal "System.Collections.Hashtable" into the request URL with no error.
        var user = new Hashtable(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = "abc",
            ["displayName"] = "Bob"
        };

        Assert.Equal("abc", InvokeMgxRequest.ResolvePipelineId(user));
        Assert.Equal("abc", InvokeMgxRequest.ResolvePipelineId(PSObject.AsPSObject(user)));
    }

    [Fact]
    public void ResolvePipelineId_extracts_id_from_a_piped_PSCustomObject()
    {
        Assert.Equal("abc", InvokeMgxRequest.ResolvePipelineId(PSCustomObject(("id", "abc"))));
    }

    [Fact]
    public void ResolvePipelineId_returns_null_when_there_is_no_id()
    {
        // ProcessRecord turns this into a WriteError rather than a corrupt URL
        Assert.Null(InvokeMgxRequest.ResolvePipelineId(new Hashtable { ["displayName"] = "Bob" }));
        Assert.Null(InvokeMgxRequest.ResolvePipelineId(PSCustomObject(("displayName", "Bob"))));
    }

    #endregion

    #region Invoke-MgxBatchRequest input

    [Fact]
    public void ParsePipelineInput_treats_a_string_as_a_url_with_the_shared_method()
    {
        var cmdlet = new InvokeMgxBatchRequest { Method = "GET" };

        var parsed = cmdlet.ParsePipelineInput("/me");

        Assert.NotNull(parsed);
        Assert.Equal("/me", parsed.Url);
        Assert.Equal("GET", parsed.Method);
    }

    [Fact]
    public void ParsePipelineInput_accepts_a_hashtable_with_per_item_method_and_body()
    {
        var cmdlet = new InvokeMgxBatchRequest { Method = "GET" };
        var item = new Hashtable(StringComparer.OrdinalIgnoreCase)
        {
            ["Url"] = "/users",
            ["Method"] = "post",
            ["Body"] = new Hashtable { ["displayName"] = "Bob" }
        };

        var parsed = cmdlet.ParsePipelineInput(item);

        Assert.NotNull(parsed);
        Assert.Equal("/users", parsed.Url);
        Assert.Equal("POST", parsed.Method);
        Assert.NotNull(parsed.Body);
    }

    [Fact]
    public void ParsePipelineInput_accepts_this_cmdlets_own_output_shape()
    {
        // Batch results carry Url/Method/Status/Body, so they can be piped back in for retry
        var cmdlet = new InvokeMgxBatchRequest { Method = "GET" };
        var previousResult = new Hashtable(StringComparer.OrdinalIgnoreCase)
        {
            ["Url"] = "/me",
            ["Method"] = "GET",
            ["Status"] = 429,
            ["Body"] = null
        };

        var parsed = cmdlet.ParsePipelineInput(previousResult);

        Assert.NotNull(parsed);
        Assert.Equal("/me", parsed.Url);
        Assert.Equal("GET", parsed.Method);
    }

    [Fact]
    public void ParsePipelineInput_still_accepts_the_documented_PSCustomObject_shape()
    {
        var cmdlet = new InvokeMgxBatchRequest { Method = "GET" };

        var parsed = cmdlet.ParsePipelineInput(
            PSCustomObject(("Url", "/groups"), ("Method", "PATCH")));

        Assert.NotNull(parsed);
        Assert.Equal("/groups", parsed.Url);
        Assert.Equal("PATCH", parsed.Method);
    }

    [Fact]
    public void ParsePipelineInput_falls_back_to_the_shared_method_when_the_item_has_none()
    {
        var cmdlet = new InvokeMgxBatchRequest { Method = "DELETE" };

        var parsed = cmdlet.ParsePipelineInput(new Hashtable { ["Url"] = "/users/abc" });

        Assert.NotNull(parsed);
        Assert.Equal("DELETE", parsed.Method);
    }

    #endregion

    #region Body serialization

    [Fact]
    public void SerializeBody_passes_a_string_through_untouched()
    {
        const string raw = """{"displayName":"Bob"}""";

        Assert.Equal(raw, InvokeMgxRequest.SerializeBody(raw));
    }

    [Fact]
    public void SerializeBody_serializes_a_hashtable()
    {
        var json = InvokeMgxRequest.SerializeBody(new Hashtable { ["displayName"] = "Bob" });

        Assert.Equal("Bob", JsonDocument.Parse(json).RootElement.GetProperty("displayName").GetString());
    }

    [Fact]
    public void SerializeBody_serializes_a_PSCustomObject()
    {
        var json = InvokeMgxRequest.SerializeBody(PSCustomObject(("displayName", "Bob")));

        Assert.Equal("Bob", JsonDocument.Parse(json).RootElement.GetProperty("displayName").GetString());
    }

    [Fact]
    public void SerializeBody_recurses_into_a_nested_PSCustomObject()
    {
        // Regression: a nested PSCustomObject used to serialize as {} because its
        // BaseObject was assumed to be a PSObject. Silent data loss on write.
        var body = new Hashtable
        {
            ["profile"] = PSCustomObject(("city", "Bern"))
        };

        var json = InvokeMgxRequest.SerializeBody(body);

        var city = JsonDocument.Parse(json).RootElement
            .GetProperty("profile").GetProperty("city").GetString();
        Assert.Equal("Bern", city);
    }

    [Fact]
    public void SerializeBody_recurses_through_mixed_nesting_and_arrays()
    {
        var body = new Hashtable
        {
            ["outer"] = PSCustomObject(("inner", new Hashtable { ["leaf"] = 1 })),
            ["tags"] = new object[] { "a", new Hashtable { ["k"] = "v" } }
        };

        var root = JsonDocument.Parse(InvokeMgxRequest.SerializeBody(body)).RootElement;

        Assert.Equal(1, root.GetProperty("outer").GetProperty("inner").GetProperty("leaf").GetInt32());
        Assert.Equal("a", root.GetProperty("tags")[0].GetString());
        Assert.Equal("v", root.GetProperty("tags")[1].GetProperty("k").GetString());
    }

    [Fact]
    public void SerializeBody_passes_a_PSObject_wrapped_string_through_untouched()
    {
        // The actual reported bug: -Body (@{...} | ConvertTo-Json) arrived as a
        // PSObject-wrapped string, missed the string branch, and was serialized as {}.
        const string raw = """{"ids":["3429da5f-44ce-4020-ad7c-cb839eb50528"]}""";

        Assert.Equal(raw, InvokeMgxRequest.SerializeBody(PSObject.AsPSObject(raw)));
    }

    [Fact]
    public void SerializeBody_serializes_a_PSObject_wrapped_hashtable()
    {
        var wrapped = PSObject.AsPSObject(new Hashtable { ["displayName"] = "Bob" });

        var json = InvokeMgxRequest.SerializeBody(wrapped);

        Assert.Equal("Bob", JsonDocument.Parse(json).RootElement.GetProperty("displayName").GetString());
    }

    [Fact]
    public void SerializeBody_serializes_a_PSObject_wrapped_array()
    {
        var wrapped = PSObject.AsPSObject(new object[] { "a", new Hashtable { ["k"] = "v" } });

        var root = JsonDocument.Parse(InvokeMgxRequest.SerializeBody(wrapped)).RootElement;

        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.Equal("a", root[0].GetString());
        Assert.Equal("v", root[1].GetProperty("k").GetString());
    }

    [Fact]
    public void SerializeBody_serializes_a_non_array_list_body()
    {
        var body = new ArrayList { new Hashtable { ["k"] = "v" } };

        var root = JsonDocument.Parse(InvokeMgxRequest.SerializeBody(body)).RootElement;

        Assert.Equal("v", root[0].GetProperty("k").GetString());
    }

    [Fact]
    public void SerializeBody_serializes_an_ordered_dictionary()
    {
        var json = InvokeMgxRequest.SerializeBody(new OrderedDictionary { ["displayName"] = "Bob" });

        Assert.Equal("Bob", JsonDocument.Parse(json).RootElement.GetProperty("displayName").GetString());
    }

    #endregion

    #region Response shape

    [Fact]
    public void TryUnwrapCollection_unwraps_an_action_response_envelope()
    {
        // POST /directoryObjects/getByIds and friends answer with the same {"value":[...]}
        // envelope a GET collection uses. The write path used to emit it as one object.
        var json = JsonSerializer.Deserialize<JsonElement>("""
            { "@odata.context": "ctx", "value": [ { "id": "a" }, { "id": "b" } ] }
            """);

        var items = InvokeMgxRequest.TryUnwrapCollection(json, out var truncated);

        Assert.NotNull(items);
        Assert.Equal(2, items.Count);
        Assert.Equal("a", items[0].GetProperty("id").GetString());
        Assert.False(truncated);
    }

    [Fact]
    public void TryUnwrapCollection_reports_a_truncated_envelope()
    {
        var json = JsonSerializer.Deserialize<JsonElement>("""
            { "value": [ { "id": "a" } ], "@odata.nextLink": "https://graph.microsoft.com/v1.0/next" }
            """);

        Assert.NotNull(InvokeMgxRequest.TryUnwrapCollection(json, out var truncated));
        Assert.True(truncated);
    }

    [Fact]
    public void TryUnwrapCollection_leaves_a_single_entity_alone()
    {
        // An entity with its own scalar 'value' property must not be mistaken for a collection
        var json = JsonSerializer.Deserialize<JsonElement>("""{ "id": "a", "value": "not-an-array" }""");

        Assert.Null(InvokeMgxRequest.TryUnwrapCollection(json, out _));
    }

    [Fact]
    public void TryUnwrapCollection_unwraps_an_entity_with_an_array_valued_value_property()
    {
        // Documented limitation: the gate is structural, so an entity whose own 'value'
        // property is an array (e.g. a schemaExtension value collection) is indistinguishable
        // from a collection envelope and unwraps. -Raw is the escape hatch for such payloads.
        var json = JsonSerializer.Deserialize<JsonElement>("""
            { "id": "a", "value": [ 1, 2, 3 ] }
            """);

        var items = InvokeMgxRequest.TryUnwrapCollection(json, out var truncated);

        Assert.NotNull(items);
        Assert.Equal(3, items.Count);
        Assert.False(truncated);
    }

    #endregion
}
