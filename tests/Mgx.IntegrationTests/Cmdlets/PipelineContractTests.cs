using System.Collections;
using System.Collections.Specialized;
using System.Management.Automation;
using System.Text.Json;
using Mgx.Cmdlets.Base;
using Mgx.Cmdlets.Cmdlets;

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
}
