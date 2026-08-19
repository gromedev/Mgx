using System.Collections;
using System.Text.Json;
using Mgx.Cmdlets.Base;

namespace Mgx.IntegrationTests.Cmdlets;

/// <summary>
/// JsonToHashtable is the single JSON-to-PowerShell conversion point for every
/// Graph-data cmdlet. It defines the module's public output contract: case-insensitive
/// Hashtables with @odata.type preserved exactly as Graph returned it, and all other
/// @odata.* transport metadata stripped.
/// </summary>
public class JsonToHashtableTests
{
    private static Hashtable Convert(string json) =>
        MgxCmdletBase.JsonToHashtable(JsonSerializer.Deserialize<JsonElement>(json));

    [Fact]
    public void Produces_a_Hashtable_with_the_JSON_properties_as_keys()
    {
        var result = Convert("""{ "id": "abc", "displayName": "Bob" }""");

        Assert.Equal("abc", result["id"]);
        Assert.Equal("Bob", result["displayName"]);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Key_lookup_is_case_insensitive()
    {
        // Matches PowerShell's @{} literal, so $user.DisplayName still resolves
        // the camelCase key exactly as it did when the output was a PSObject.
        var result = Convert("""{ "displayName": "Bob" }""");

        Assert.Equal("Bob", result["DISPLAYNAME"]);
        Assert.Equal("Bob", result["displayname"]);
        Assert.True(result.ContainsKey("DisplayName"));
    }

    [Fact]
    public void Preserves_odata_type_verbatim()
    {
        var result = Convert("""{ "@odata.type": "#microsoft.graph.user", "id": "abc" }""");

        Assert.Equal("#microsoft.graph.user", result["@odata.type"]);
    }

    [Fact]
    public void Strips_odata_etag()
    {
        // The etag changes on every write, so keeping it would make two reads of an
        // unchanged entity compare unequal - phantom drift for state-comparison
        // consumers. -Raw | ConvertFrom-Json still exposes it for If-Match callers.
        var result = Convert("""{ "@odata.etag": "W/\"JzEtVXNlcic=\"", "id": "abc" }""");

        Assert.False(result.ContainsKey("@odata.etag"));
        Assert.Equal("abc", result["id"]);
    }

    [Theory]
    [InlineData("@odata.context")]
    [InlineData("@odata.nextLink")]
    [InlineData("@odata.count")]
    [InlineData("@odata.etag")]
    public void Strips_odata_transport_metadata(string metadataKey)
    {
        var result = Convert($$"""
            { "{{metadataKey}}": "transport-value", "id": "abc" }
            """);

        Assert.False(result.ContainsKey(metadataKey));
        Assert.Equal("abc", result["id"]);
    }

    [Fact]
    public void Keeps_non_odata_annotations_such_as_removed()
    {
        // Sync-MgxDelta relies on @removed to report deleted entities
        var result = Convert("""
            { "id": "abc", "@removed": { "reason": "changed" } }
            """);

        Assert.True(result.ContainsKey("@removed"));
    }

    [Fact]
    public void Converts_nested_objects_to_Hashtables_preserving_odata_type()
    {
        var result = Convert("""
            {
              "id": "abc",
              "manager": { "@odata.type": "#microsoft.graph.user", "id": "mgr-1" }
            }
            """);

        var manager = Assert.IsType<Hashtable>(result["manager"]);
        Assert.Equal("mgr-1", manager["id"]);
        Assert.Equal("#microsoft.graph.user", manager["@odata.type"]);
    }

    [Fact]
    public void Converts_arrays_of_objects_to_arrays_of_Hashtables()
    {
        var result = Convert("""
            {
              "members": [
                { "@odata.type": "#microsoft.graph.group",  "id": "g1" },
                { "@odata.type": "#microsoft.graph.device", "id": "d1" }
              ]
            }
            """);

        var members = Assert.IsType<object?[]>(result["members"]);
        Assert.Equal(2, members.Length);
        Assert.Equal("#microsoft.graph.group", Assert.IsType<Hashtable>(members[0])["@odata.type"]);
        Assert.Equal("#microsoft.graph.device", Assert.IsType<Hashtable>(members[1])["@odata.type"]);
    }

    [Fact]
    public void Converts_scalar_arrays_without_wrapping_the_elements()
    {
        var result = Convert("""{ "groupTypes": [ "Unified", "DynamicMembership" ] }""");

        var types = Assert.IsType<object?[]>(result["groupTypes"]);
        Assert.Equal(["Unified", "DynamicMembership"], types);
    }

    [Fact]
    public void Parses_ISO8601_strings_into_DateTime()
    {
        var result = Convert("""{ "createdDateTime": "2024-01-15T10:30:00Z" }""");

        var created = Assert.IsType<DateTime>(result["createdDateTime"]);
        Assert.Equal(new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc), created);
    }

    [Fact]
    public void Leaves_non_date_strings_alone()
    {
        var result = Convert("""{ "displayName": "2024 Planning Committee" }""");

        Assert.Equal("2024 Planning Committee", result["displayName"]);
    }

    [Fact]
    public void Maps_JSON_primitives_to_matching_CLR_types()
    {
        var result = Convert("""
            {
              "count": 42,
              "ratio": 1.5,
              "mailEnabled": true,
              "securityEnabled": false,
              "description": null
            }
            """);

        Assert.Equal(42L, result["count"]);
        Assert.Equal(1.5d, result["ratio"]);
        Assert.Equal(true, result["mailEnabled"]);
        Assert.Equal(false, result["securityEnabled"]);
        Assert.Null(result["description"]);
        Assert.True(result.ContainsKey("description"));
    }

    [Fact]
    public void Wraps_a_non_object_element_under_a_Value_key()
    {
        // Some Graph endpoints return a bare scalar; PowerShell needs a named member
        var result = Convert("\"just-a-string\"");

        Assert.Equal("just-a-string", result["Value"]);
    }

    [Fact]
    public void Last_value_wins_for_keys_differing_only_by_case()
    {
        // A case-insensitive Hashtable must not throw on such a payload
        var result = Convert("""{ "id": "first", "ID": "second" }""");

        Assert.Equal("second", result["id"]);
        Assert.Single(result);
    }
}
