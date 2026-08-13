using System.Net;
using System.Text;

namespace Mgx.IntegrationTests.Fakes;

/// <summary>
/// Scripted HttpMessageHandler for testing the resilience pipeline without network access.
/// Responses are dequeued in order; the last response repeats once the queue is drained,
/// so a test only has to script the responses it actually cares about.
/// </summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();
    private Func<HttpRequestMessage, HttpResponseMessage>? _last;
    private readonly Lock _sync = new();

    /// <summary>Method and URI of every request the handler received, in order.</summary>
    public List<(HttpMethod Method, string Uri)> Requests { get; } = [];

    public int RequestCount
    {
        get { lock (_sync) return Requests.Count; }
    }

    public StubHttpMessageHandler Enqueue(Func<HttpRequestMessage, HttpResponseMessage> factory)
    {
        _responses.Enqueue(factory);
        return this;
    }

    /// <summary>Queue a response with a JSON body.</summary>
    public StubHttpMessageHandler EnqueueJson(HttpStatusCode status, string json) =>
        Enqueue(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });

    /// <summary>Queue a status-only response, optionally with a Retry-After header in seconds.</summary>
    public StubHttpMessageHandler EnqueueStatus(HttpStatusCode status, int? retryAfterSeconds = null) =>
        Enqueue(_ =>
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
            if (retryAfterSeconds.HasValue)
                response.Headers.Add("Retry-After", retryAfterSeconds.Value.ToString());
            return response;
        });

    /// <summary>Queue the same response factory <paramref name="count"/> times.</summary>
    public StubHttpMessageHandler EnqueueRepeated(int count, Func<HttpRequestMessage, HttpResponseMessage> factory)
    {
        for (var i = 0; i < count; i++)
            Enqueue(factory);
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Func<HttpRequestMessage, HttpResponseMessage> factory;
        lock (_sync)
        {
            // Record method/uri rather than the message: HttpClient disposes the
            // request once the call completes, so the object is not safe to keep.
            Requests.Add((request.Method, request.RequestUri?.ToString() ?? string.Empty));

            if (_responses.Count > 0)
                _last = _responses.Dequeue();

            factory = _last ?? throw new InvalidOperationException(
                "StubHttpMessageHandler received a request but no response was scripted.");
        }

        var response = factory(request);
        return Task.FromResult(response);
    }
}
