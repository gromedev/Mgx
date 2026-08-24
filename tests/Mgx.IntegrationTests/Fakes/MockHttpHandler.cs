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
/// A request as it looked on the wire at send time. Captured eagerly because the
/// client may dispose the request content once the call completes, so reading
/// Requests[n].Content after the fact is unreliable.
/// </summary>
public sealed record CapturedRequest(
    HttpMethod Method,
    string Uri,
    IReadOnlyDictionary<string, string[]> Headers,
    IReadOnlyDictionary<string, string[]> ContentHeaders,
    byte[]? Body)
{
    public string? BodyText => Body == null ? null : Encoding.UTF8.GetString(Body);
}

/// <summary>
/// Mock HTTP handler that returns configurable responses.
/// Tracks request count for verifying retry behavior.
/// </summary>
public class MockHttpHandler : HttpMessageHandler
{
    private readonly Queue<MockResponse> _responses = new();
    private readonly List<HttpRequestMessage> _requests = [];
    private readonly List<CapturedRequest> _captured = [];
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

    /// <summary>
    /// Requests buffered at send time: method, URI, headers, and body bytes.
    /// Survives the client disposing the originals.
    /// </summary>
    public List<CapturedRequest> CapturedRequests
    {
        get { lock (_lock) { return [.. _captured]; } }
    }

    public void QueueResponse(HttpStatusCode statusCode, string? body = null, Dictionary<string, string>? headers = null, string contentType = "application/json")
    {
        _responses.Enqueue(new MockResponse(statusCode, body, headers, null, contentType, null));
    }

    /// <summary>
    /// Queue a response with a raw byte body, for binary and non-UTF8 cases.
    /// </summary>
    public void QueueBytes(HttpStatusCode statusCode, byte[] body, string contentType, Dictionary<string, string>? headers = null)
    {
        _responses.Enqueue(new MockResponse(statusCode, null, headers, null, contentType, body));
    }

    /// <summary>
    /// Queue a response with no content at all - a 204, or a 200 with a zero-length body.
    /// </summary>
    public void QueueEmpty(HttpStatusCode statusCode, Dictionary<string, string>? headers = null)
    {
        _responses.Enqueue(new MockResponse(statusCode, null, headers, null, null, null));
    }

    public void SetDefaultResponse(HttpStatusCode statusCode, string? body = null, Dictionary<string, string>? headers = null)
    {
        _defaultResponse = new MockResponse(statusCode, body, headers, null, "application/json", null);
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
        _responses.Enqueue(new MockResponse(HttpStatusCode.OK, null, null, exception, null, null));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        byte[]? bodyBytes = null;
        if (request.Content != null)
            bodyBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);

        var captured = new CapturedRequest(
            request.Method,
            request.RequestUri?.OriginalString ?? string.Empty,
            request.Headers.ToDictionary(h => h.Key, h => h.Value.ToArray()),
            request.Content?.Headers.ToDictionary(h => h.Key, h => h.Value.ToArray())
                ?? new Dictionary<string, string[]>(),
            bodyBytes);

        MockResponse mock;
        lock (_lock)
        {
            _requests.Add(request);
            _captured.Add(captured);
            mock = _responses.Count > 0 ? _responses.Dequeue() : (_defaultResponse ?? new MockResponse(HttpStatusCode.OK, null, null, null, null, null));
        }

        if (mock.Exception != null)
            throw mock.Exception;

        // Real transports (SocketsHttpHandler) set RequestMessage on the response; consumers
        // like the pacer's OnRetry hook read the request URI off it. Mirror that here.
        var response = new HttpResponseMessage(mock.StatusCode) { RequestMessage = request };
        if (mock.BodyBytes != null)
        {
            response.Content = new ByteArrayContent(mock.BodyBytes);
            if (mock.ContentType != null)
                response.Content.Headers.TryAddWithoutValidation("Content-Type", mock.ContentType);
        }
        else if (mock.Body != null)
        {
            response.Content = new StringContent(mock.Body, Encoding.UTF8, mock.ContentType ?? "application/json");
        }

        if (mock.Headers != null)
        {
            foreach (var (key, value) in mock.Headers)
                response.Headers.TryAddWithoutValidation(key, value);
        }

        return response;
    }

    private record MockResponse(HttpStatusCode StatusCode, string? Body, Dictionary<string, string>? Headers, Exception? Exception, string? ContentType, byte[]? BodyBytes);
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
