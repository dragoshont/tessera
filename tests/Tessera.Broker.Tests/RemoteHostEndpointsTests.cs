using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Tessera.Core.Product;
using Xunit;

namespace Tessera.Broker.Tests;

public sealed class RemoteHostEndpointsTests : IAsyncLifetime
{
    private const string Owner = "host-owner@example.com";
    private const string Other = "host-other@example.com";
    private const string DevHeader = "X-Tessera-Dev-Principal";

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _directory = null!;

    public async Task InitializeAsync()
    {
        var port = FreePort();
        _directory = Directory.CreateTempSubdirectory("tessera-host-api-test").FullName;
        var configPath = Path.Combine(_directory, "tessera.json");
        await File.WriteAllTextAsync(configPath, $$"""
            {
              "server": { "host": "127.0.0.1", "port": {{port}} },
              "identity": { "mode": "dev", "trustDomain": "tessera.local" },
              "policy": { "default": "deny" },
              "audit": { "enabled": false }
            }
            """);
        var policyPath = Path.Combine(_directory, "grants.json");
        await File.WriteAllTextAsync(policyPath, "{ \"grants\": [], \"bindings\": [], \"recipes\": [] }");
        _app = await BrokerHost.BuildAppAsync(new BrokerHostOptions
        {
            ConfigPath = configPath,
            PolicyPath = policyPath,
            ProductDatabasePath = Path.Combine(_directory, "product.db"),
            PluginRoot = Path.Combine(_directory, "plugins"),
        });
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    }

    [Fact]
    public async Task Pair_claim_confirm_list_update_and_revoke_are_strict_owner_scoped_replayable_and_redacted()
    {
        using var unauthenticated = await _client.GetAsync("/api/v1/hosts");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Equal("no-store", unauthenticated.Headers.CacheControl?.ToString());

        using var empty = await SendAsync(Owner, HttpMethod.Get, "/api/v1/hosts");
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        Assert.Empty((await Json(empty)).GetProperty("items").EnumerateArray());

        using (var oversizedRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/host-pairings"))
        {
            oversizedRequest.Headers.Add(DevHeader, Owner);
            oversizedRequest.Headers.Add("Idempotency-Key", "oversized-host-key");
            oversizedRequest.Content = new StringContent(
                "{\"claimSecretHash\":\"" + new string('a', 64) + "\",\"padding\":\""
                + new string('x', 64 * 1024) + "\"}", Encoding.UTF8, "application/json");
            using var oversized = await _client.SendAsync(oversizedRequest);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        }

        var claimSecret = RemoteHostValidation.CreateClaimSecret();
        var claimSecretHash = RemoteHostValidation.HashClaimSecret(claimSecret);
        using var createBody = await SendJsonAsync(Owner, HttpMethod.Post, "/api/v1/host-pairings",
            new { claimSecretHash, ignored = true }, "create-body-key");
        Assert.Equal(HttpStatusCode.BadRequest, createBody.StatusCode);

        var createRequest = new { claimSecretHash };
        var concurrentCreates = await Task.WhenAll(
            SendJsonAsync(Owner, HttpMethod.Post, "/api/v1/host-pairings", createRequest, "create-host-key"),
            SendJsonAsync(Owner, HttpMethod.Post, "/api/v1/host-pairings", createRequest, "create-host-key"));
        using var created = concurrentCreates[0];
        using var concurrentCreateReplay = concurrentCreates[1];
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdText = await created.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, concurrentCreateReplay.StatusCode);
        Assert.Equal(createdText, await concurrentCreateReplay.Content.ReadAsStringAsync());
        AssertDlp(createdText, claimSecret);
        var createdBody = JsonDocument.Parse(createdText).RootElement;
        var pairingId = createdBody.GetProperty("pairingId").GetString()!;
        Assert.Equal(32, Decode(claimSecret).Length);
        using var createReplay = await SendJsonAsync(Owner, HttpMethod.Post, "/api/v1/host-pairings",
            createRequest, "create-host-key");
        Assert.Equal(createdText, await createReplay.Content.ReadAsStringAsync());
        using var createConflict = await SendJsonAsync(Owner, HttpMethod.Post, "/api/v1/host-pairings",
            new { claimSecretHash = new string('a', 64) }, "create-host-key");
        Assert.Equal(HttpStatusCode.Conflict, createConflict.StatusCode);

        var changedCreateRace = await Task.WhenAll(
            SendJsonAsync(Other, HttpMethod.Post, "/api/v1/host-pairings",
                new { claimSecretHash = new string('c', 64) }, "changed-create-key"),
            SendJsonAsync(Other, HttpMethod.Post, "/api/v1/host-pairings",
                new { claimSecretHash = new string('d', 64) }, "changed-create-key"));
        using var changedCreateFirst = changedCreateRace[0];
        using var changedCreateSecond = changedCreateRace[1];
        Assert.Contains(changedCreateRace, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Contains(changedCreateRace, response => response.StatusCode == HttpStatusCode.Conflict);

        var (jwkObject, normalized) = PublicKey();
        var claimBody = new
        {
            claimSecret,
            publicKeyJwk = jwkObject,
            protection = "KEYCHAIN_THIS_DEVICE_ONLY",
            platform = "macOS",
            architecture = "arm64",
            agentVersion = "1.0.0",
            protocolVersion = "1",
            requestedCapabilities = new[] { new
            {
                capabilityId = "host.repo.identity",
                capabilityVersion = "1",
                schemaHash = new string('a', 64),
                sideEffectClass = "READ_ONLY",
            } },
            requestedResources = new[] { new
            {
                resourceId = "repo-main",
                type = "REPOSITORY",
                displayName = "Tessera",
                fingerprint = new string('b', 64),
                state = "AVAILABLE",
            } },
        };
        using var unknownClaim = await SendJsonAsync(null, HttpMethod.Post, $"/api/v1/host-pairings/{pairingId}/claim",
            new
            {
                claimSecret,
                publicKeyJwk = jwkObject,
                protection = "KEYCHAIN_THIS_DEVICE_ONLY",
                platform = "macOS",
                architecture = "arm64",
                agentVersion = "1.0.0",
                protocolVersion = "1",
                requestedCapabilities = new[] { new
                {
                    capabilityId = "host.repo.identity", capabilityVersion = "1",
                    schemaHash = new string('a', 64), sideEffectClass = "READ_ONLY", executable = "/bin/sh",
                } },
                requestedResources = Array.Empty<object>(),
            }, "claim-unknown-key");
        Assert.Equal(HttpStatusCode.BadRequest, unknownClaim.StatusCode);
        using var missingJwk = await SendJsonAsync(null, HttpMethod.Post, $"/api/v1/host-pairings/{pairingId}/claim",
            new
            {
                claimSecret,
                protection = "KEYCHAIN_THIS_DEVICE_ONLY",
                platform = "macOS",
                architecture = "arm64",
                agentVersion = "1.0.0",
                protocolVersion = "1",
                requestedCapabilities = Array.Empty<object>(),
                requestedResources = Array.Empty<object>(),
            }, "claim-missing-jwk-key");
        Assert.Equal(HttpStatusCode.BadRequest, missingJwk.StatusCode);

        using var claimed = await SendJsonAsync(null, HttpMethod.Post,
            $"/api/v1/host-pairings/{pairingId}/claim", claimBody, "claim-host-key");
        Assert.Equal(HttpStatusCode.Accepted, claimed.StatusCode);
        var claimedBody = await claimed.Content.ReadAsStringAsync();
        Assert.DoesNotContain(claimSecret, claimedBody, StringComparison.Ordinal);
        using var claimReplay = await SendJsonAsync(null, HttpMethod.Post,
            $"/api/v1/host-pairings/{pairingId}/claim", claimBody, "claim-host-key");
        Assert.Equal(claimedBody, await claimReplay.Content.ReadAsStringAsync());

        using var pairing = await SendAsync(Owner, HttpMethod.Get, $"/api/v1/host-pairings/{pairingId}");
        var pairingBody = await pairing.Content.ReadAsStringAsync();
        Assert.DoesNotContain(claimSecret, pairingBody, StringComparison.Ordinal);
        Assert.DoesNotContain("publicKey", pairingBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("claimSecretHash", pairingBody, StringComparison.OrdinalIgnoreCase);
        var pairingJson = JsonDocument.Parse(pairingBody).RootElement;
        var version = pairingJson.GetProperty("version").GetInt64();
        var confirmationCode = RemoteHostValidation.DeriveConfirmationCode(pairingId, normalized);
        var confirmBody = new
        {
            expectedVersion = version,
            confirmationCode,
            displayName = "My Mac",
            capabilityGrants = new[] { new { capabilityId = "host.repo.identity", capabilityVersion = "1" } },
            resourceGrants = new[] { new { resourceId = "repo-main", accessMode = "READ_ONLY" } },
        };
        using var otherConfirm = await SendJsonAsync(Other, HttpMethod.Post,
            $"/api/v1/host-pairings/{pairingId}/confirm", confirmBody, "other-confirm-key");
        Assert.Equal(HttpStatusCode.NotFound, otherConfirm.StatusCode);

        var concurrentConfirms = await Task.WhenAll(
            SendJsonAsync(Owner, HttpMethod.Post,
                $"/api/v1/host-pairings/{pairingId}/confirm", confirmBody, "confirm-host-key"),
            SendJsonAsync(Owner, HttpMethod.Post,
                $"/api/v1/host-pairings/{pairingId}/confirm", confirmBody, "confirm-host-key"));
        using var confirmed = concurrentConfirms[0];
        using var concurrentConfirmReplay = concurrentConfirms[1];
        Assert.Equal(HttpStatusCode.Created, confirmed.StatusCode);
        var confirmedText = await confirmed.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, concurrentConfirmReplay.StatusCode);
        Assert.Equal(confirmedText, await concurrentConfirmReplay.Content.ReadAsStringAsync());
        AssertDlp(confirmedText, claimSecret);
        var confirmedJson = JsonDocument.Parse(confirmedText).RootElement;
        var hostId = confirmedJson.GetProperty("host").GetProperty("hostId").GetString()!;
        var hostVersion = confirmedJson.GetProperty("host").GetProperty("version").GetInt64();
        using var confirmReplay = await SendJsonAsync(Owner, HttpMethod.Post,
            $"/api/v1/host-pairings/{pairingId}/confirm", confirmBody, "confirm-host-key");
        Assert.Equal(confirmedText, await confirmReplay.Content.ReadAsStringAsync());
        using var changedReplay = await SendJsonAsync(Owner, HttpMethod.Post,
            $"/api/v1/host-pairings/{pairingId}/confirm", confirmBody with { displayName = "Changed Mac" }, "confirm-host-key");
        Assert.Equal(HttpStatusCode.Conflict, changedReplay.StatusCode);

        using var otherHost = await SendAsync(Other, HttpMethod.Get, $"/api/v1/hosts/{hostId}");
        Assert.Equal(HttpStatusCode.NotFound, otherHost.StatusCode);
        using var listed = await SendAsync(Owner, HttpMethod.Get, "/api/v1/hosts");
        Assert.Single((await Json(listed)).GetProperty("items").EnumerateArray());

        var grantsBody = new
        {
            expectedVersion = hostVersion,
            capabilityGrants = Array.Empty<object>(),
            resourceGrants = Array.Empty<object>(),
        };
        using var updated = await SendJsonAsync(Owner, HttpMethod.Put,
            $"/api/v1/hosts/{hostId}/grants", grantsBody, "update-host-key");
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var updatedText = await updated.Content.ReadAsStringAsync();
        var updatedVersion = JsonDocument.Parse(updatedText).RootElement.GetProperty("host").GetProperty("version").GetInt64();
        using var updateReplay = await SendJsonAsync(Owner, HttpMethod.Put,
            $"/api/v1/hosts/{hostId}/grants", grantsBody, "update-host-key");
        Assert.Equal(updatedText, await updateReplay.Content.ReadAsStringAsync());
        using var updateTargetConflict = await SendJsonAsync(Owner, HttpMethod.Put,
            "/api/v1/hosts/host-substitution/grants", grantsBody, "update-host-key");
        Assert.Equal(HttpStatusCode.Conflict, updateTargetConflict.StatusCode);
        using var updateConflict = await SendJsonAsync(Owner, HttpMethod.Put,
            $"/api/v1/hosts/{hostId}/grants", grantsBody with { expectedVersion = updatedVersion }, "update-host-key");
        Assert.Equal(HttpStatusCode.Conflict, updateConflict.StatusCode);

        var revokeBody = new { expectedVersion = updatedVersion };
        using var revoked = await SendJsonAsync(Owner, HttpMethod.Post,
            $"/api/v1/hosts/{hostId}/revoke", revokeBody, "revoke-host-key");
        Assert.Equal(HttpStatusCode.Accepted, revoked.StatusCode);
        var revokedText = await revoked.Content.ReadAsStringAsync();
        Assert.Contains("REVOKED", revokedText, StringComparison.Ordinal);
        AssertDlp(revokedText, claimSecret);
        using var revokeTargetConflict = await SendJsonAsync(Owner, HttpMethod.Post,
            "/api/v1/hosts/host-substitution/revoke", revokeBody, "revoke-host-key");
        Assert.Equal(HttpStatusCode.Conflict, revokeTargetConflict.StatusCode);
    }

    [Fact]
    public async Task Reused_claim_hash_and_public_key_return_stable_receipted_conflicts()
    {
        var firstSecret = RemoteHostValidation.CreateClaimSecret();
        var firstHash = RemoteHostValidation.HashClaimSecret(firstSecret);
        using var firstCreated = await SendJsonAsync(Owner, HttpMethod.Post, "/api/v1/host-pairings",
            new { claimSecretHash = firstHash }, "first-pairing");
        var firstPairingId = (await Json(firstCreated)).GetProperty("pairingId").GetString()!;

        using var reused = await SendJsonAsync(Owner, HttpMethod.Post, "/api/v1/host-pairings",
            new { claimSecretHash = firstHash }, "reused-hash");
        Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);
        var reusedBody = await reused.Content.ReadAsStringAsync();
        using var reusedReplay = await SendJsonAsync(Owner, HttpMethod.Post, "/api/v1/host-pairings",
            new { claimSecretHash = firstHash }, "reused-hash");
        Assert.Equal(HttpStatusCode.Conflict, reusedReplay.StatusCode);
        Assert.Equal(reusedBody, await reusedReplay.Content.ReadAsStringAsync());
        using var reusedChanged = await SendJsonAsync(Owner, HttpMethod.Post, "/api/v1/host-pairings",
            new { claimSecretHash = new string('c', 64) }, "reused-hash");
        Assert.Equal(HttpStatusCode.Conflict, reusedChanged.StatusCode);

        var (jwkObject, normalized) = PublicKey();
        object ClaimBody(string secret) => new
        {
            claimSecret = secret,
            publicKeyJwk = jwkObject,
            protection = "KEYCHAIN_THIS_DEVICE_ONLY",
            platform = "macOS",
            architecture = "arm64",
            agentVersion = "1.0.0",
            protocolVersion = "1",
            requestedCapabilities = Array.Empty<object>(),
            requestedResources = Array.Empty<object>(),
        };
        using var firstClaimed = await SendJsonAsync(null, HttpMethod.Post,
            $"/api/v1/host-pairings/{firstPairingId}/claim", ClaimBody(firstSecret), "first-claim");
        var firstClaimedJson = await Json(firstClaimed);
        var firstVersion = firstClaimedJson.GetProperty("version").GetInt64();
        var firstConfirmBody = new
        {
            expectedVersion = firstVersion,
            confirmationCode = RemoteHostValidation.DeriveConfirmationCode(firstPairingId, normalized),
            displayName = "First Mac",
            capabilityGrants = Array.Empty<object>(),
            resourceGrants = Array.Empty<object>(),
        };
        using var firstConfirmed = await SendJsonAsync(Owner, HttpMethod.Post,
            $"/api/v1/host-pairings/{firstPairingId}/confirm", firstConfirmBody, "first-confirm");
        Assert.Equal(HttpStatusCode.Created, firstConfirmed.StatusCode);

        var secondSecret = RemoteHostValidation.CreateClaimSecret();
        using var secondCreated = await SendJsonAsync(Owner, HttpMethod.Post, "/api/v1/host-pairings",
            new { claimSecretHash = RemoteHostValidation.HashClaimSecret(secondSecret) }, "second-pairing");
        var secondPairingId = (await Json(secondCreated)).GetProperty("pairingId").GetString()!;
        using var claimTargetConflict = await SendJsonAsync(null, HttpMethod.Post,
            $"/api/v1/host-pairings/{secondPairingId}/claim", ClaimBody(firstSecret), "first-claim");
        Assert.Equal(HttpStatusCode.Conflict, claimTargetConflict.StatusCode);
        using var secondClaimed = await SendJsonAsync(null, HttpMethod.Post,
            $"/api/v1/host-pairings/{secondPairingId}/claim", ClaimBody(secondSecret), "second-claim");
        var secondVersion = (await Json(secondClaimed)).GetProperty("version").GetInt64();
        using var confirmTargetConflict = await SendJsonAsync(Owner, HttpMethod.Post,
            $"/api/v1/host-pairings/{secondPairingId}/confirm", firstConfirmBody, "first-confirm");
        Assert.Equal(HttpStatusCode.Conflict, confirmTargetConflict.StatusCode);
        var secondConfirmBody = new
        {
            expectedVersion = secondVersion,
            confirmationCode = RemoteHostValidation.DeriveConfirmationCode(secondPairingId, normalized),
            displayName = "Second Mac",
            capabilityGrants = Array.Empty<object>(),
            resourceGrants = Array.Empty<object>(),
        };
        using var duplicateKey = await SendJsonAsync(Owner, HttpMethod.Post,
            $"/api/v1/host-pairings/{secondPairingId}/confirm", secondConfirmBody, "duplicate-key-confirm");
        Assert.Equal(HttpStatusCode.Conflict, duplicateKey.StatusCode);
        var duplicateBody = await duplicateKey.Content.ReadAsStringAsync();
        using var duplicateReplay = await SendJsonAsync(Owner, HttpMethod.Post,
            $"/api/v1/host-pairings/{secondPairingId}/confirm", secondConfirmBody, "duplicate-key-confirm");
        Assert.Equal(HttpStatusCode.Conflict, duplicateReplay.StatusCode);
        Assert.Equal(duplicateBody, await duplicateReplay.Content.ReadAsStringAsync());
        using var hosts = await SendAsync(Owner, HttpMethod.Get, "/api/v1/hosts");
        Assert.Single((await Json(hosts)).GetProperty("items").EnumerateArray());

        using var cancelFirst = await SendJsonAsync(Owner, HttpMethod.Post, "/api/v1/host-pairings",
            new { claimSecretHash = new string('d', 64) }, "cancel-first-create");
        var cancelFirstId = (await Json(cancelFirst)).GetProperty("pairingId").GetString()!;
        using var cancelSecond = await SendJsonAsync(Owner, HttpMethod.Post, "/api/v1/host-pairings",
            new { claimSecretHash = new string('e', 64) }, "cancel-second-create");
        var cancelSecondId = (await Json(cancelSecond)).GetProperty("pairingId").GetString()!;
        var cancelBody = new { expectedVersion = 1 };
        using var canceled = await SendJsonAsync(Owner, HttpMethod.Post,
            $"/api/v1/host-pairings/{cancelSecondId}/cancel", cancelBody, "cancel-target-key");
        Assert.Equal(HttpStatusCode.OK, canceled.StatusCode);
        using var cancelTargetConflict = await SendJsonAsync(Owner, HttpMethod.Post,
            $"/api/v1/host-pairings/{cancelFirstId}/cancel", cancelBody, "cancel-target-key");
        Assert.Equal(HttpStatusCode.Conflict, cancelTargetConflict.StatusCode);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private async Task<HttpResponseMessage> SendJsonAsync(string? owner, HttpMethod method, string path, object body, string key)
    {
        var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        if (owner is not null) request.Headers.Add(DevHeader, owner);
        request.Headers.Add("Idempotency-Key", key);
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendAsync(string owner, HttpMethod method, string path, string? key = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(DevHeader, owner);
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return await _client.SendAsync(request);
    }

    private static (object Jwk, P256PublicJwk Normalized) PublicKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(false);
        var jwk = new { kty = "EC", crv = "P-256", x = Base64Url(parameters.Q.X!), y = Base64Url(parameters.Q.Y!), alg = "ES256" };
        return (jwk, RemoteHostValidation.NormalizeP256PublicJwk(JsonSerializer.Serialize(jwk)));
    }

    private static void AssertDlp(string body, string secret)
    {
        Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
        Assert.DoesNotContain("claimSecret", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("publicKey", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("signature", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/Users/", body, StringComparison.Ordinal);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static byte[] Decode(string value)
        => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + "=");

    private static string Base64Url(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}