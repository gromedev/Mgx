using System.Net;
using Mgx.Engine.Http;
using Mgx.Engine.Models;

namespace Mgx.IntegrationTests;

[Collection("Pipeline")]
public class ObservabilityTests
{
    // --- LogThrottleHeaders tests ---

    [Fact]
    public async Task ThrottleHeaders_AllPresent_LogsFullMessage()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser, new()
        {
            ["x-ms-throttle-limit-percentage"] = "0.8",
            ["x-ms-throttle-scope"] = "Tenant",
            ["x-ms-throttle-information"] = "Rate approaching limit"
        });

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var messages = new List<string>();
        client.VerboseWriter = msg => messages.Add(msg);

        await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");
        client.DrainVerboseMessages();

        Assert.Single(messages);
        Assert.Contains("80%", messages[0]); // 0.8 ratio → P0 format → "80%"
        Assert.Contains("(scope: Tenant)", messages[0]);
        Assert.Contains("[Rate approaching limit]", messages[0]);
    }

    [Fact]
    public async Task ThrottleHeaders_PercentageOnly_LogsWithoutScopeAndInfo()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser, new()
        {
            ["x-ms-throttle-limit-percentage"] = "0.45"
        });

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var messages = new List<string>();
        client.VerboseWriter = msg => messages.Add(msg);

        await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");
        client.DrainVerboseMessages();

        Assert.Single(messages);
        Assert.Contains("45%", messages[0]); // 0.45 ratio → P0 format → "45%"
        Assert.DoesNotContain("(scope:", messages[0]);
        Assert.DoesNotContain("[", messages[0]);
    }

    [Fact]
    public async Task ThrottleHeaders_Absent_NoVerboseOutput()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var messages = new List<string>();
        client.VerboseWriter = msg => messages.Add(msg);

        await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");
        client.DrainVerboseMessages();

        Assert.Empty(messages);
    }

    [Fact]
    public async Task ThrottleHeaders_NullVerboseWriter_NoException()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser, new()
        {
            ["x-ms-throttle-limit-percentage"] = "0.9"
        });

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        // VerboseWriter is null (default)

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");
        client.DrainVerboseMessages();

        // Should not throw; headers are silently ignored when no writer is set
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // --- Resource Unit tracking tests (T-3) ---

    [Fact]
    public async Task ResourceUnit_IsTracked_WhenHeaderPresent()
    {
        ResiliencePipelineFactory.Reset();
        MgxTelemetryCollector.Current.Reset();
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser, new()
        {
            ["x-ms-resource-unit"] = "5"
        });

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");

        Assert.Equal(5, MgxTelemetryCollector.Current.GetSummary().ResourceUnitsConsumed);
        MgxTelemetryCollector.Current.Reset();
    }

    [Fact]
    public async Task ResourceUnit_AccumulatesAcrossRequests()
    {
        ResiliencePipelineFactory.Reset();
        MgxTelemetryCollector.Current.Reset();
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser, new() { ["x-ms-resource-unit"] = "2" });
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser, new() { ["x-ms-resource-unit"] = "5" });
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser, new() { ["x-ms-resource-unit"] = "3" });

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");
        await client.GetAsync("https://graph.microsoft.com/v1.0/users/user2");
        await client.GetAsync("https://graph.microsoft.com/v1.0/users/user3");

        Assert.Equal(10, MgxTelemetryCollector.Current.GetSummary().ResourceUnitsConsumed);
        MgxTelemetryCollector.Current.Reset();
    }

    [Fact]
    public async Task ResourceUnit_NotTracked_WhenHeaderAbsent()
    {
        ResiliencePipelineFactory.Reset();
        MgxTelemetryCollector.Current.Reset();
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");

        Assert.Equal(0, MgxTelemetryCollector.Current.GetSummary().ResourceUnitsConsumed);
        MgxTelemetryCollector.Current.Reset();
    }

    // --- WarningWriter / throttle proximity tests (T-4) ---

    [Fact]
    public async Task ThrottleProximity_EmitsWarning_WhenAtOrOverLimit()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser, new()
        {
            ["x-ms-throttle-limit-percentage"] = "1.2",
            ["x-ms-throttle-scope"] = "Tenant"
        });

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var warnings = new List<string>();
        var verbose = new List<string>();
        client.WarningWriter = msg => warnings.Add(msg);
        client.VerboseWriter = msg => verbose.Add(msg);

        await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");
        client.DrainWarningMessages();
        client.DrainVerboseMessages();

        // Warning should be emitted for >= 1.0
        Assert.Single(warnings);
        Assert.Contains("429", warnings[0]);
        Assert.Contains("Scope: Tenant", warnings[0]);
        // Verbose should still be emitted too
        Assert.Single(verbose);
        Assert.Contains("120%", verbose[0]); // 1.2 ratio → P0 format → "120%"
    }

    [Fact]
    public async Task ThrottleProximity_NoWarning_WhenBelowLimit()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser, new()
        {
            ["x-ms-throttle-limit-percentage"] = "0.85"
        });

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var warnings = new List<string>();
        client.WarningWriter = msg => warnings.Add(msg);
        client.VerboseWriter = _ => { };

        await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");
        client.DrainWarningMessages();

        // No warning for < 1.0
        Assert.Empty(warnings);
    }

    [Fact]
    public async Task WarningWriter_MessagesBufferedUntilDrain()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser, new()
        {
            ["x-ms-throttle-limit-percentage"] = "1.5"
        });

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var warnings = new List<string>();
        client.WarningWriter = msg => warnings.Add(msg);

        await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");

        // Before drain: warnings should be empty (buffered)
        Assert.Empty(warnings);

        client.DrainWarningMessages();

        // After drain: warning should be present
        Assert.Single(warnings);
    }

    // --- Boundary tests (D1) ---

    [Fact]
    public async Task ThrottleProximity_EmitsWarning_AtExactBoundary()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser, new()
        {
            ["x-ms-throttle-limit-percentage"] = "1.0"
        });

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var warnings = new List<string>();
        client.WarningWriter = msg => warnings.Add(msg);
        client.VerboseWriter = _ => { };

        await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");
        client.DrainWarningMessages();

        Assert.Single(warnings); // >= 1.0 triggers warning
    }

    [Fact]
    public async Task ThrottleProximity_NoWarning_JustBelowBoundary()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser, new()
        {
            ["x-ms-throttle-limit-percentage"] = "0.999"
        });

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var warnings = new List<string>();
        client.WarningWriter = msg => warnings.Add(msg);
        client.VerboseWriter = _ => { };

        await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");
        client.DrainWarningMessages();

        Assert.Empty(warnings); // < 1.0 does not trigger
    }

    // --- Malformed input tests (D2) ---

    [Fact]
    public async Task ThrottlePercentage_NonNumeric_NoExceptionNoWarning()
    {
        ResiliencePipelineFactory.Reset();
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser, new()
        {
            ["x-ms-throttle-limit-percentage"] = "abc"
        });

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        var warnings = new List<string>();
        var verbose = new List<string>();
        client.WarningWriter = msg => warnings.Add(msg);
        client.VerboseWriter = msg => verbose.Add(msg);

        await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");
        client.DrainWarningMessages();
        client.DrainVerboseMessages();

        Assert.Empty(warnings); // Non-numeric: no warning
        Assert.Single(verbose); // But verbose message is still emitted with raw value
        Assert.Contains("(raw)", verbose[0]); // Fallback format
    }

    [Fact]
    public async Task ResourceUnit_NonNumeric_Ignored()
    {
        ResiliencePipelineFactory.Reset();
        MgxTelemetryCollector.Current.Reset();
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser, new()
        {
            ["x-ms-resource-unit"] = "abc"
        });

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");

        Assert.Equal(0, MgxTelemetryCollector.Current.GetSummary().ResourceUnitsConsumed);
        MgxTelemetryCollector.Current.Reset();
    }

    [Fact]
    public async Task ResourceUnit_Negative_Ignored()
    {
        ResiliencePipelineFactory.Reset();
        MgxTelemetryCollector.Current.Reset();
        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, TestData.SingleUser, new()
        {
            ["x-ms-resource-unit"] = "-5"
        });

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });

        await client.GetAsync("https://graph.microsoft.com/v1.0/users/user1");

        Assert.Equal(0, MgxTelemetryCollector.Current.GetSummary().ResourceUnitsConsumed);
        MgxTelemetryCollector.Current.Reset();
    }

    // --- GetGuidanceForCode tests ---

    [Theory]
    [InlineData("Authorization_RequestDenied", "Get-MgContext")]
    [InlineData("Request_ResourceNotFound", "-SkipNotFound")]
    [InlineData("Request_BadRequest", "$filter")]
    [InlineData("InvalidAuthenticationToken", "Connect-MgGraph")]
    [InlineData("Authentication_ExpiredToken", "Connect-MgGraph")]
    [InlineData("ErrorAccessDenied", "permissions-reference")]
    [InlineData("Forbidden", "admin consent")]
    [InlineData("TooManyRequests", "-TotalTimeoutSeconds")]
    [InlineData("activityLimitReached", "-TotalTimeoutSeconds")]
    [InlineData("ServiceNotAvailable", "status.cloud.microsoft.com")]
    [InlineData("BadRequest", "$filter")]
    public void GetGuidanceForCode_KnownCodes_ReturnsActionableHint(string code, string expectedSubstring)
    {
        var guidance = GraphServiceException.GetGuidanceForCode(code);
        Assert.NotNull(guidance);
        Assert.Contains(expectedSubstring, guidance);
    }

    [Theory]
    [InlineData("SomeUnknownCode")]
    [InlineData("")]
    [InlineData(null)]
    public void GetGuidanceForCode_UnknownCodes_ReturnsNull(string? code)
    {
        Assert.Null(GraphServiceException.GetGuidanceForCode(code));
    }

    [Fact]
    public void GraphServiceException_KnownCode_MessageContainsHint()
    {
        var body = """{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges."}}""";
        var ex = new GraphServiceException(HttpStatusCode.Forbidden, body);

        Assert.Contains("Authorization_RequestDenied", ex.Message);
        Assert.Contains("Insufficient privileges", ex.Message);
        Assert.Contains("Hint:", ex.Message);
        Assert.Contains("Get-MgContext", ex.Message);
        Assert.Equal("Authorization_RequestDenied", ex.ErrorCode);
    }

    [Fact]
    public void GraphServiceException_UnknownCode_NoHint()
    {
        var body = """{"error":{"code":"SomeFutureError","message":"Something new."}}""";
        var ex = new GraphServiceException(HttpStatusCode.BadRequest, body);

        Assert.Contains("SomeFutureError", ex.Message);
        Assert.Contains("Something new", ex.Message);
        Assert.DoesNotContain("Hint:", ex.Message);
    }

    [Fact]
    public void GraphServiceException_NullCode_PreservesMessage()
    {
        var body = """{"error":{"code":null,"message":"Something broke without a code."}}""";
        var ex = new GraphServiceException(HttpStatusCode.InternalServerError, body);

        Assert.Contains("Something broke without a code", ex.Message);
        Assert.Null(ex.ErrorCode);
    }

    [Fact]
    public void GraphServiceException_MalformedJson_FallsBackToHttpStatus()
    {
        var ex = new GraphServiceException(HttpStatusCode.InternalServerError, "not json at all");

        Assert.Contains("500", ex.Message);
        Assert.Null(ex.ErrorCode);
        Assert.DoesNotContain("Hint:", ex.Message);
    }

    // --- R3-12: Narrowed exception handling in FormatAndExtract ---

    [Fact]
    public void GraphServiceException_HandlesInvalidJson()
    {
        var ex = new GraphServiceException(System.Net.HttpStatusCode.BadRequest, "not json {{{");
        Assert.Contains("400", ex.Message);
    }

    [Fact]
    public void GraphServiceException_HandlesNullBody()
    {
        var ex = new GraphServiceException(System.Net.HttpStatusCode.InternalServerError, null!);
        Assert.Contains("500", ex.Message);
    }

    [Fact]
    public void GraphServiceException_HandlesEmptyBody()
    {
        var ex = new GraphServiceException(System.Net.HttpStatusCode.Forbidden, "");
        Assert.Contains("403", ex.Message);
    }

    [Fact]
    public void GraphServiceException_ParsesValidGraphError()
    {
        var ex = new GraphServiceException(System.Net.HttpStatusCode.NotFound,
            """{"error":{"code":"Request_ResourceNotFound","message":"Resource not found"}}""");
        Assert.Contains("Request_ResourceNotFound", ex.Message);
        Assert.Contains("Resource not found", ex.Message);
        Assert.Equal("Request_ResourceNotFound", ex.ErrorCode);
    }
}
