using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Tessera.Broker.Egress;
using Tessera.Core.Egress;
using Tessera.Core.Configuration;
using Tessera.Core.Kernel;
using Tessera.Core.Product;
using Tessera.Core.Stores;
using Tessera.Persistence.Sqlite;
using Tessera.Plugin.Abstractions;
using Tessera.Providers;

namespace Tessera.Broker;

internal static class FoundryRealtimeContract
{
    public const string ModelId = "gpt-realtime-2.1";
    public const string ModelVersion = "2026-07-07";
    public const string DeploymentRef = "tessera-realtime-21";
}

public sealed record RealtimeFoundrySecret(string Value, DateTimeOffset ExpiresAt);
public sealed record RealtimeFoundryCredential(string Mode, string Value);
public sealed record RealtimeFoundrySessionConfiguration(
    string Voice, string TranscriptionModel, string? Instructions,
    IReadOnlyList<JsonElement>? Tools = null);

public interface IRealtimeFoundryTransport
{
    Task<RealtimeFoundrySecret> CreateClientSecretAsync(
        Uri endpoint, RealtimeFoundryCredential credential,
        RealtimeFoundrySessionConfiguration session, CancellationToken cancellationToken);

    Task<string> NegotiateSdpAsync(
        Uri endpoint, string ephemeralSecret, string offerSdp, CancellationToken cancellationToken);
}

public sealed class RealtimeFoundryTransport : IRealtimeFoundryTransport, IDisposable
{
    private const int MaximumSecretResponseBytes = 16 * 1024;
    private static readonly string[] AudioOutputModalities = ["audio"];
    private static readonly JsonSerializerOptions OmitNullJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly HttpClient _client;

    public RealtimeFoundryTransport()
        : this(HttpClientTransport.CreateGuardedHttpClient(AddressGuard.PublicOnly))
    {
    }

    internal RealtimeFoundryTransport(HttpMessageHandler handler)
        : this(new HttpClient(handler, disposeHandler: true))
    {
    }

    private RealtimeFoundryTransport(HttpClient client)
    {
        _client = client;
        _client.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task<RealtimeFoundrySecret> CreateClientSecretAsync(
        Uri endpoint, RealtimeFoundryCredential credential,
        RealtimeFoundrySessionConfiguration session, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new
        {
            session = new
            {
                type = "realtime",
                model = FoundryRealtimeContract.DeploymentRef,
                output_modalities = AudioOutputModalities,
                instructions = session.Instructions,
                audio = new
                {
                    input = new
                    {
                        transcription = new { model = session.TranscriptionModel },
                        turn_detection = new { type = "server_vad" },
                    },
                    output = new { voice = session.Voice },
                },
                tools = session.Tools is { Count: > 0 } ? session.Tools : null,
            },
        }, OmitNullJson);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "/openai/v1/realtime/client_secrets"));
        ApplyCredential(request, credential);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        RequireSuccess((int)response.StatusCode);
        var responseBody = await ReadBoundedAsync(response.Content, MaximumSecretResponseBytes, cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            var value = root.GetProperty("value").GetString();
            var expiresAt = ReadExpiry(root.GetProperty("expires_at"));
            if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || expiresAt <= DateTimeOffset.UtcNow.AddSeconds(10))
                throw new RealtimeProviderException(502, "provider_malformed");
            return new(value, expiresAt);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new RealtimeProviderException(502, "provider_malformed");
        }
    }

    public async Task<string> NegotiateSdpAsync(
        Uri endpoint, string ephemeralSecret, string offerSdp, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "/openai/v1/realtime/calls"));
        request.Headers.Authorization = new("Bearer", ephemeralSecret);
        request.Content = new StringContent(offerSdp, Encoding.UTF8, "application/sdp");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        RequireSuccess((int)response.StatusCode);
        var answer = await ReadBoundedAsync(response.Content, RealtimeVoiceLimits.MaximumSdpBytes, cancellationToken).ConfigureAwait(false);
        RealtimeVoiceService.ValidateSdp(answer, answer: true);
        return answer;
    }

    private static DateTimeOffset ReadExpiry(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetInt64(out var seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds),
        JsonValueKind.String => DateTimeOffset.Parse(value.GetString()!, System.Globalization.CultureInfo.InvariantCulture),
        _ => throw new FormatException(),
    };

    private static void ApplyCredential(HttpRequestMessage request, RealtimeFoundryCredential credential)
    {
        if (credential.Mode == "api-key") request.Headers.Add("api-key", credential.Value);
        else if (credential.Mode == "bearer") request.Headers.Authorization = new("Bearer", credential.Value);
        else throw new RealtimeProviderException(502, "provider_auth_required");
    }

    private static void RequireSuccess(int status)
    {
        if (status is >= 200 and < 300) return;
        throw status switch
        {
            401 or 403 => new RealtimeProviderException(502, "provider_auth_required"),
            429 => new RealtimeProviderException(429, "provider_rate_limited"),
            _ => new RealtimeProviderException(502, "provider_unavailable"),
        };
    }

    private static async Task<string> ReadBoundedAsync(HttpContent content, int maximumBytes, CancellationToken token)
    {
        if (content.Headers.ContentLength > maximumBytes) throw new RealtimeProviderException(502, "provider_malformed");
        await using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream(Math.Min(maximumBytes, 8192));
        var bytes = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(bytes, token).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > maximumBytes) throw new RealtimeProviderException(502, "provider_malformed");
            await buffer.WriteAsync(bytes.AsMemory(0, read), token).ConfigureAwait(false);
        }
        try { return new UTF8Encoding(false, true).GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length)); }
        catch (DecoderFallbackException) { throw new RealtimeProviderException(502, "provider_malformed"); }
    }

    public void Dispose() => _client.Dispose();
}

public sealed class RealtimeProviderException(int statusCode, string code) : Exception(code)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

public sealed record RealtimeReadinessSnapshot(
    string State, string? BlockedCode, bool SupportsTools, int MaxSessionSeconds,
    DateTimeOffset? CheckedAt, DateTimeOffset? ValidUntil, long Version);

public sealed class RealtimeReadinessService(
    TesseraConfig config, ICredentialStore custody, IRealtimeFoundryTransport transport,
    SqliteKernelStore store) : BackgroundService
{
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private readonly object _snapshotGate = new();
    private RealtimeReadinessSnapshot _snapshot = config.RealtimeVoice.Enabled
        ? new("CHECKING", null, false, RealtimeVoiceLimits.ClampSessionSeconds(config.RealtimeVoice.MaxSessionSeconds), null, null, 1)
        : new("UNAVAILABLE", "not_configured", false, RealtimeVoiceLimits.ClampSessionSeconds(config.RealtimeVoice.MaxSessionSeconds), null, null, 1);

    public RealtimeReadinessSnapshot GetCached()
    {
        lock (_snapshotGate)
        {
            if (_snapshot.State == "READY" && _snapshot.ValidUntil <= DateTimeOffset.UtcNow)
                _snapshot = _snapshot with { State = "BLOCKED", BlockedCode = "readiness_stale", ValidUntil = null, Version = _snapshot.Version + 1 };
            return _snapshot;
        }
    }

    public void RecordNegotiationSuccess()
    {
        var now = DateTimeOffset.UtcNow;
        Update(current => new("READY", null, true,
            RealtimeVoiceLimits.ClampSessionSeconds(config.RealtimeVoice.MaxSessionSeconds),
            now, now.AddMinutes(5), current.Version + 1));
    }

    public void RecordNegotiationFailure(string code)
    {
        var now = DateTimeOffset.UtcNow;
        Update(current => new("BLOCKED", SafeBlockedCode(code), false,
            RealtimeVoiceLimits.ClampSessionSeconds(config.RealtimeVoice.MaxSessionSeconds),
            now, null, current.Version + 1));
    }

    public async Task ProbeAsync(CancellationToken cancellationToken)
    {
        if (!config.RealtimeVoice.Enabled) return;
        await _probeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = GetCached();
            if (current.State == "READY" && current.ValidUntil > DateTimeOffset.UtcNow) return;
            var now = DateTimeOffset.UtcNow;
            Set(current with { State = "CHECKING", BlockedCode = null, CheckedAt = now, ValidUntil = null, Version = current.Version + 1 });
            try
            {
                var credential = await custody.GetBundleAsync(config.RealtimeVoice.CredentialRef, cancellationToken).ConfigureAwait(false);
                if (!credential.HasAccessToken) throw new RealtimeProviderException(502, "credential_unavailable");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var secret = await transport.CreateClientSecretAsync(
                    new Uri(config.RealtimeVoice.Endpoint, UriKind.Absolute),
                    new(config.RealtimeVoice.AuthenticationMode, credential.AccessToken!),
                    new(config.RealtimeVoice.Voice, config.RealtimeVoice.TranscriptionModel, null, []),
                    timeout.Token).ConfigureAwait(false);
                if (secret.ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(10))
                    throw new RealtimeProviderException(502, "provider_malformed");
                RecordNegotiationSuccess();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                RecordNegotiationFailure("provider_timeout");
            }
            catch (RealtimeProviderException exception)
            {
                RecordNegotiationFailure(exception.Code);
            }
            catch (Exception exception) when (exception is StoreException or IOException or HttpRequestException)
            {
                RecordNegotiationFailure("provider_unavailable");
            }
        }
        finally
        {
            _probeGate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.RealtimeVoice.Enabled) return;
        try
        {
            await store.FenceExpiredRealtimeNegotiationsAsync(DateTimeOffset.UtcNow, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                await ProbeAsync(stoppingToken).ConfigureAwait(false);
                var next = GetCached().ValidUntil ?? DateTimeOffset.UtcNow.AddMinutes(5);
                var delay = next - DateTimeOffset.UtcNow;
                if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private void Set(RealtimeReadinessSnapshot value)
    {
        lock (_snapshotGate) _snapshot = value;
    }

    private void Update(Func<RealtimeReadinessSnapshot, RealtimeReadinessSnapshot> update)
    {
        lock (_snapshotGate) _snapshot = update(_snapshot);
    }

    private static string SafeBlockedCode(string code) => code switch
    {
        "provider_auth_required" or "provider_rate_limited" or "provider_timeout" or
        "provider_malformed" or "provider_unavailable" or "credential_unavailable" => code,
        _ => "provider_unavailable",
    };
}

public sealed record RealtimeNegotiationResult(
    string SessionId, string AnswerSdp, DateTimeOffset NegotiatedAt,
    DateTimeOffset ExpiresAt, int MaxSessionSeconds);

public sealed class RealtimeVoiceService(
    TesseraConfig config, SqliteKernelStore store, ICredentialStore custody,
    IRealtimeFoundryTransport transport, RealtimeReadinessService readiness,
    TesseraPluginRegistry plugins) : IDisposable
{
    private sealed record ReplayEntry(RealtimeNegotiationResult Result, DateTimeOffset ValidUntil);
    private sealed record Flight(string KeyHash, string OfferHash, Task<RealtimeNegotiationResult> Task);

    private readonly ConcurrentDictionary<string, ReplayEntry> _replay = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Flight> _flights = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _beginGate = new(1, 1);

    public async Task<RealtimeNegotiationResult> NegotiateAsync(
        string owner, string conversationId, string clientAttemptId, string idempotencyKey,
        string offerSdp, CancellationToken cancellationToken)
    {
        ValidateIdentifier(clientAttemptId, nameof(clientAttemptId));
        ValidateIdentifier(idempotencyKey, nameof(idempotencyKey));
        ValidateSdp(offerSdp, answer: false);
        var cached = readiness.GetCached();
        if (cached.State != "READY") throw new RealtimeProviderException(422, "realtime_unavailable");
        var keyHash = Hash(idempotencyKey);
        var offerHash = Hash(offerSdp);
        var flightKey = $"{owner}\n{clientAttemptId}";

        await _beginGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Flight flight;
        try
        {
            await store.FenceExpiredRealtimeNegotiationsAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            var existing = await store.GetRealtimeSessionByAttemptAsync(owner, clientAttemptId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing.ConversationId != conversationId || existing.IdempotencyKeyHash != keyHash || existing.OfferHash != offerHash)
                    throw new RealtimeProviderException(409, "idempotency_conflict");
                if (existing.State == "NEGOTIATED")
                {
                    if (_replay.TryGetValue(existing.SessionId, out var replay) && replay.ValidUntil > DateTimeOffset.UtcNow)
                        return replay.Result;
                    throw new RealtimeProviderException(409, "realtime_negotiation_expired");
                }
                if (existing.State != "NEGOTIATING" || !_flights.TryGetValue(flightKey, out flight!))
                    throw new RealtimeProviderException(409, "realtime_negotiation_expired");
                if (flight.KeyHash != keyHash || flight.OfferHash != offerHash)
                    throw new RealtimeProviderException(409, "idempotency_conflict");
            }
            else
            {
                var now = DateTimeOffset.UtcNow;
                if (await store.CountOpenRealtimeSessionsAsync(owner, now, cancellationToken).ConfigureAwait(false)
                    >= config.RealtimeVoice.OwnerSessionLimit
                    || await store.CountOpenRealtimeSessionsAsync(null, now, cancellationToken).ConfigureAwait(false)
                    >= config.RealtimeVoice.GlobalSessionLimit)
                    throw new RealtimeProviderException(429, "realtime_session_limit");
                var maxSeconds = RealtimeVoiceLimits.ClampSessionSeconds(config.RealtimeVoice.MaxSessionSeconds);
                var sessionId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{owner}\n{conversationId}\n{clientAttemptId}\n{idempotencyKey}")));
                var generation = now.UtcTicks;
                var receipt = new RealtimeSessionReceipt(owner, sessionId, conversationId, clientAttemptId,
                    keyHash, offerHash, "NEGOTIATING", generation, now.AddSeconds(30),
                    FoundryRealtimeContract.ModelId, FoundryRealtimeContract.ModelVersion, FoundryRealtimeContract.DeploymentRef,
                    null, now.AddSeconds(maxSeconds), null, null, null, 1);
                var sessionConfiguration = await BuildSessionConfigurationAsync(
                    owner, conversationId, sessionId, cancellationToken).ConfigureAwait(false);
                if (!await store.BeginRealtimeNegotiationAsync(
                    receipt, sessionConfiguration.Projection.Tools, cancellationToken).ConfigureAwait(false))
                    throw new RealtimeProviderException(409, "idempotency_conflict");
                var task = NegotiateUpstreamAsync(
                    receipt, offerSdp, maxSeconds, sessionConfiguration.Configuration, CancellationToken.None);
                flight = new(keyHash, offerHash, task);
                _flights[flightKey] = flight;
                _ = task.ContinueWith(completedTask => _flights.TryRemove(flightKey, out var removedFlight), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        finally
        {
            _beginGate.Release();
        }
        return await flight.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<RealtimeNegotiationResult> NegotiateUpstreamAsync(
        RealtimeSessionReceipt receipt, string offerSdp, int maxSeconds,
        RealtimeFoundrySessionConfiguration sessionConfiguration, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var credential = await custody.GetBundleAsync(config.RealtimeVoice.CredentialRef, timeout.Token).ConfigureAwait(false);
            if (!credential.HasAccessToken) throw new RealtimeProviderException(502, "provider_auth_required");
            var endpoint = new Uri(config.RealtimeVoice.Endpoint, UriKind.Absolute);
            var secret = await transport.CreateClientSecretAsync(endpoint,
                new(config.RealtimeVoice.AuthenticationMode, credential.AccessToken!),
                sessionConfiguration, timeout.Token).ConfigureAwait(false);
            var answer = await transport.NegotiateSdpAsync(endpoint, secret.Value, offerSdp, timeout.Token).ConfigureAwait(false);
            var negotiatedAt = DateTimeOffset.UtcNow;
            var expiresAt = new[] { receipt.ExpiresAt, secret.ExpiresAt }.Min();
            if (!await store.CompleteRealtimeNegotiationAsync(receipt.OwnerPrincipalId, receipt.SessionId,
                receipt.NegotiationGeneration, negotiatedAt, expiresAt, CancellationToken.None).ConfigureAwait(false))
                throw new RealtimeProviderException(409, "realtime_negotiation_expired");
            var result = new RealtimeNegotiationResult(receipt.SessionId, answer, negotiatedAt, expiresAt, maxSeconds);
            var replay = new ReplayEntry(result, DateTimeOffset.UtcNow.AddSeconds(30));
            _replay[receipt.SessionId] = replay;
            _ = ExpireReplayAsync(receipt.SessionId, replay);
            readiness.RecordNegotiationSuccess();
            return result;
        }
        catch (OperationCanceledException)
        {
            await store.FailRealtimeNegotiationAsync(receipt.OwnerPrincipalId, receipt.SessionId,
                receipt.NegotiationGeneration, "provider_timeout", CancellationToken.None).ConfigureAwait(false);
            readiness.RecordNegotiationFailure("provider_timeout");
            throw new RealtimeProviderException(504, "provider_timeout");
        }
        catch (RealtimeProviderException exception)
        {
            await store.FailRealtimeNegotiationAsync(receipt.OwnerPrincipalId, receipt.SessionId,
                receipt.NegotiationGeneration, exception.Code, CancellationToken.None).ConfigureAwait(false);
            readiness.RecordNegotiationFailure(exception.Code);
            throw;
        }
        catch (Exception exception) when (exception is StoreException or IOException or HttpRequestException)
        {
            await store.FailRealtimeNegotiationAsync(receipt.OwnerPrincipalId, receipt.SessionId,
                receipt.NegotiationGeneration, "provider_unavailable", CancellationToken.None).ConfigureAwait(false);
            readiness.RecordNegotiationFailure("provider_unavailable");
            throw new RealtimeProviderException(502, "provider_unavailable");
        }
    }

    public static void ValidateSdp(string value, bool answer)
    {
        if (string.IsNullOrWhiteSpace(value) || Encoding.UTF8.GetByteCount(value) > RealtimeVoiceLimits.MaximumSdpBytes
            || value.Any(character => char.IsControl(character) && character is not '\r' and not '\n')
            || !value.StartsWith("v=0", StringComparison.Ordinal)
            || !value.Contains("m=audio", StringComparison.Ordinal)
            || value.Contains("m=video", StringComparison.Ordinal))
            throw new RealtimeProviderException(answer ? 502 : 422, answer ? "provider_malformed" : "realtime_offer_invalid");
    }

    public static void ValidateIdentifier(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(character => character is < '!' or > '~'))
            throw new ArgumentException("Identifier must contain 1-128 visible ASCII characters.", name);
    }

    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private async Task<(RealtimeFoundrySessionConfiguration Configuration, RealtimeToolProjection Projection)>
        BuildSessionConfigurationAsync(string owner, string conversationId, string sessionId, CancellationToken token)
    {
        const int maximumInstructionBytes = 16 * 1024;
        var prefix = "You are Tessera, continuing the user's current private conversation by voice. "
            + "Be concise, preserve context, and never claim a tool succeeded unless Tessera returns a canonical result.";
        var history = new StringBuilder(prefix);
        var messages = await store.ListMessagesAsync(owner, conversationId, token).ConfigureAwait(false);
        foreach (var message in messages.TakeLast(24))
        {
            var text = string.Join("\n", message.Parts.Where(part => part.Kind == "TEXT" && !string.IsNullOrWhiteSpace(part.Text)).Select(part => part.Text));
            if (string.IsNullOrWhiteSpace(text)) continue;
            var speaker = message.Role == "USER" ? "User" : message.Role == "ASSISTANT" ? "Tessera" : "System";
            var addition = $"\n{speaker}: {text}";
            if (Encoding.UTF8.GetByteCount(history.ToString()) + Encoding.UTF8.GetByteCount(addition) > maximumInstructionBytes) break;
            history.Append(addition);
        }
        var projection = await RealtimeToolProjection.ProjectAsync(
            store, plugins, owner, conversationId, sessionId, token).ConfigureAwait(false);
        return (new(config.RealtimeVoice.Voice, config.RealtimeVoice.TranscriptionModel,
            history.ToString(), projection.Definitions), projection);
    }

    private async Task ExpireReplayAsync(string sessionId, ReplayEntry replay)
    {
        var delay = replay.ValidUntil - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero) await Task.Delay(delay).ConfigureAwait(false);
        if (_replay.TryGetValue(sessionId, out var current) && ReferenceEquals(current, replay))
            _replay.TryRemove(sessionId, out _);
    }

    public void Dispose() => _beginGate.Dispose();
}

internal sealed record RealtimeToolProjection(
    R2ProductEndpoints.ChatToolContext Context,
    IReadOnlyList<RealtimeSessionTool> Tools,
    IReadOnlyList<JsonElement> Definitions)
{
    private sealed record LocalTool(string CapabilityId, string CapabilityVersion, string SideEffectClass);

    private static readonly Dictionary<string, LocalTool> LocalTools =
        new Dictionary<string, LocalTool>(StringComparer.Ordinal)
        {
            ["current_time"] = new("local.time", "1", SideEffectClass.ReadOnly.ToString()),
            ["remember_memory"] = new("local.memory.remember", "1", SideEffectClass.LocalReversible.ToString()),
            ["correct_memory"] = new("local.memory.correct", "1", SideEffectClass.LocalReversible.ToString()),
            ["why_memory"] = new("local.memory.why", "1", SideEffectClass.ReadOnly.ToString()),
        };

    public static async Task<RealtimeToolProjection> ProjectAsync(
        SqliteKernelStore store, TesseraPluginRegistry plugins, string owner,
        string conversationId, string sessionId, CancellationToken token)
    {
        var context = await R2ProductEndpoints.ChatToolsAsync(
            store, owner, conversationId, token, plugins).ConfigureAwait(false);
        var definitions = context.Definitions
            .Select(item => JsonSerializer.SerializeToElement(item))
            .Where(item => item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("name", out var name)
                && !string.IsNullOrWhiteSpace(name.GetString())
                && item.TryGetProperty("parameters", out var parameters)
                && parameters.ValueKind == JsonValueKind.Object)
            .GroupBy(item => item.GetProperty("name").GetString()!, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var tools = new List<RealtimeSessionTool>();
        foreach (var (name, definition) in definitions)
        {
            var schemaHash = RealtimeVoiceService.Hash(definition.GetProperty("parameters").GetRawText());
            if (LocalTools.TryGetValue(name, out var local))
            {
                tools.Add(new(owner, sessionId, name, "local", "1.0.0", local.CapabilityId,
                    local.CapabilityVersion, null, schemaHash, local.SideEffectClass));
                continue;
            }
            var projected = context.PluginTools
                .Where(item => item.Tool.Name == name
                    && (item.Capability.AccountRequired ? item.Accounts.Count == 1 : item.Accounts.Count == 0))
                .ToArray();
            if (projected.Length != 1) continue;
            var plugin = projected[0];
            tools.Add(new(owner, sessionId, name, plugin.PluginId, plugin.PluginVersion,
                plugin.Capability.CapabilityId, plugin.Capability.Version,
                plugin.Capability.AccountRequired ? plugin.Accounts[0].AccountId : null,
                schemaHash, plugin.Capability.SideEffectClass.ToString()));
        }
        var sortedTools = tools.OrderBy(item => item.ExposedName, StringComparer.Ordinal).ToArray();
        var sortedDefinitions = sortedTools.Select(item =>
        {
            var definition = definitions[item.ExposedName];
            return JsonSerializer.SerializeToElement(new
            {
                type = "function",
                name = item.ExposedName,
                description = definition.TryGetProperty("description", out var description)
                    ? description.GetString() ?? string.Empty : string.Empty,
                parameters = definition.GetProperty("parameters").Clone(),
            });
        }).ToArray();
        return new(context, sortedTools, sortedDefinitions);
    }
}