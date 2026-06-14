using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tessera.Core.Portal;

namespace Tessera.Broker;

/// <summary>
/// The real HTTP client for the browser-worker live-view contract (ADR 0016 §3) —
/// the broker half of "Phase 1 wraps the existing noVNC/harvester as the first
/// browser worker." It POSTs an arm request to the worker and parses the session
/// it returns. It is hardened like the egress transport (no proxy, no redirects,
/// no ambient cookies, short timeout) and <b>fail-closed</b>: any non-2xx,
/// transport error, timeout, or unparseable body yields <c>null</c>, which
/// <see cref="WorkerLiveViewProvider"/> maps to a secret-free Unavailable result.
///
/// <para>It never sees the session cookie: the worker harvests that to the vault
/// itself (the cookie never crosses the broker). The only secret this client may
/// carry is an optional caller token authenticating the broker <em>to</em> the
/// worker, sent as a bearer header and never logged.</para>
/// </summary>
public sealed class HttpLiveViewWorker : ILiveViewWorker, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _client;
    private readonly Uri _armEndpoint;
    private readonly string? _callerToken;
    private readonly bool _ownsClient;

    /// <summary>Creates the worker client over an arm endpoint.</summary>
    /// <param name="armEndpoint">The absolute URL the broker POSTs an arm request to.</param>
    /// <param name="callerToken">Optional bearer token authenticating the broker to the worker (never logged).</param>
    /// <param name="client">An <see cref="HttpClient"/> to reuse (tests); null = a hardened internal one.</param>
    public HttpLiveViewWorker(Uri armEndpoint, string? callerToken = null, HttpClient? client = null)
    {
        ArgumentNullException.ThrowIfNull(armEndpoint);
        _armEndpoint = armEndpoint;
        _callerToken = string.IsNullOrWhiteSpace(callerToken) ? null : callerToken;
        if (client is null)
        {
            _client = new HttpClient(new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(10),
            })
            {
                Timeout = TimeSpan.FromSeconds(20),
            };
            _ownsClient = true;
        }
        else
        {
            _client = client;
            _ownsClient = false;
        }
    }

    /// <inheritdoc/>
    public async Task<WorkerLiveViewSession?> ArmAsync(LiveViewWorkerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _armEndpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(
                    new ArmRequestDto(request.ConnectionId, request.Principal, request.Provider), Json),
                Encoding.UTF8,
                "application/json"),
        };
        if (_callerToken is not null)
        {
            httpRequest.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_callerToken}");
        }

        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null; // worker unreachable → fail-closed
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null; // timeout → fail-closed (a real caller-cancel still throws)
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return null; // 4xx/5xx (slot busy, unmapped, error) → fail-closed
            }

            ArmResponseDto? dto;
            try
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                dto = JsonSerializer.Deserialize<ArmResponseDto>(body, Json);
            }
            catch (JsonException)
            {
                return null; // unparseable → fail-closed
            }

            if (dto is null || string.IsNullOrWhiteSpace(dto.LiveViewUrl) || string.IsNullOrWhiteSpace(dto.TargetHostname))
            {
                return null;
            }

            return new WorkerLiveViewSession(
                LiveViewUrl: dto.LiveViewUrl,
                TargetHostname: dto.TargetHostname,
                TtlSeconds: dto.TtlSeconds,
                ReadWrite: dto.ReadWrite ?? true,
                FaviconUrl: dto.FaviconUrl);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    private sealed record ArmRequestDto(
        [property: JsonPropertyName("connectionId")] string ConnectionId,
        [property: JsonPropertyName("principal")] string Principal,
        [property: JsonPropertyName("provider")] string Provider);

    private sealed record ArmResponseDto(
        [property: JsonPropertyName("liveViewUrl")] string? LiveViewUrl,
        [property: JsonPropertyName("targetHostname")] string? TargetHostname,
        [property: JsonPropertyName("ttlSeconds")] int? TtlSeconds,
        [property: JsonPropertyName("readWrite")] bool? ReadWrite,
        [property: JsonPropertyName("faviconUrl")] string? FaviconUrl);
}
