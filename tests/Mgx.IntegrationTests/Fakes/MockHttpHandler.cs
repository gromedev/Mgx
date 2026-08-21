using System.Net;
using System.Text;

namespace Mgx.IntegrationTests;

/// <summary>
/// Serializes test classes that share ResiliencePipelineFactory static state.
/// Without this, xUnit runs classes in parallel and Reset() calls from one
/// class can corrupt circuit breaker/pipeline state in another.
/// </summary>
[CollectionDefinition("Pipeline")]
public class PipelineCollection;

/// <summary>
/// Mock HTTP handler that returns configurable responses.
/// Tracks request count for verifying retry behavior.
/// </summary>
public class MockHttpHandler : HttpMessageHandler
{
    private readonly Queue<MockResponse> _responses = new();
    private readonly List<HttpRequestMessage> _requests = [];
    private readonly object _lock = new();
    private MockResponse? _defaultResponse;

    public int RequestCount
    {
        get { lock (_lock) { return _requests.Count; } }
    }

    public List<HttpRequestMessage> Requests
    {
        get { lock (_lock) { return [.. _requests]; } }
    }

    public void QueueResponse(HttpStatusCode statusCode, string? body = null, Dictionary<string, string>? headers = null)
    {
        _responses.Enqueue(new MockResponse(statusCode, body, headers, null));
    }

    public void SetDefaultResponse(HttpStatusCode statusCode, string? body = null, Dictionary<string, string>? headers = null)
    {
        _defaultResponse = new MockResponse(statusCode, body, headers, null);
    }

    /// <summary>
    /// Queue N failures followed by a success.
    /// </summary>
    public void QueueFailuresThenSuccess(int failCount, HttpStatusCode failStatus, string successBody, Dictionary<string, string>? failHeaders = null)
    {
        for (int i = 0; i < failCount; i++)
            QueueResponse(failStatus, null, failHeaders);
        QueueResponse(HttpStatusCode.OK, successBody);
    }

    /// <summary>
    /// Queue an exception to be thrown on the next request.
    /// Used to test retry behavior for TaskCanceledException, HttpRequestException, etc.
    /// </summary>
    public void QueueException(Exception exception)
    {
        _responses.Enqueue(new MockResponse(HttpStatusCode.OK, null, null, exception));
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        MockResponse mock;
        lock (_lock)
        {
            _requests.Add(request);
            mock = _responses.Count > 0 ? _responses.Dequeue() : (_defaultResponse ?? new MockResponse(HttpStatusCode.OK, null, null, null));
        }

        if (mock.Exception != null)
            return Task.FromException<HttpResponseMessage>(mock.Exception);

        // Real transports (SocketsHttpHandler) set RequestMessage on the response; consumers
        // like the pacer's OnRetry hook read the request URI off it. Mirror that here.
        var response = new HttpResponseMessage(mock.StatusCode) { RequestMessage = request };
        if (mock.Body != null)
            response.Content = new StringContent(mock.Body, Encoding.UTF8, "application/json");

        if (mock.Headers != null)
        {
            foreach (var (key, value) in mock.Headers)
                response.Headers.TryAddWithoutValidation(key, value);
        }

        return Task.FromResult(response);
    }

    private record MockResponse(HttpStatusCode StatusCode, string? Body, Dictionary<string, string>? Headers, Exception? Exception);
}

public static class TestData
{
    public static string UsersPage1 => """
    {
        "value": [
            {"id": "user1", "displayName": "User One"},
            {"id": "user2", "displayName": "User Two"}
        ],
        "@odata.nextLink": "https://graph.microsoft.com/v1.0/users?$skiptoken=page2"
    }
    """;

    public static string UsersPage2 => """
    {
        "value": [
            {"id": "user3", "displayName": "User Three"}
        ]
    }
    """;

    public static string SingleUser => """
    {
        "id": "user1",
        "displayName": "User One",
        "userPrincipalName": "user1@test.com"
    }
    """;

    public static string EmptyCollection => """
    {
        "value": []
    }
    """;
}
