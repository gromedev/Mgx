using System.Net;
using Mgx.Engine.Http;

namespace Mgx.IntegrationTests;

/// <summary>
/// The rate limiter's lifetime against option changes. Every ResilientGraphClient and every
/// ResilientDelegatingHandler captures the limiter it was built with as a readonly field, and
/// the Enable-MgxResilience handler is not rebuilt when options change - so a limiter released
/// by GetOrCreate or Reset is still the one live traffic goes through. Disposing it on a timer
/// left those sessions throwing ObjectDisposedException minutes after a Set-MgxOption call.
/// </summary>
[Collection("Pipeline")]
public class RateLimiterLifetimeTests
{
    /// <summary>
    /// Longer than the window the old timer waited, so a scheduled disposal has fired by the
    /// time the held handler sends.
    /// </summary>
    private static readonly TimeSpan PastTheOldDisposalWindow = TimeSpan.FromSeconds(1.5);

    private static (ResilientDelegatingHandler Handler, System.Threading.RateLimiting.TokenBucketRateLimiter Limiter)
        BuildHeldHandler(HttpMessageHandler inner)
    {
        // TotalTimeoutSeconds was what the old code used as the disposal delay; 1s keeps the
        // wait short without changing what is being proven.
        var options = new ResilientGraphClientOptions { TotalTimeoutSeconds = 1 };
        var (pipeline, rateLimiter) = ResiliencePipelineFactory.GetOrCreate(options);
        Assert.NotNull(rateLimiter);
        return (new ResilientDelegatingHandler(pipeline, rateLimiter) { InnerHandler = inner }, rateLimiter);
    }

    [Fact]
    public async Task A_held_handler_keeps_its_limiter_when_options_change()
    {
        ResiliencePipelineFactory.Reset();
        try
        {
            var wire = new MockHttpHandler();
            wire.SetDefaultResponse(HttpStatusCode.OK, TestData.SingleUser);
            var (handler, limiter) = BuildHeldHandler(wire);
            using var held = handler;
            using var httpClient = new HttpClient(held);

            // Set-MgxOption builds a fresh options instance, so GetOrCreate rebuilds. The
            // old code read the disposal delay off the INCOMING options, so the replacement
            // has to carry the same short timeout or the wait below outlasts nothing.
            ResiliencePipelineFactory.GetOrCreate(
                new ResilientGraphClientOptions { RateLimitPerSecond = 10, TotalTimeoutSeconds = 1 });
            await Task.Delay(PastTheOldDisposalWindow);

            var response = await httpClient.GetAsync("https://graph.microsoft.com/v1.0/users/user1");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, wire.RequestCount);
            // The lease path above already went through the limiter; reading its statistics
            // fails on a disposed instance and says so more precisely than the send does.
            Assert.NotNull(limiter.GetStatistics());
        }
        finally { ResiliencePipelineFactory.Reset(); }
    }

    [Fact]
    public async Task A_held_handler_keeps_its_limiter_across_a_reset()
    {
        ResiliencePipelineFactory.Reset();
        try
        {
            var wire = new MockHttpHandler();
            wire.SetDefaultResponse(HttpStatusCode.OK, TestData.SingleUser);
            var (handler, limiter) = BuildHeldHandler(wire);
            using var held = handler;
            using var httpClient = new HttpClient(held);

            // A tenant change drops the cached pipeline and limiter outright.
            ResiliencePipelineFactory.Reset();
            await Task.Delay(PastTheOldDisposalWindow);

            var response = await httpClient.GetAsync("https://graph.microsoft.com/v1.0/users/user1");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, wire.RequestCount);
            Assert.NotNull(limiter.GetStatistics());
        }
        finally { ResiliencePipelineFactory.Reset(); }
    }
}
