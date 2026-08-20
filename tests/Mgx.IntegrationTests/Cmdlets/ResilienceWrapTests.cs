using System.Reflection;
using Mgx.Cmdlets.Cmdlets.Configuration;
using Mgx.Engine.Http;

namespace Mgx.IntegrationTests;

/// <summary>
/// Enable-MgxResilience swaps the SDK's HttpClient for a wrapper that bridges to the
/// original. Invoke-MgGraphRequest resolves a relative -Uri against the active client's
/// BaseAddress before any handler runs, so the wrapper must carry the original client's
/// configuration or every relative-URI SDK call dies before reaching the wire.
/// </summary>
[Collection("Pipeline")]
public class ResilienceWrapTests
{
    private static HttpClient? BuildWrapper(HttpClient sdkClient, List<string> warnings)
    {
        var build = typeof(EnableMgxResilience).GetMethod(
            "BuildResilientSdkClient", BindingFlags.NonPublic | BindingFlags.Static)!;
        try
        {
            return (HttpClient?)build.Invoke(null, [sdkClient, (Action<string>)warnings.Add]);
        }
        finally
        {
            EnableMgxResilience.ActiveHandler = null;
            ResiliencePipelineFactory.Reset();
        }
    }

    [Fact]
    public void The_wrapper_keeps_the_sdk_clients_base_address()
    {
        using var sdkClient = new HttpClient
        {
            BaseAddress = new Uri("https://graph.microsoft.com/v1.0/")
        };
        var warnings = new List<string>();

        var wrapper = BuildWrapper(sdkClient, warnings);

        Assert.NotNull(wrapper);
        Assert.Empty(warnings);
        Assert.Equal(sdkClient.BaseAddress, wrapper.BaseAddress);
    }

    [Fact]
    public void The_wrapper_keeps_the_sdk_clients_timeout()
    {
        using var sdkClient = new HttpClient { Timeout = TimeSpan.FromSeconds(123) };
        var warnings = new List<string>();

        var wrapper = BuildWrapper(sdkClient, warnings);

        Assert.NotNull(wrapper);
        Assert.Equal(sdkClient.Timeout, wrapper.Timeout);
    }
}
