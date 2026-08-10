using System.Net;

namespace StonkWatch.Web.Tests;

/// <summary>Returns canned responses and records the requests it was asked to make.</summary>
public sealed class StubHttpMessageHandler(
    Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];

    public static StubHttpMessageHandler Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        });

    /// <summary>Returns each body in turn, one per request — for testing batching.</summary>
    public static StubHttpMessageHandler Sequence(params string[] bodies)
    {
        var index = 0;
        return new StubHttpMessageHandler(_ =>
        {
            var body = bodies[Math.Min(index++, bodies.Length - 1)];
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };
        });
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        return Task.FromResult(respond(request));
    }
}
