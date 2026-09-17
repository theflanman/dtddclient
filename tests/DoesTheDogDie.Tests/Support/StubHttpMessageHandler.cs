using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace DoesTheDogDie.Tests.Support;

/// <summary>
/// An <see cref="HttpMessageHandler"/> stub that records every request it receives and answers
/// with a response built by a caller-supplied responder function.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    /// <summary>All requests received so far, in order.</summary>
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        cancellationToken.ThrowIfCancellationRequested();
        var response = _responder(request);
        return Task.FromResult(response);
    }

    /// <summary>
    /// Builds a JSON response with the given status code and body, optionally configured further
    /// (e.g. to add rate-limit or retry-after headers) before being returned.
    /// </summary>
    public static HttpResponseMessage Json(HttpStatusCode status, string body, Action<HttpResponseMessage>? configure = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        configure?.Invoke(response);
        return response;
    }
}
