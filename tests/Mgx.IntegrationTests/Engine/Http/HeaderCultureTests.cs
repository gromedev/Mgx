using System.Globalization;
using System.Management.Automation;
using System.Net;
using System.Reflection;
using Mgx.Cmdlets.Base;

namespace Mgx.IntegrationTests;

/// <summary>
/// A -Headers value that is not a string is rendered by mgx before it goes on the wire, and
/// object.ToString() renders under the thread's culture: the same script sent "X-Ratio: 0,5"
/// on a de-DE machine and "X-Ratio: 0.5" on an en-US one.
/// </summary>
[Collection("Pipeline")]
public class HeaderCultureTests
{
    private static readonly MethodInfo Build =
        typeof(MgxCmdletBase).GetMethod("BuildRequestHeaders",
            BindingFlags.Static | BindingFlags.NonPublic)!;

    private static Dictionary<string, string> Headers(System.Collections.Hashtable values) =>
        (Dictionary<string, string>)Build.Invoke(null, [null, values])!;

    private static void InCulture(string name, Action body)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
        try { body(); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("en-US")]
    [InlineData("fr-FR")]
    public void A_non_string_header_value_renders_the_same_in_every_culture(string culture)
    {
        InCulture(culture, () =>
        {
            var headers = Headers(new System.Collections.Hashtable
            {
                ["X-Ratio"] = 0.5d,
                ["X-Decimal"] = 1.7m,
                ["X-Count"] = 100,
                ["X-List"] = new object[] { 0.5d, 100 }
            });

            Assert.Equal("0.5", headers["X-Ratio"]);
            Assert.Equal("1.7", headers["X-Decimal"]);
            Assert.Equal("100", headers["X-Count"]);
            Assert.Equal("0.5, 100", headers["X-List"]);
        });
    }

    /// <summary>A -Headers key is typed object as well, and was rendered under the culture.</summary>
    [Theory]
    [InlineData("de-DE")]
    [InlineData("en-US")]
    [InlineData("fr-FR")]
    public void A_non_string_header_name_renders_the_same_in_every_culture(string culture)
    {
        InCulture(culture, () =>
        {
            var headers = Headers(new System.Collections.Hashtable { [1.5d] = "x" });

            Assert.Equal("1.5", Assert.Single(headers.Keys));
            Assert.Equal("x", headers["1.5"]);
        });
    }

    /// <summary>The value as sent, read back off the captured request.</summary>
    [Fact]
    public void The_value_on_the_wire_does_not_depend_on_the_operators_locale()
    {
        var wire = new MockHttpHandler();
        wire.QueueResponse(HttpStatusCode.OK, "{\"id\":\"u1\"}");
        using var transport = MgxTransportScope.Inject(wire);

        using var ps = PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.InvokeMgxRequest).Assembly);
        ps.Invoke();
        ps.Commands.Clear();
        ps.AddScript(
            "[System.Threading.Thread]::CurrentThread.CurrentCulture = 'de-DE'; "
            + "Invoke-MgxRequest -Uri '/users/u1' -Headers @{ 'X-Ratio' = 0.5 }");
        ps.Invoke();

        var sent = Assert.Single(wire.CapturedRequests);
        Assert.Equal("0.5", Assert.Single(sent.Headers["X-Ratio"]));
    }
}
