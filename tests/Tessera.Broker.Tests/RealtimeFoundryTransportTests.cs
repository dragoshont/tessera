using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Xunit;

namespace Tessera.Broker.Tests;

public sealed class RealtimeFoundryTransportTests
{
    [Fact]
    public async Task Uses_fixed_GA_paths_and_consumes_ephemeral_secret_for_bounded_SDP()
    {
        var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"value\":\"ephemeral-value\",\"expires_at\":{DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()}}}", Encoding.UTF8, "application/json"),
            },
            new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("v=0\r\nm=audio 9 UDP/TLS/RTP/SAVPF 111\r\n", Encoding.UTF8, "application/sdp"),
            });
        using var transport = new RealtimeFoundryTransport(handler);

        var secret = await transport.CreateClientSecretAsync(new Uri("https://fixed.example"),
            new("api-key", "standing-value"), new("marin", "whisper-1", "history-canary"), CancellationToken.None);
        var answer = await transport.NegotiateSdpAsync(new Uri("https://fixed.example"), secret.Value,
            "v=0\r\nm=audio 9 UDP/TLS/RTP/SAVPF 111\r\n", CancellationToken.None);

        Assert.Equal("ephemeral-value", secret.Value);
        Assert.StartsWith("v=0", answer, StringComparison.Ordinal);
        Assert.Collection(handler.Requests,
            request =>
            {
                Assert.Equal("https://fixed.example/openai/v1/realtime/client_secrets", request.Url);
                Assert.Equal("standing-value", request.ApiKey);
                Assert.Null(request.Authorization);
                Assert.Equal("application/json", request.ContentType);
                Assert.Contains("\"model\":\"tessera-realtime-21\"", request.Body, StringComparison.Ordinal);
                Assert.Contains("\"voice\":\"marin\"", request.Body, StringComparison.Ordinal);
                Assert.Contains("\"model\":\"whisper-1\"", request.Body, StringComparison.Ordinal);
                Assert.Contains("history-canary", request.Body, StringComparison.Ordinal);
                Assert.DoesNotContain("\"tools\"", request.Body, StringComparison.Ordinal);
                Assert.DoesNotContain("conversation", request.Body, StringComparison.OrdinalIgnoreCase);
            },
            request =>
            {
                Assert.Equal("https://fixed.example/openai/v1/realtime/calls", request.Url);
                Assert.Equal("Bearer ephemeral-value", request.Authorization);
                Assert.Equal("application/sdp", request.ContentType);
                Assert.Equal("v=0\r\nm=audio 9 UDP/TLS/RTP/SAVPF 111\r\n", request.Body);
            });
    }

    [Fact]
    public async Task Oversized_answer_fails_with_redacted_provider_malformed()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("v=0\r\nm=audio 9\r\n" + new string('a', 64 * 1024))),
        });
        using var transport = new RealtimeFoundryTransport(handler);

        var exception = await Assert.ThrowsAsync<RealtimeProviderException>(() => transport.NegotiateSdpAsync(
            new Uri("https://fixed.example"), "ephemeral-canary", "v=0\r\nm=audio 9\r\n", CancellationToken.None));

        Assert.Equal(502, exception.StatusCode);
        Assert.Equal("provider_malformed", exception.Code);
        Assert.DoesNotContain("ephemeral-canary", exception.ToString(), StringComparison.Ordinal);
    }

    private sealed record RecordedRequest(string Url, string? Authorization, string? ApiKey, string? ContentType, string Body);

    private sealed class RecordingHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("api-key", out var values) ? values.Single() : null,
                request.Content?.Headers.ContentType?.MediaType,
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return _responses.Dequeue();
        }
    }
}