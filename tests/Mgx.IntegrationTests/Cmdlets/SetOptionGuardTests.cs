using System.Management.Automation;
using Mgx.Cmdlets.Base;
using Mgx.Engine.Http;

namespace Mgx.IntegrationTests;

/// <summary>
/// Set-MgxOption rebuilds the resilience pipeline, which discards circuit-breaker failure
/// history. It guards against doing that for nothing - but the guard counted every bound
/// parameter, and PowerShell binds the common ones too, so -Verbose alone looked like a change.
/// </summary>
[Collection("Pipeline")]
public class SetOptionGuardTests
{
    private static PowerShell Shell()
    {
        var ps = PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Configuration.SetMgxOption).Assembly);
        ps.Invoke();
        ps.Commands.Clear();
        return ps;
    }

    [Theory]
    [InlineData("Verbose")]
    [InlineData("ErrorAction")]
    public void A_common_parameter_alone_is_not_a_change(string parameter)
    {
        using var ps = Shell();
        var cmd = ps.AddCommand("Set-MgxOption").AddParameter("Verbose", true);
        if (parameter == "ErrorAction") cmd.AddParameter("ErrorAction", ActionPreference.Continue);
        ps.Invoke();

        var said = string.Join(" ", ps.Streams.Verbose.Select(v => v.Message));
        Assert.Contains("Options unchanged", said);
        Assert.DoesNotContain("Mgx options updated", said);
    }

    [Fact]
    public void A_real_parameter_still_updates()
    {
        using var ps = Shell();
        ps.AddCommand("Set-MgxOption")
          .AddParameter("RateLimitPerSecond", 42)
          .AddParameter("Verbose", true);
        ps.Invoke();

        var said = string.Join(" ", ps.Streams.Verbose.Select(v => v.Message));
        Assert.Contains("Mgx options updated", said);

        // put it back
        using var reset = Shell();
        reset.AddCommand("Set-MgxOption").AddParameter("Reset", true);
        reset.Invoke();
    }

    /// <summary>
    /// -NoRateLimit turns off batch item pacing too. That is a second mechanism, aimed at Graph's
    /// server-side write throttle rather than the client-side limiter, and it has its own
    /// documented off switch - so a caller setting both should be told which one won.
    /// </summary>
    [Fact]
    public void Setting_NoRateLimit_beside_BatchItemsPerSecond_says_which_one_wins()
    {
        try
        {
            using var ps = Shell();
            ps.AddCommand("Set-MgxOption")
              .AddParameter("NoRateLimit", true)
              .AddParameter("BatchItemsPerSecond", 20);
            ps.Invoke();

            var warned = string.Join(" ", ps.Streams.Warning.Select(w => w.Message));
            Assert.Contains("has no effect", warned);
            Assert.Contains("BatchItemsPerSecond", warned);
        }
        finally
        {
            using var reset = Shell();
            reset.AddCommand("Set-MgxOption").AddParameter("Reset", true);
            reset.Invoke();
        }
    }
}
