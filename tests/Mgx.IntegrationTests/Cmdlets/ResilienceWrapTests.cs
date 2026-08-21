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

    /// <summary>Counts what the wire actually saw, and what it answered with.</summary>
    private sealed class ThrottleThenOk : HttpMessageHandler
    {
        private readonly int _throttles;
        public int Calls { get; private set; }
        public ThrottleThenOk(int throttles) => _throttles = throttles;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (Calls <= _throttles)
            {
                var r = new HttpResponseMessage((System.Net.HttpStatusCode)429) { RequestMessage = request };
                r.Headers.TryAddWithoutValidation("Retry-After", "0");
                return Task.FromResult(r);
            }
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("{\"value\":[]}", System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>
    /// A 429 answered through the bridge must be visible to mgx: the adaptive pacer learns from
    /// throttles, and Get-MgxTelemetry reports them.
    ///
    /// Scope: this covers the BRIDGE, with the wire directly beneath it. A live session also has
    /// the SDK's own RetryHandler inside the wrapped chain, which may answer a 429 before the
    /// outer pipeline ever sees it - that is not exercised here, because Kiota's handlers are
    /// only constructed by a real Graph client. What this pins is that the bridge itself does
    /// not swallow a throttle on the way past.
    /// </summary>
    [Fact]
    public void A_throttle_through_the_wrapper_is_visible_to_mgx()
    {
        var wire = new ThrottleThenOk(throttles: 1);
        using var sdkClient = new HttpClient(wire) { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
        var warnings = new List<string>();

        MgxTelemetryCollector.Current.Reset();
        var wrapper = BuildWrapper(sdkClient, warnings);
        Assert.NotNull(wrapper);

        using var resp = wrapper!.GetAsync("users").GetAwaiter().GetResult();

        var snapshot = MgxTelemetryCollector.Current.GetSummary();
        Assert.True(resp.IsSuccessStatusCode, $"final status was {(int)resp.StatusCode}");
        Assert.Equal(2, wire.Calls);                       // the 429 and the retry both hit the wire
        Assert.True(snapshot.ThrottleRetries > 0,
            $"mgx recorded {snapshot.ThrottleRetries} throttle retries for a 429 the wire served");
    }
}
