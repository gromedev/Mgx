using System.Collections;
using System.Management.Automation;
using Mgx.Cmdlets.Cmdlets;

namespace Mgx.IntegrationTests;

/// <summary>
/// The acceptance criterion for body serialization: a PowerShell wrapper never alters the
/// wire JSON. Serialize(x) must be byte-identical to Serialize(PSObject.AsPSObject(x)),
/// with the wrapping at any depth. (GraphSDK-3654 PSObject wrappers, GraphSDK-3361
/// scalar/array.)
/// </summary>
public class PSWrapperEquivalenceTests
{
    public static TheoryData<string, object> Values()
    {
        var d = new TheoryData<string, object>
        {
            { "bool", true },
            { "int", 42 },
            { "long-max", long.MaxValue },
            { "double", 3.5d },
            { "decimal", 79228162514264337593543950335m },
            { "guid", Guid.Parse("11111111-2222-3333-4444-555555555555") },
            { "datetime-utc", new DateTime(2026, 1, 5, 10, 0, 0, DateTimeKind.Utc) },
            { "datetime-unspecified", new DateTime(2026, 1, 5, 10, 0, 0, DateTimeKind.Unspecified) },
            { "datetimeoffset", new DateTimeOffset(2026, 1, 5, 10, 0, 0, TimeSpan.FromHours(2)) },
            { "timespan", new TimeSpan(1, 30, 0) },
            { "enum", DayOfWeek.Monday },
            { "string-unicode", "Müller & Söhne <A> 日本語 😀" },
            { "string-quotes", "it's \"quoted\"" },
            { "bytes", new byte[] { 1, 2, 3 } },
            { "array", new object[] { 1, "two", true } },
            { "single-element-array", new object[] { "alone" } },
            { "empty-array", Array.Empty<object>() },
        };
        return d;
    }

    private static string Serialize(object body) => InvokeMgxRequest.SerializeBody(body);

    [Theory]
    [MemberData(nameof(Values))]
    public void Wrapping_at_depth_0_changes_nothing(string label, object value)
    {
        _ = label;
        var direct = Serialize(new Hashtable { ["v"] = value });
        var wrapped = Serialize(new Hashtable { ["v"] = PSObject.AsPSObject(value) });
        Assert.Equal(direct, wrapped);
    }

    [Theory]
    [MemberData(nameof(Values))]
    public void Wrapping_at_depth_1_changes_nothing(string label, object value)
    {
        _ = label;
        var direct = Serialize(new Hashtable { ["outer"] = new Hashtable { ["v"] = value } });
        var wrapped = Serialize(PSObject.AsPSObject(
            new Hashtable { ["outer"] = PSObject.AsPSObject(new Hashtable { ["v"] = PSObject.AsPSObject(value) }) }));
        Assert.Equal(direct, wrapped);
    }

    [Theory]
    [MemberData(nameof(Values))]
    public void Wrapping_at_depth_3_changes_nothing(string label, object value)
    {
        _ = label;
        object Nest(object v) => new Hashtable { ["a"] = new Hashtable { ["b"] = new Hashtable { ["c"] = v } } };
        var direct = Serialize(Nest(value));
        var wrapped = Serialize(Nest(PSObject.AsPSObject(value)));
        Assert.Equal(direct, wrapped);
    }

    [Fact]
    public void An_array_of_wrapped_elements_matches_the_unwrapped_array()
    {
        var direct = Serialize(new Hashtable { ["members"] = new object[] { "a", 1, true } });
        var wrapped = Serialize(new Hashtable
        {
            ["members"] = new object[]
            {
                PSObject.AsPSObject("a"), PSObject.AsPSObject(1), PSObject.AsPSObject(true)
            }
        });
        Assert.Equal(direct, wrapped);
    }
}
