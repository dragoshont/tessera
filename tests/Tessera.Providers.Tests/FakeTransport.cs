namespace Tessera.Providers.Tests;

/// <summary>A fake transport: records the last request, returns a canned response. No network.</summary>
internal sealed class FakeTransport : IHttpTransport
{
    private readonly int _status;
    private readonly string _body;
    private readonly IReadOnlyDictionary<string, string> _responseHeaders;

    public FakeTransport(int status = 200, string body = "{\"ok\":true}", IReadOnlyDictionary<string, string>? responseHeaders = null)
    {
        _status = status;
        _body = body;
        _responseHeaders = responseHeaders ?? new Dictionary<string, string>();
    }

    public string? LastMethod { get; private set; }
    public string? LastUrl { get; private set; }
    public IReadOnlyDictionary<string, string>? LastHeaders { get; private set; }
    public string? LastBody { get; private set; }
    public int Calls { get; private set; }

    public Task<TransportResponse> SendAsync(string method, string url, IReadOnlyDictionary<string, string> headers, string? body, CancellationToken cancellationToken = default)
    {
        Calls++;
        LastMethod = method;
        LastUrl = url;
        LastHeaders = headers;
        LastBody = body;
        return Task.FromResult(new TransportResponse(_status, _responseHeaders, _body));
    }
}
