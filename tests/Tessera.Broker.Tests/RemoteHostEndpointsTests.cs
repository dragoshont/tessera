using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Tessera.Core.Kernel;
using Tessera.Persistence.Sqlite;
using Tessera.Core.Stores;
using Tessera.Providers;
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

    [Fact]
    public async Task Execution_policy_routes_round_trip_and_delete_with_optimistic_versioning()
    {
        var store = _app.Services.GetRequiredService<SqliteKernelStore>();
        var ownerId = PrincipalRef.Create("https://dev.tessera.local", "dev", Owner, Owner, DateTimeOffset.UtcNow).PrincipalId;
        await store.AddAsync(PrincipalRef.Create("https://dev.tessera.local", "dev", Owner, Owner, DateTimeOffset.UtcNow));
        var now = DateTimeOffset.UtcNow;
        await store.AddJobAsync(new(ownerId, "job-host", "Host job", "Inspect repo", "ACTIVE", "READY", null,
            new JobSchedule("once", now, null, "UTC", null), null, "{}", [], [("host.repo.identity", "1")], [], now, now, 1));

        using var defaultPolicy = await SendAsync(Owner, HttpMethod.Get, "/api/v1/jobs/job-host/execution-policy");
        Assert.Equal(HttpStatusCode.OK, defaultPolicy.StatusCode);
        Assert.Equal("SERVER", (await Json(defaultPolicy)).GetProperty("location").GetString());

        var putBody = new
        {
            expectedVersion = 0,
            location = "ANY_COMPATIBLE_HOST",
            preferredHostId = (string?)null,
            requiredCapabilities = new[] { new { capabilityId = "host.repo.identity", capabilityVersion = "1" } },
            requiredResourceIds = new[] { "repo-main" },
            fallbackPolicy = "NONE",
        };
        using var updated = await SendJsonAsync(Owner, HttpMethod.Put, "/api/v1/jobs/job-host/execution-policy", putBody, "policy-put-key");
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var updatedText = await updated.Content.ReadAsStringAsync();
        var updatedJson = await Json(updated);
        Assert.Equal("ANY_COMPATIBLE_HOST", updatedJson.GetProperty("location").GetString());
        Assert.Equal(1L, updatedJson.GetProperty("version").GetInt64());
        using var putReplay = await SendJsonAsync(Owner, HttpMethod.Put, "/api/v1/jobs/job-host/execution-policy", putBody, "policy-put-key");
        Assert.Equal(updatedText, await putReplay.Content.ReadAsStringAsync());
        using var putConflict = await SendJsonAsync(Owner, HttpMethod.Put, "/api/v1/jobs/job-host/execution-policy",
            putBody with { requiredResourceIds = new[] { "repo-other" } }, "policy-put-key");
        Assert.Equal(HttpStatusCode.Conflict, putConflict.StatusCode);

        var deleteBody = new
        {
            expectedVersion = 1,
        };
        using var deleted = await SendJsonAsync(Owner, HttpMethod.Delete, "/api/v1/jobs/job-host/execution-policy", deleteBody, "policy-delete-key");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        var deletedText = await deleted.Content.ReadAsStringAsync();
        Assert.Equal("SERVER", (await Json(deleted)).GetProperty("location").GetString());
        using var deleteReplay = await SendJsonAsync(Owner, HttpMethod.Delete, "/api/v1/jobs/job-host/execution-policy", deleteBody, "policy-delete-key");
        Assert.Equal(deletedText, await deleteReplay.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Signed_host_channel_poll_ack_event_and_complete_flow_updates_the_canonical_run()
    {
        var store = _app.Services.GetRequiredService<SqliteKernelStore>();
        var ownerPrincipal = PrincipalRef.Create("https://dev.tessera.local", "dev", Owner, Owner, DateTimeOffset.UtcNow);
        await store.AddAsync(ownerPrincipal);
        await SeedOnlineHostAsync(store, ownerPrincipal.PrincipalId, "host-main", "repo-main");
        var now = DateTimeOffset.UtcNow;
        await store.AddJobAsync(new(ownerPrincipal.PrincipalId, "job-host-run", "Host job", "Inspect repo", "ACTIVE", "READY", null,
            new JobSchedule("once", now, null, "UTC", null), null, "{}", [], [("host.repo.identity", "1")], [], now, now, 1));
        await store.PutJobExecutionPolicyAsync(new(
            ownerPrincipal.PrincipalId,
            "job-host-run",
            JobExecutionLocations.Host,
            "host-main",
            [(RemoteHostValidation.SupportedCapabilityId, RemoteHostValidation.SupportedCapabilityVersion)],
            ["repo-main"],
            JobExecutionFallbackPolicies.None,
            1), 0);
        var run = await store.CreateRunOccurrenceAsync(ownerPrincipal.PrincipalId, "job-host-run", now);
        Assert.NotNull(run);
        await new R2SchedulerService(store, new InMemoryCredentialStore(), new NoopTransport(), NullLogger<R2SchedulerService>.Instance)
            .DispatchQueuedAsync(CancellationToken.None);
        var projection = await store.GetRemoteJobRunProjectionAsync(ownerPrincipal.PrincipalId, run!.RunId);
        Assert.NotNull(projection?.Lease);
        var lease = projection!.Lease!;

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await SetHostKeyAsync(ownerPrincipal.PrincipalId, "host-main", key);

        var pollBody = new { maxWaitSeconds = 1, activeAttempt = (object?)null };
        using var poll = await SendSignedAsync(key, "host-main", HostAcceptedMessageOperations.Poll, "-", "/host-channel/poll", pollBody, "message-poll", 1, now.ToUnixTimeSeconds());
        Assert.Equal(HttpStatusCode.OK, poll.StatusCode);
        var pollText = await poll.Content.ReadAsStringAsync();
        Assert.Contains(lease.LeaseId, pollText, StringComparison.Ordinal);
        using var pollReplay = await SendSignedAsync(key, "host-main", HostAcceptedMessageOperations.Poll, "-", "/host-channel/poll", pollBody, "message-poll", 1, now.ToUnixTimeSeconds());
        Assert.Equal(pollText, await pollReplay.Content.ReadAsStringAsync());

        var localAttemptId = "attempt-main";
        using var ack = await SendSignedAsync(key, "host-main", HostAcceptedMessageOperations.LeaseAck, lease.LeaseId, $"/host-channel/leases/{lease.LeaseId}/ack", new
        {
            leaseVersion = lease.Version,
            localAttemptId,
            accepted = true,
            rejectionCode = (string?)null,
        }, "message-ack", 2, now.ToUnixTimeSeconds() + 1);
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
        Assert.Equal("RUNNING", (await store.GetJobRunAsync(ownerPrincipal.PrincipalId, run.RunId))!.State);

        using var events = await SendSignedAsync(key, "host-main", HostAcceptedMessageOperations.LeaseEvents, lease.LeaseId, $"/host-channel/leases/{lease.LeaseId}/events", new
        {
            leaseVersion = lease.Version + 1,
            localAttemptId,
            events = new[] { new { eventId = "event-1", sequence = 1, type = "STEP_STARTED", occurredAt = now.AddSeconds(2), summary = "started", data = new { phase = "inspect" } } },
        }, "message-events", 3, now.ToUnixTimeSeconds() + 2);
        Assert.Equal(HttpStatusCode.OK, events.StatusCode);

        const string output = "branch=main commit=0123456789abcdef0123456789abcdef01234567";
        var outputSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(output)));
        using var complete = await SendSignedAsync(key, "host-main", HostAcceptedMessageOperations.LeaseComplete, lease.LeaseId, $"/host-channel/leases/{lease.LeaseId}/complete", new
        {
            leaseVersion = lease.Version + 2,
            outcome = "SUCCEEDED",
            output,
            outputSha256,
            truncated = false,
            localAttemptId,
        }, "message-complete", 4, now.ToUnixTimeSeconds() + 3);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        Assert.Equal("SUCCEEDED", (await store.GetJobRunAsync(ownerPrincipal.PrincipalId, run.RunId))!.State);
    }

    [Fact]
    public async Task Signed_host_artifact_upload_projection_list_detail_and_verify_are_redacted_replayable_and_owner_scoped()
    {
        var (store, ownerPrincipal, run, lease) = await CreateDispatchedLeaseAsync("artifact-owner", "host-artifact", "job-artifact");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await SetHostKeyAsync(ownerPrincipal.PrincipalId, "host-artifact", key);
        var now = DateTimeOffset.UtcNow;
        const string localAttemptId = "attempt-artifact";

        using var ack = await SendSignedAsync(key, "host-artifact", HostAcceptedMessageOperations.LeaseAck,
            lease.LeaseId, $"/host-channel/leases/{lease.LeaseId}/ack", new
            {
                leaseVersion = lease.Version,
                localAttemptId,
                accepted = true,
                rejectionCode = (string?)null,
            }, "message-artifact-ack", 1, now.ToUnixTimeSeconds());
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);

        const string rawContent = "Authorization: Bearer canary-token\nPATH=/Users/dragoshont/Repo/tessera\nSIGNATURE=abcdef\nSECRET_TOKEN=visible-secret\n{\"password\":\"json-canary\",\"escaped_secret\":\"do\\\"escaped-canary\",\"privateKey\":\"camel-key-canary\",\"private_key\":\"snake-key-canary\",\"path\":\"/Volumes/External/repo\"}\npassword: \"bare-secret with suffix\"\nmount /Volumes/External/repo\nsystem /System/Volumes/Data/repo\nbinary /usr/local/bin/tool";
        var normalized = RemoteHostOutputNormalizer.Normalize(Encoding.UTF8.GetBytes(rawContent), RemoteHostProtocol.MaximumArtifactBodyBytes);
        using var upload = await SendSignedAsync(key, "host-artifact", HostAcceptedMessageOperations.LeaseArtifact,
            lease.LeaseId, $"/host-channel/leases/{lease.LeaseId}/artifacts", new
            {
                leaseVersion = lease.Version + 1,
                localAttemptId,
                artifactId = "artifact-main",
                kind = "TEXT",
                mediaType = "text/plain",
                summary = "path=/Users/dragoshont/Repo/tessera",
                declaredSize = normalized.SizeBytes,
                declaredSha256 = normalized.Sha256,
                retention = "RUN",
                textContent = rawContent,
            }, "message-artifact-upload", 2, now.ToUnixTimeSeconds() + 1);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var uploadText = await upload.Content.ReadAsStringAsync();
        AssertDlp(uploadText, "canary-token");

        using var uploadReplay = await SendSignedAsync(key, "host-artifact", HostAcceptedMessageOperations.LeaseArtifact,
            lease.LeaseId, $"/host-channel/leases/{lease.LeaseId}/artifacts", new
            {
                leaseVersion = lease.Version + 1,
                localAttemptId,
                artifactId = "artifact-main",
                kind = "TEXT",
                mediaType = "text/plain",
                summary = "path=/Users/dragoshont/Repo/tessera",
                declaredSize = normalized.SizeBytes,
                declaredSha256 = normalized.Sha256,
                retention = "RUN",
                textContent = rawContent,
            }, "message-artifact-upload", 2, now.ToUnixTimeSeconds() + 1);
        Assert.Equal(uploadText, await uploadReplay.Content.ReadAsStringAsync());

        using var changedReplay = await SendSignedAsync(key, "host-artifact", HostAcceptedMessageOperations.LeaseArtifact,
            lease.LeaseId, $"/host-channel/leases/{lease.LeaseId}/artifacts", new
            {
                leaseVersion = lease.Version + 1,
                localAttemptId,
                artifactId = "artifact-main",
                kind = "TEXT",
                mediaType = "text/plain",
                summary = "changed",
                declaredSize = normalized.SizeBytes,
                declaredSha256 = normalized.Sha256,
                retention = "RUN",
                textContent = rawContent,
            }, "message-artifact-upload", 2, now.ToUnixTimeSeconds() + 1);
        Assert.Equal(HttpStatusCode.Conflict, changedReplay.StatusCode);
        Assert.Contains("host_replay", await changedReplay.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var artifactConflict = await SendSignedAsync(key, "host-artifact", HostAcceptedMessageOperations.LeaseArtifact,
            lease.LeaseId, $"/host-channel/leases/{lease.LeaseId}/artifacts", new
            {
                leaseVersion = lease.Version + 1,
                localAttemptId,
                artifactId = "artifact-main",
                kind = "TEXT",
                mediaType = "text/plain",
                summary = "second-attempt",
                declaredSize = normalized.SizeBytes,
                declaredSha256 = normalized.Sha256,
                retention = "RUN",
                textContent = rawContent,
            }, "message-artifact-conflict", 3, now.ToUnixTimeSeconds() + 2);
        Assert.Equal(HttpStatusCode.Conflict, artifactConflict.StatusCode);
        Assert.Contains("artifact_conflict", await artifactConflict.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Assert.Equal(1L, await ReadHostArtifactCountAsync(ownerPrincipal.PrincipalId, "artifact-main"));
        Assert.Equal(1L, await ReadHostArtifactReceiptCountAsync(ownerPrincipal.PrincipalId, "artifact-main"));
        Assert.Equal(0L, await ReadHostArtifactEvidenceCountAsync(ownerPrincipal.PrincipalId, "artifact-main"));
        var persistedContent = await ReadHostArtifactContentAsync(ownerPrincipal.PrincipalId, "artifact-main");
        Assert.DoesNotContain("canary-token", persistedContent, StringComparison.Ordinal);
        Assert.DoesNotContain("visible-secret", persistedContent, StringComparison.Ordinal);
        Assert.DoesNotContain("json-canary", persistedContent, StringComparison.Ordinal);
        Assert.DoesNotContain("escaped-canary", persistedContent, StringComparison.Ordinal);
        Assert.DoesNotContain("bare-secret", persistedContent, StringComparison.Ordinal);
        Assert.DoesNotContain("camel-key-canary", persistedContent, StringComparison.Ordinal);
        Assert.DoesNotContain("snake-key-canary", persistedContent, StringComparison.Ordinal);
        Assert.DoesNotContain("/Users/dragoshont/Repo/tessera", persistedContent, StringComparison.Ordinal);
        Assert.DoesNotContain("/Volumes/External/repo", persistedContent, StringComparison.Ordinal);
        Assert.DoesNotContain("/System/Volumes/Data/repo", persistedContent, StringComparison.Ordinal);
        Assert.DoesNotContain("/usr/local/bin/tool", persistedContent, StringComparison.Ordinal);
        Assert.DoesNotContain("SIGNATURE", persistedContent, StringComparison.OrdinalIgnoreCase);

        using var projection = await SendAsync("artifact-owner", HttpMethod.Get, $"/api/v1/job-runs/{run.RunId}/remote");
        Assert.Single((await Json(projection)).GetProperty("artifacts").EnumerateArray());

        using var listed = await SendAsync("artifact-owner", HttpMethod.Get, $"/api/v1/job-runs/{run.RunId}/remote-artifacts");
        var listedJson = await Json(listed);
        Assert.Single(listedJson.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, listedJson.GetProperty("nextCursor").ValueKind);
        Assert.DoesNotContain("/Users/dragoshont/Repo/tessera", listedJson.GetRawText(), StringComparison.Ordinal);

        using var crossOwnerList = await SendAsync(Other, HttpMethod.Get, $"/api/v1/job-runs/{run.RunId}/remote-artifacts");
        Assert.Equal(HttpStatusCode.NotFound, crossOwnerList.StatusCode);

        using var detail = await SendAsync("artifact-owner", HttpMethod.Get, "/api/v1/host-artifacts/artifact-main");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var detailText = await detail.Content.ReadAsStringAsync();
        AssertDlp(detailText, "canary-token");
        Assert.Contains("[REDACTED]", detailText, StringComparison.Ordinal);

        using var crossOwnerDetail = await SendAsync(Other, HttpMethod.Get, "/api/v1/host-artifacts/artifact-main");
        Assert.Equal(HttpStatusCode.NotFound, crossOwnerDetail.StatusCode);

        using var verify = await SendJsonAsync("artifact-owner", HttpMethod.Post,
            "/api/v1/host-artifacts/artifact-main/verify", new { expectedVersion = 1 }, "artifact-verify-key");
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        var verifyText = await verify.Content.ReadAsStringAsync();
        Assert.Contains("evidence:host-artifact:artifact-main", verifyText, StringComparison.Ordinal);
        Assert.Equal(1L, await ReadHostArtifactEvidenceCountAsync(ownerPrincipal.PrincipalId, "artifact-main"));

        using var verifyReplay = await SendJsonAsync("artifact-owner", HttpMethod.Post,
            "/api/v1/host-artifacts/artifact-main/verify", new { expectedVersion = 1 }, "artifact-verify-key");
        Assert.Equal(verifyText, await verifyReplay.Content.ReadAsStringAsync());

        using var staleVerify = await SendJsonAsync("artifact-owner", HttpMethod.Post,
            "/api/v1/host-artifacts/artifact-main/verify", new { expectedVersion = 2 }, "artifact-verify-stale");
        Assert.Equal(HttpStatusCode.Conflict, staleVerify.StatusCode);

        using var crossOwnerVerify = await SendJsonAsync(Other, HttpMethod.Post,
            "/api/v1/host-artifacts/artifact-main/verify", new { expectedVersion = 1 }, "artifact-verify-other");
        Assert.Equal(HttpStatusCode.NotFound, crossOwnerVerify.StatusCode);

        using var malformedDetail = await SendAsync("artifact-owner", HttpMethod.Get, "/api/v1/host-artifacts/INVALID");
        Assert.Equal(HttpStatusCode.BadRequest, malformedDetail.StatusCode);
        using var malformedVerify = await SendJsonAsync("artifact-owner", HttpMethod.Post,
            "/api/v1/host-artifacts/INVALID/verify", new { expectedVersion = 1 }, "artifact-verify-malformed");
        Assert.Equal(HttpStatusCode.BadRequest, malformedVerify.StatusCode);
    }

    [Fact]
    public async Task Artifact_verification_requires_canonical_evidence_identity_upload_provenance_and_atomic_commit()
    {
        const string ownerSubject = "artifact-integrity-owner";
        const string hostId = "host-artifact-integrity";
        var (store, ownerPrincipal, _, lease) = await CreateDispatchedLeaseAsync(ownerSubject, hostId, "job-artifact-integrity");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await SetHostKeyAsync(ownerPrincipal.PrincipalId, hostId, key);
        var now = DateTimeOffset.UtcNow;
        const string localAttemptId = "attempt-artifact-integrity";

        using var ack = await SendSignedAsync(key, hostId, HostAcceptedMessageOperations.LeaseAck,
            lease.LeaseId, $"/host-channel/leases/{lease.LeaseId}/ack", new
            {
                leaseVersion = lease.Version,
                localAttemptId,
                accepted = true,
                rejectionCode = (string?)null,
            }, "message-artifact-integrity-ack", 1, now.ToUnixTimeSeconds());
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);

        var normalized = RemoteHostOutputNormalizer.Normalize(
            Encoding.UTF8.GetBytes("integrity content"),
            RemoteHostProtocol.MaximumArtifactBodyBytes);

        async Task UploadAsync(string artifactId, string messageId, long sequence)
        {
            using var upload = await SendSignedAsync(key, hostId, HostAcceptedMessageOperations.LeaseArtifact,
                lease.LeaseId, $"/host-channel/leases/{lease.LeaseId}/artifacts", new
                {
                    leaseVersion = lease.Version + 1,
                    localAttemptId,
                    artifactId,
                    kind = "TEXT",
                    mediaType = "text/plain",
                    summary = "integrity",
                    declaredSize = normalized.SizeBytes,
                    declaredSha256 = normalized.Sha256,
                    retention = "RUN",
                    textContent = "integrity content",
                }, messageId, sequence, now.ToUnixTimeSeconds() + sequence);
            Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        }

        await UploadAsync("artifact-existing-evidence", "message-artifact-existing-evidence", 2);
        var existingArtifact = (await store.GetHostArtifactDetailAsync(
            ownerPrincipal.PrincipalId,
            "artifact-existing-evidence"))!.Artifact;
        var existingEvidence = EvidenceRecord.Create(
            "evidence-existing-artifact",
            ownerPrincipal.PrincipalId,
            "host.artifact",
            existingArtifact.ArtifactId,
            $"host-artifact:{existingArtifact.ArtifactId}",
            now,
            existingArtifact.CreatedAt,
            "SHA-256",
            1,
            existingArtifact.Sha256,
            RetentionState.Active,
            SensitivityClass.Confidential,
            ProducerRef.Create("remote-host", "1"),
            1,
            contentReference: existingArtifact.ArtifactId);
        await store.AddAsync(ownerPrincipal.PrincipalId, existingEvidence);

        using var reuse = await SendJsonAsync(ownerSubject, HttpMethod.Post,
            "/api/v1/host-artifacts/artifact-existing-evidence/verify",
            new { expectedVersion = 1 },
            "verify-existing-evidence");
        Assert.Equal(HttpStatusCode.OK, reuse.StatusCode);
        Assert.Contains(existingEvidence.EvidenceId, await reuse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await UploadAsync("artifact-evidence-collision", "message-artifact-evidence-collision", 3);
        await store.AddAsync(ownerPrincipal.PrincipalId, EvidenceRecord.Create(
            "evidence:host-artifact:artifact-evidence-collision",
            ownerPrincipal.PrincipalId,
            "unrelated.source",
            "unrelated-native-id",
            "unrelated:locator",
            now,
            null,
            "SHA-256",
            1,
            new string('a', 64),
            RetentionState.Active,
            SensitivityClass.Confidential,
            ProducerRef.Create("test", "1"),
            1));
        using var collision = await SendJsonAsync(ownerSubject, HttpMethod.Post,
            "/api/v1/host-artifacts/artifact-evidence-collision/verify",
            new { expectedVersion = 1 },
            "verify-evidence-collision");
        Assert.Equal(HttpStatusCode.Conflict, collision.StatusCode);
        Assert.Equal(0L, await ReadHostArtifactEvidenceCountAsync(
            ownerPrincipal.PrincipalId,
            "artifact-evidence-collision"));

        await UploadAsync("artifact-missing-receipt", "message-artifact-missing-receipt", 4);
        await DeleteHostArtifactReceiptAsync(ownerPrincipal.PrincipalId, "artifact-missing-receipt");
        using var missingReceipt = await SendJsonAsync(ownerSubject, HttpMethod.Post,
            "/api/v1/host-artifacts/artifact-missing-receipt/verify",
            new { expectedVersion = 1 },
            "verify-missing-receipt");
        Assert.Equal(HttpStatusCode.Conflict, missingReceipt.StatusCode);
        Assert.Equal(0L, await ReadHostArtifactEvidenceCountAsync(
            ownerPrincipal.PrincipalId,
            "artifact-missing-receipt"));

        await UploadAsync("artifact-verify-rollback", "message-artifact-verify-rollback", 5);
        typeof(SqliteKernelStore)
            .GetProperty("RemoteHostBeforeCommitAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(store, (Func<CancellationToken, Task>)(_ => throw new InvalidOperationException("injected-before-commit")));
        using var rollback = await SendJsonAsync(ownerSubject, HttpMethod.Post,
            "/api/v1/host-artifacts/artifact-verify-rollback/verify",
            new { expectedVersion = 1 },
            "verify-artifact-rollback");
        typeof(SqliteKernelStore)
            .GetProperty("RemoteHostBeforeCommitAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(store, null);
        Assert.Equal(HttpStatusCode.InternalServerError, rollback.StatusCode);
        Assert.Equal(0L, await ReadHostArtifactEvidenceCountAsync(
            ownerPrincipal.PrincipalId,
            "artifact-verify-rollback"));

        using var retry = await SendJsonAsync(ownerSubject, HttpMethod.Post,
            "/api/v1/host-artifacts/artifact-verify-rollback/verify",
            new { expectedVersion = 1 },
            "verify-artifact-rollback");
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(1L, await ReadHostArtifactEvidenceCountAsync(
            ownerPrincipal.PrincipalId,
            "artifact-verify-rollback"));
    }

    [Fact]
    public async Task Artifact_message_ids_are_independent_per_host_for_the_same_owner()
    {
        var (store, ownerPrincipal, _, firstLease) = await CreateDispatchedLeaseAsync(
            "artifact-message-owner",
            "host-message-first",
            "job-message-first");
        var (_, secondLease) = await CreateDispatchedLeaseForOwnerAsync(
            store,
            ownerPrincipal,
            "host-message-second",
            "job-message-second");
        using var firstKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var secondKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await SetHostKeyAsync(ownerPrincipal.PrincipalId, "host-message-first", firstKey);
        await SetHostKeyAsync(ownerPrincipal.PrincipalId, "host-message-second", secondKey);
        var now = DateTimeOffset.UtcNow;

        async Task AcknowledgeAsync(ECDsa key, string hostId, HostWorkLease lease, string attemptId, string messageId)
        {
            using var response = await SendSignedAsync(key, hostId, HostAcceptedMessageOperations.LeaseAck,
                lease.LeaseId, $"/host-channel/leases/{lease.LeaseId}/ack", new
                {
                    leaseVersion = lease.Version,
                    localAttemptId = attemptId,
                    accepted = true,
                    rejectionCode = (string?)null,
                }, messageId, 1, now.ToUnixTimeSeconds());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await AcknowledgeAsync(firstKey, "host-message-first", firstLease, "attempt-message-first", "ack-message-first");
        await AcknowledgeAsync(secondKey, "host-message-second", secondLease, "attempt-message-second", "ack-message-second");
        var normalized = RemoteHostOutputNormalizer.Normalize(
            Encoding.UTF8.GetBytes("same message namespace"),
            RemoteHostProtocol.MaximumArtifactBodyBytes);

        async Task UploadAsync(ECDsa key, string hostId, HostWorkLease lease, string attemptId, string artifactId)
        {
            using var response = await SendSignedAsync(key, hostId, HostAcceptedMessageOperations.LeaseArtifact,
                lease.LeaseId, $"/host-channel/leases/{lease.LeaseId}/artifacts", new
                {
                    leaseVersion = lease.Version + 1,
                    localAttemptId = attemptId,
                    artifactId,
                    kind = "TEXT",
                    mediaType = "text/plain",
                    summary = "message namespace",
                    declaredSize = normalized.SizeBytes,
                    declaredSha256 = normalized.Sha256,
                    retention = "RUN",
                    textContent = "same message namespace",
                }, "shared-artifact-message", 2, now.ToUnixTimeSeconds() + 1);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        await UploadAsync(firstKey, "host-message-first", firstLease, "attempt-message-first", "artifact-message-first");
        await UploadAsync(secondKey, "host-message-second", secondLease, "attempt-message-second", "artifact-message-second");

        Assert.Equal(1L, await ReadHostArtifactReceiptCountAsync(ownerPrincipal.PrincipalId, "artifact-message-first"));
        Assert.Equal(1L, await ReadHostArtifactReceiptCountAsync(ownerPrincipal.PrincipalId, "artifact-message-second"));
        Assert.Equal(1L, await ReadAcceptedHostMessageCountAsync(
            ownerPrincipal.PrincipalId, "host-message-first", "shared-artifact-message"));
        Assert.Equal(1L, await ReadAcceptedHostMessageCountAsync(
            ownerPrincipal.PrincipalId, "host-message-second", "shared-artifact-message"));
    }

    [Fact]
    public async Task Artifact_upload_rejects_wrong_state_attempt_version_host_grant_fence_and_expiry()
    {
        static object ArtifactBody(string localAttemptId, int declaredSize, string declaredSha256) => new
        {
            leaseVersion = 1L,
            localAttemptId,
            artifactId = "artifact-check",
            kind = "TEXT",
            mediaType = "text/plain",
            summary = "summary",
            declaredSize,
            declaredSha256,
            retention = "RUN",
            textContent = "content",
        };

        var normalized = RemoteHostOutputNormalizer.Normalize(Encoding.UTF8.GetBytes("content"), RemoteHostProtocol.MaximumArtifactBodyBytes);

        var (offeredStore, offeredOwner, _, offeredLease) = await CreateDispatchedLeaseAsync("artifact-offered", "host-offered", "job-offered");
        using (var offeredKey = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            await SetHostKeyAsync(offeredOwner.PrincipalId, "host-offered", offeredKey);
            using var offered = await SendSignedAsync(offeredKey, "host-offered", HostAcceptedMessageOperations.LeaseArtifact,
                offeredLease.LeaseId, $"/host-channel/leases/{offeredLease.LeaseId}/artifacts", ArtifactBody("attempt", normalized.SizeBytes, normalized.Sha256),
                "message-offered-artifact", 1, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            Assert.Equal(HttpStatusCode.Conflict, offered.StatusCode);
        }

        var (activeStore, activeOwner, activeRun, activeLease) = await CreateDispatchedLeaseAsync("artifact-active", "host-active", "job-active");
        using var activeKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await SetHostKeyAsync(activeOwner.PrincipalId, "host-active", activeKey);
        var activeNow = DateTimeOffset.UtcNow;
        const string localAttemptId = "attempt-active";
        using var ack = await SendSignedAsync(activeKey, "host-active", HostAcceptedMessageOperations.LeaseAck,
            activeLease.LeaseId, $"/host-channel/leases/{activeLease.LeaseId}/ack", new
            {
                leaseVersion = activeLease.Version,
                localAttemptId,
                accepted = true,
                rejectionCode = (string?)null,
            }, "message-active-ack", 1, activeNow.ToUnixTimeSeconds());
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);

        using var wrongAttempt = await SendSignedAsync(activeKey, "host-active", HostAcceptedMessageOperations.LeaseArtifact,
            activeLease.LeaseId, $"/host-channel/leases/{activeLease.LeaseId}/artifacts", new
            {
                leaseVersion = activeLease.Version + 1,
                localAttemptId = "attempt-wrong",
                artifactId = "artifact-wrong-attempt",
                kind = "TEXT",
                mediaType = "text/plain",
                summary = "summary",
                declaredSize = normalized.SizeBytes,
                declaredSha256 = normalized.Sha256,
                retention = "RUN",
                textContent = "content",
            }, "message-wrong-attempt", 2, activeNow.ToUnixTimeSeconds() + 1);
        Assert.Equal(HttpStatusCode.Conflict, wrongAttempt.StatusCode);
        Assert.Contains("host_attempt_mismatch", await wrongAttempt.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var wrongVersion = await SendSignedAsync(activeKey, "host-active", HostAcceptedMessageOperations.LeaseArtifact,
            activeLease.LeaseId, $"/host-channel/leases/{activeLease.LeaseId}/artifacts", new
            {
                leaseVersion = activeLease.Version,
                localAttemptId,
                artifactId = "artifact-wrong-version",
                kind = "TEXT",
                mediaType = "text/plain",
                summary = "summary",
                declaredSize = normalized.SizeBytes,
                declaredSha256 = normalized.Sha256,
                retention = "RUN",
                textContent = "content",
            }, "message-wrong-version", 3, activeNow.ToUnixTimeSeconds() + 2);
        Assert.Equal(HttpStatusCode.Conflict, wrongVersion.StatusCode);

        await SeedOnlineHostAsync(activeStore, activeOwner.PrincipalId, "host-other", "repo-main");
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await SetHostKeyAsync(activeOwner.PrincipalId, "host-other", otherKey);
        using var wrongHost = await SendSignedAsync(otherKey, "host-other", HostAcceptedMessageOperations.LeaseArtifact,
            activeLease.LeaseId, $"/host-channel/leases/{activeLease.LeaseId}/artifacts", new
            {
                leaseVersion = activeLease.Version + 1,
                localAttemptId,
                artifactId = "artifact-wrong-host",
                kind = "TEXT",
                mediaType = "text/plain",
                summary = "summary",
                declaredSize = normalized.SizeBytes,
                declaredSha256 = normalized.Sha256,
                retention = "RUN",
                textContent = "content",
            }, "message-wrong-host", 1, activeNow.ToUnixTimeSeconds() + 3);
        Assert.Equal(HttpStatusCode.Conflict, wrongHost.StatusCode);

        await DeleteSchedulerLeaseAsync(activeOwner.PrincipalId, activeRun.RunId, activeLease.SchedulerFence);
        using var wrongFence = await SendSignedAsync(activeKey, "host-active", HostAcceptedMessageOperations.LeaseArtifact,
            activeLease.LeaseId, $"/host-channel/leases/{activeLease.LeaseId}/artifacts", new
            {
                leaseVersion = activeLease.Version + 1,
                localAttemptId,
                artifactId = "artifact-wrong-fence",
                kind = "TEXT",
                mediaType = "text/plain",
                summary = "summary",
                declaredSize = normalized.SizeBytes,
                declaredSha256 = normalized.Sha256,
                retention = "RUN",
                textContent = "content",
            }, "message-wrong-fence", 4, activeNow.ToUnixTimeSeconds() + 4);
        Assert.Equal(HttpStatusCode.Conflict, wrongFence.StatusCode);

        var (expiredStore, expiredOwner, _, expiredLease) = await CreateDispatchedLeaseAsync("artifact-expired", "host-expired", "job-expired");
        using var expiredKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await SetHostKeyAsync(expiredOwner.PrincipalId, "host-expired", expiredKey);
        var expiredNow = DateTimeOffset.UtcNow;
        const string expiredAttemptId = "attempt-expired";
        using var expiredAck = await SendSignedAsync(expiredKey, "host-expired", HostAcceptedMessageOperations.LeaseAck,
            expiredLease.LeaseId, $"/host-channel/leases/{expiredLease.LeaseId}/ack", new
            {
                leaseVersion = expiredLease.Version,
                localAttemptId = expiredAttemptId,
                accepted = true,
                rejectionCode = (string?)null,
            }, "message-expired-ack", 1, expiredNow.ToUnixTimeSeconds());
        Assert.Equal(HttpStatusCode.OK, expiredAck.StatusCode);
        await ForceLeaseExpiryAsync(expiredOwner.PrincipalId, expiredLease.LeaseId, expiredNow.AddMinutes(-1));
        using var expired = await SendSignedAsync(expiredKey, "host-expired", HostAcceptedMessageOperations.LeaseArtifact,
            expiredLease.LeaseId, $"/host-channel/leases/{expiredLease.LeaseId}/artifacts", new
            {
                leaseVersion = expiredLease.Version + 1,
                localAttemptId = expiredAttemptId,
                artifactId = "artifact-expired",
                kind = "TEXT",
                mediaType = "text/plain",
                summary = "summary",
                declaredSize = normalized.SizeBytes,
                declaredSha256 = normalized.Sha256,
                retention = "RUN",
                textContent = "content",
            }, "message-expired", 2, expiredNow.ToUnixTimeSeconds() + 1);
        Assert.Equal(HttpStatusCode.Conflict, expired.StatusCode);
        Assert.Contains("host_lease_expired", await expired.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var (grantStore, grantOwner, _, grantLease) = await CreateDispatchedLeaseAsync("artifact-grant", "host-grant", "job-grant");
        using var grantKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await SetHostKeyAsync(grantOwner.PrincipalId, "host-grant", grantKey);
        var grantNow = DateTimeOffset.UtcNow;
        const string grantAttemptId = "attempt-grant";
        using var grantAck = await SendSignedAsync(grantKey, "host-grant", HostAcceptedMessageOperations.LeaseAck,
            grantLease.LeaseId, $"/host-channel/leases/{grantLease.LeaseId}/ack", new
            {
                leaseVersion = grantLease.Version,
                localAttemptId = grantAttemptId,
                accepted = true,
                rejectionCode = (string?)null,
            }, "message-grant-ack", 1, grantNow.ToUnixTimeSeconds());
        Assert.Equal(HttpStatusCode.OK, grantAck.StatusCode);
        await RevokeActiveHostCapabilityGrantAsync(grantOwner.PrincipalId, "host-grant");
        using var grantChanged = await SendSignedAsync(grantKey, "host-grant", HostAcceptedMessageOperations.LeaseArtifact,
            grantLease.LeaseId, $"/host-channel/leases/{grantLease.LeaseId}/artifacts", new
            {
                leaseVersion = grantLease.Version + 1,
                localAttemptId = grantAttemptId,
                artifactId = "artifact-grant",
                kind = "TEXT",
                mediaType = "text/plain",
                summary = "summary",
                declaredSize = normalized.SizeBytes,
                declaredSha256 = normalized.Sha256,
                retention = "RUN",
                textContent = "content",
            }, "message-grant-changed", 2, grantNow.ToUnixTimeSeconds() + 1);
        Assert.Equal(HttpStatusCode.Conflict, grantChanged.StatusCode);
        Assert.Contains("host_lease_grant_changed", await grantChanged.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Artifact_upload_enforces_route_body_limit_unknown_fields_and_rolls_back_on_precommit_failure()
    {
        var (store, ownerPrincipal, run, lease) = await CreateDispatchedLeaseAsync("artifact-bounds", "host-bounds", "job-bounds");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await SetHostKeyAsync(ownerPrincipal.PrincipalId, "host-bounds", key);
        var now = DateTimeOffset.UtcNow;
        const string localAttemptId = "attempt-bounds";
        using var ack = await SendSignedAsync(key, "host-bounds", HostAcceptedMessageOperations.LeaseAck,
            lease.LeaseId, $"/host-channel/leases/{lease.LeaseId}/ack", new
            {
                leaseVersion = lease.Version,
                localAttemptId,
                accepted = true,
                rejectionCode = (string?)null,
            }, "message-bounds-ack", 1, now.ToUnixTimeSeconds());
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);

        var oversizedRequestText = new string('x', RemoteHostProtocol.MaximumArtifactRequestBodyBytes);
        var oversizedRequestNormalized = RemoteHostOutputNormalizer.Normalize(
            Encoding.UTF8.GetBytes(oversizedRequestText),
            RemoteHostProtocol.MaximumArtifactBodyBytes);
        using var oversizedRequest = await SendSignedAsync(key, "host-bounds", HostAcceptedMessageOperations.LeaseArtifact,
            lease.LeaseId, $"/host-channel/leases/{lease.LeaseId}/artifacts", new
            {
                leaseVersion = lease.Version + 1,
                localAttemptId,
                artifactId = "artifact-request-oversized",
                kind = "TEXT",
                mediaType = "text/plain",
                summary = "summary",
                declaredSize = oversizedRequestNormalized.SizeBytes,
                declaredSha256 = oversizedRequestNormalized.Sha256,
                retention = "RUN",
                textContent = oversizedRequestText,
            }, "message-artifact-request-oversized", 2, now.ToUnixTimeSeconds() + 1);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedRequest.StatusCode);

        var maximumText = new string('"', RemoteHostProtocol.MaximumArtifactBodyBytes);
        var maximumNormalized = RemoteHostOutputNormalizer.Normalize(
            Encoding.UTF8.GetBytes(maximumText),
            RemoteHostProtocol.MaximumArtifactBodyBytes);
        using var maximum = await SendSignedAsync(key, "host-bounds", HostAcceptedMessageOperations.LeaseArtifact,
            lease.LeaseId, $"/host-channel/leases/{lease.LeaseId}/artifacts", new
            {
                leaseVersion = lease.Version + 1,
                localAttemptId,
                artifactId = "artifact-maximum-content",
                kind = "TEXT",
                mediaType = "text/plain",
                summary = "summary",
                declaredSize = maximumNormalized.SizeBytes,
                declaredSha256 = maximumNormalized.Sha256,
                retention = "RUN",
                textContent = maximumText,
            }, "message-artifact-maximum-content", 2, now.ToUnixTimeSeconds() + 1);
        Assert.Equal(HttpStatusCode.Created, maximum.StatusCode);
        Assert.Equal(RemoteHostProtocol.MaximumArtifactBodyBytes,
            (await store.GetHostArtifactDetailAsync(ownerPrincipal.PrincipalId, "artifact-maximum-content"))!.Artifact.SizeBytes);

        var normalized = RemoteHostOutputNormalizer.Normalize(Encoding.UTF8.GetBytes("content"), RemoteHostProtocol.MaximumArtifactBodyBytes);
        using var unknown = await SendSignedAsync(key, "host-bounds", HostAcceptedMessageOperations.LeaseArtifact,
            lease.LeaseId, $"/host-channel/leases/{lease.LeaseId}/artifacts", new
            {
                leaseVersion = lease.Version + 1,
                localAttemptId,
                artifactId = "artifact-unknown",
                kind = "TEXT",
                mediaType = "text/plain",
                summary = "summary",
                declaredSize = normalized.SizeBytes,
                declaredSha256 = normalized.Sha256,
                retention = "RUN",
                textContent = "content",
                unexpected = true,
            }, "message-artifact-unknown", 3, now.ToUnixTimeSeconds() + 2);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        typeof(SqliteKernelStore)
            .GetProperty("RemoteHostBeforeCommitAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(store, (Func<CancellationToken, Task>)(_ => throw new InvalidOperationException("injected-before-commit")));
        using var rollback = await SendSignedAsync(key, "host-bounds", HostAcceptedMessageOperations.LeaseArtifact,
            lease.LeaseId, $"/host-channel/leases/{lease.LeaseId}/artifacts", new
            {
                leaseVersion = lease.Version + 1,
                localAttemptId,
                artifactId = "artifact-rollback",
                kind = "TEXT",
                mediaType = "text/plain",
                summary = "summary",
                declaredSize = normalized.SizeBytes,
                declaredSha256 = normalized.Sha256,
                retention = "RUN",
                textContent = "content",
            }, "message-artifact-rollback", 4, now.ToUnixTimeSeconds() + 3);
        typeof(SqliteKernelStore)
            .GetProperty("RemoteHostBeforeCommitAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(store, null);

        Assert.Equal(HttpStatusCode.InternalServerError, rollback.StatusCode);
        Assert.Equal(0L, await ReadHostArtifactCountAsync(ownerPrincipal.PrincipalId, "artifact-rollback"));
        Assert.Equal(0L, await ReadHostArtifactReceiptCountAsync(ownerPrincipal.PrincipalId, "artifact-rollback"));
        Assert.Equal(0L, await ReadAcceptedHostMessageCountAsync(ownerPrincipal.PrincipalId, "host-bounds", "message-artifact-rollback"));
        Assert.Equal(3L, await ReadLastAcceptedHostSequenceAsync(ownerPrincipal.PrincipalId, "host-bounds"));

        await SeedHostArtifactMetadataAsync(
            ownerPrincipal.PrincipalId,
            run.RunId,
            lease.LeaseId,
            RemoteHostValidation.MaximumArtifactsPerRun - 1);
        using var limit = await SendSignedAsync(key, "host-bounds", HostAcceptedMessageOperations.LeaseArtifact,
            lease.LeaseId, $"/host-channel/leases/{lease.LeaseId}/artifacts", new
            {
                leaseVersion = lease.Version + 1,
                localAttemptId,
                artifactId = "artifact-over-limit",
                kind = "TEXT",
                mediaType = "text/plain",
                summary = "summary",
                declaredSize = normalized.SizeBytes,
                declaredSha256 = normalized.Sha256,
                retention = "RUN",
                textContent = "content",
            }, "message-artifact-over-limit", 4, now.ToUnixTimeSeconds() + 4);
        Assert.Equal(HttpStatusCode.Conflict, limit.StatusCode);
        var limitText = await limit.Content.ReadAsStringAsync();
        Assert.Contains("artifact_limit_exceeded", limitText, StringComparison.Ordinal);
        Assert.Equal(0L, await ReadHostArtifactCountAsync(ownerPrincipal.PrincipalId, "artifact-over-limit"));

        using var limitReplay = await SendSignedAsync(key, "host-bounds", HostAcceptedMessageOperations.LeaseArtifact,
            lease.LeaseId, $"/host-channel/leases/{lease.LeaseId}/artifacts", new
            {
                leaseVersion = lease.Version + 1,
                localAttemptId,
                artifactId = "artifact-over-limit",
                kind = "TEXT",
                mediaType = "text/plain",
                summary = "summary",
                declaredSize = normalized.SizeBytes,
                declaredSha256 = normalized.Sha256,
                retention = "RUN",
                textContent = "content",
            }, "message-artifact-over-limit", 4, now.ToUnixTimeSeconds() + 4);
        Assert.Equal(limitText, await limitReplay.Content.ReadAsStringAsync());

        var pagedArtifactIds = new List<string>();
        string? cursor = null;
        var expectedPageSizes = new[] { 25, 25, 14 };
        foreach (var expectedPageSize in expectedPageSizes)
        {
            var path = $"/api/v1/job-runs/{run.RunId}/remote-artifacts?limit=25";
            if (cursor is not null)
                path += $"&cursor={Uri.EscapeDataString(cursor)}";
            using var page = await SendAsync("artifact-bounds", HttpMethod.Get, path);
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            var pageJson = await Json(page);
            var items = pageJson.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(expectedPageSize, items.Length);
            pagedArtifactIds.AddRange(items.Select(item => item.GetProperty("artifactId").GetString()!));
            cursor = pageJson.GetProperty("nextCursor").ValueKind == JsonValueKind.Null
                ? null
                : pageJson.GetProperty("nextCursor").GetString();
        }
        Assert.Null(cursor);
        Assert.Equal(RemoteHostValidation.MaximumArtifactsPerRun, pagedArtifactIds.Count);
        Assert.Equal(pagedArtifactIds.Count, pagedArtifactIds.Distinct(StringComparer.Ordinal).Count());

        using var invalidCursor = await SendAsync("artifact-bounds", HttpMethod.Get,
            $"/api/v1/job-runs/{run.RunId}/remote-artifacts?cursor=not-a-cursor");
        Assert.Equal(HttpStatusCode.BadRequest, invalidCursor.StatusCode);
    }

    [Fact]
    public async Task Unsigned_host_channel_requests_fail_closed()
    {
        using var poll = await _client.PostAsync("/host-channel/poll", JsonContent.Create(new { maxWaitSeconds = 1 }));
        Assert.Equal(HttpStatusCode.Unauthorized, poll.StatusCode);

        using var ack = await _client.PostAsync("/host-channel/leases/lease-123/ack", JsonContent.Create(new { }));
        Assert.Equal(HttpStatusCode.Unauthorized, ack.StatusCode);
    }

    [Fact]
    public async Task All_signed_host_routes_require_valid_headers_and_json_content_type()
    {
        foreach (var request in new[]
        {
            new HttpRequestMessage(HttpMethod.Post, "/host-channel/poll") { Content = JsonContent.Create(new { maxWaitSeconds = 1 }) },
            new HttpRequestMessage(HttpMethod.Post, "/host-channel/leases/lease-1/ack") { Content = JsonContent.Create(new { leaseVersion = 1, localAttemptId = "attempt-1", accepted = true, rejectionCode = (string?)null }) },
            new HttpRequestMessage(HttpMethod.Post, "/host-channel/leases/lease-1/events") { Content = JsonContent.Create(new { leaseVersion = 1, localAttemptId = "attempt-1", events = Array.Empty<object>() }) },
            new HttpRequestMessage(HttpMethod.Post, "/host-channel/leases/lease-1/complete") { Content = JsonContent.Create(new { leaseVersion = 1, outcome = "SUCCEEDED", output = (string?)null, outputSha256 = (string?)null, truncated = false, localAttemptId = "attempt-1" }) },
            new HttpRequestMessage(HttpMethod.Post, "/host-channel/leases/lease-1/reconcile") { Content = JsonContent.Create(new { leaseVersion = 1, localAttemptId = "attempt-1", observedState = "UNKNOWN" }) },
        })
        {
            using (request)
            {
                using var response = await _client.SendAsync(request);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }
        }

        using var invalidMedia = new HttpRequestMessage(HttpMethod.Post, "/host-channel/poll")
        {
            Content = new StringContent("{}", Encoding.UTF8, "text/plain"),
        };
        using var invalidMediaResponse = await _client.SendAsync(invalidMedia);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, invalidMediaResponse.StatusCode);
    }

    [Fact]
    public async Task Invalid_event_batch_consumes_a_rejection_receipt_without_partial_event_or_lease_mutation()
    {
        var (store, ownerPrincipal, run, lease) = await CreateDispatchedLeaseAsync("batch-owner", "host-batch", "job-batch");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await SetHostKeyAsync(ownerPrincipal.PrincipalId, "host-batch", key);
        var now = DateTimeOffset.UtcNow;
        const string localAttemptId = "attempt-batch";

        using var ack = await SendSignedAsync(key, "host-batch", HostAcceptedMessageOperations.LeaseAck, lease.LeaseId, $"/host-channel/leases/{lease.LeaseId}/ack", new
        {
            leaseVersion = lease.Version,
            localAttemptId,
            accepted = true,
            rejectionCode = (string?)null,
        }, "message-batch-ack", 1, now.ToUnixTimeSeconds());
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);

        using var rejected = await SendSignedAsync(key, "host-batch", HostAcceptedMessageOperations.LeaseEvents, lease.LeaseId, $"/host-channel/leases/{lease.LeaseId}/events", new
        {
            leaseVersion = lease.Version + 1,
            localAttemptId,
            events = new[]
            {
                new { eventId = "event-start", sequence = 1, type = "STEP_STARTED", occurredAt = now.AddSeconds(1), summary = "started", data = new { phase = "inspect" } },
                new { eventId = "event-second-start", sequence = 2, type = "STEP_STARTED", occurredAt = now.AddSeconds(2), summary = "started-again", data = new { phase = "inspect" } },
            },
        }, "message-batch-events", 2, now.ToUnixTimeSeconds() + 1);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        var rejectedBody = await rejected.Content.ReadAsStringAsync();

        using var replay = await SendSignedAsync(key, "host-batch", HostAcceptedMessageOperations.LeaseEvents, lease.LeaseId, $"/host-channel/leases/{lease.LeaseId}/events", new
        {
            leaseVersion = lease.Version + 1,
            localAttemptId,
            events = new[]
            {
                new { eventId = "event-start", sequence = 1, type = "STEP_STARTED", occurredAt = now.AddSeconds(1), summary = "started", data = new { phase = "inspect" } },
                new { eventId = "event-second-start", sequence = 2, type = "STEP_STARTED", occurredAt = now.AddSeconds(2), summary = "started-again", data = new { phase = "inspect" } },
            },
        }, "message-batch-events", 2, now.ToUnixTimeSeconds() + 1);
        Assert.Equal(rejectedBody, await replay.Content.ReadAsStringAsync());

        Assert.Equal(0L, await ReadLeaseEventCountAsync(ownerPrincipal.PrincipalId, lease.LeaseId));
        var projection = await store.GetRemoteJobRunProjectionAsync(ownerPrincipal.PrincipalId, run.RunId);
        Assert.Equal(HostLeaseStates.Acknowledged, projection!.Lease!.State);
        Assert.Equal("RUNNING", (await store.GetJobRunAsync(ownerPrincipal.PrincipalId, run.RunId))!.State);
    }

    [Fact]
    public async Task Grant_drift_atomically_invalidates_the_lease_and_rejects_step_started()
    {
        var (store, ownerPrincipal, run, lease) = await CreateDispatchedLeaseAsync(
            "grant-drift-owner", "host-grant-drift", "job-grant-drift");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await SetHostKeyAsync(ownerPrincipal.PrincipalId, "host-grant-drift", key);
        var now = DateTimeOffset.UtcNow;
        const string localAttemptId = "attempt-grant-drift";

        using var ack = await SendSignedAsync(key, "host-grant-drift", HostAcceptedMessageOperations.LeaseAck,
            lease.LeaseId, $"/host-channel/leases/{lease.LeaseId}/ack", new
            {
                leaseVersion = lease.Version,
                localAttemptId,
                accepted = true,
                rejectionCode = (string?)null,
            }, "message-grant-drift-ack", 1, now.ToUnixTimeSeconds());
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);

        var detail = await store.GetRemoteHostDetailAsync(ownerPrincipal.PrincipalId, "host-grant-drift");
        var changed = await store.UpdateRemoteHostGrantsAsync(
            ownerPrincipal.PrincipalId,
            "host-grant-drift",
            detail!.Host.Version,
            [],
            [],
            "grant-drift-key",
            RequestHash(new { expectedVersion = detail.Host.Version, capabilities = Array.Empty<object>(), resources = Array.Empty<object>() }),
            now.AddSeconds(1));
        Assert.True(changed.Succeeded);
        var invalidated = await store.GetRemoteJobRunProjectionAsync(ownerPrincipal.PrincipalId, run.RunId);
        Assert.Equal(HostLeaseStates.ReconciliationRequired, invalidated!.Lease!.State);
        Assert.Equal("RECONCILIATION_REQUIRED",
            (await store.GetJobRunAsync(ownerPrincipal.PrincipalId, run.RunId))!.State);

        using var events = await SendSignedAsync(key, "host-grant-drift", HostAcceptedMessageOperations.LeaseEvents,
            lease.LeaseId, $"/host-channel/leases/{lease.LeaseId}/events", new
            {
                leaseVersion = lease.Version + 1,
                localAttemptId,
                events = new[]
                {
                    new { eventId = "event-grant-drift", sequence = 1, type = "STEP_STARTED", occurredAt = now.AddSeconds(2), summary = "started", data = new { phase = "inspect" } },
                },
            }, "message-grant-drift-events", 2, now.ToUnixTimeSeconds() + 2);
        Assert.Equal(HttpStatusCode.Conflict, events.StatusCode);
            Assert.Contains("host_lease_invalid", await events.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(0L, await ReadLeaseEventCountAsync(ownerPrincipal.PrincipalId, lease.LeaseId));
        var projection = await store.GetRemoteJobRunProjectionAsync(ownerPrincipal.PrincipalId, run.RunId);
            Assert.Equal(HostLeaseStates.ReconciliationRequired, projection!.Lease!.State);
    }

    [Fact]
    public async Task Signed_invalid_json_shape_consumes_a_stable_rejection_receipt()
    {
        var (_, ownerPrincipal, _, _) = await CreateDispatchedLeaseAsync("invalid-owner", "host-invalid", "job-invalid");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await SetHostKeyAsync(ownerPrincipal.PrincipalId, "host-invalid", key);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var invalidBody = new { maxWaitSeconds = 1, activeAttempt = (object?)null, unexpected = true };

        using var rejected = await SendSignedAsync(key, "host-invalid", HostAcceptedMessageOperations.Poll,
            "-", "/host-channel/poll", invalidBody, "message-invalid-json", 1, timestamp);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var rejectedText = await rejected.Content.ReadAsStringAsync();
        using var replay = await SendSignedAsync(key, "host-invalid", HostAcceptedMessageOperations.Poll,
            "-", "/host-channel/poll", invalidBody, "message-invalid-json", 1, timestamp);
        Assert.Equal(rejectedText, await replay.Content.ReadAsStringAsync());
        using var changed = await SendSignedAsync(key, "host-invalid", HostAcceptedMessageOperations.Poll,
            "-", "/host-channel/poll", new { maxWaitSeconds = 1, activeAttempt = (object?)null },
            "message-invalid-json", 1, timestamp);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
    }

    [Fact]
    public async Task Poll_never_reoffers_acknowledged_work_and_resumes_only_the_exact_attempt()
    {
        var (store, ownerPrincipal, run, lease) = await CreateDispatchedLeaseAsync("poll-owner", "host-poll", "job-poll");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await SetHostKeyAsync(ownerPrincipal.PrincipalId, "host-poll", key);
        var now = DateTimeOffset.UtcNow;
        const string localAttemptId = "attempt-poll";
        using var ack = await SendSignedAsync(key, "host-poll", HostAcceptedMessageOperations.LeaseAck,
            lease.LeaseId, $"/host-channel/leases/{lease.LeaseId}/ack", new
            {
                leaseVersion = lease.Version,
                localAttemptId,
                accepted = true,
                rejectionCode = (string?)null,
            }, "message-poll-ack", 1, now.ToUnixTimeSeconds());
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);

        using var missingAttempt = await SendSignedAsync(key, "host-poll", HostAcceptedMessageOperations.Poll,
            "-", "/host-channel/poll", new { maxWaitSeconds = 1, activeAttempt = (object?)null },
            "message-poll-missing-attempt", 2, now.ToUnixTimeSeconds() + 1);
        Assert.Equal(HttpStatusCode.Conflict, missingAttempt.StatusCode);

        using var resumed = await SendSignedAsync(key, "host-poll", HostAcceptedMessageOperations.Poll,
            "-", "/host-channel/poll", new
            {
                maxWaitSeconds = 1,
                activeAttempt = new { leaseId = lease.LeaseId, localAttemptId, state = "STARTED" },
            }, "message-poll-resume", 3, now.ToUnixTimeSeconds() + 2);
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        var resumedJson = await Json(resumed);
        Assert.Equal(JsonValueKind.Null, resumedJson.GetProperty("command").ValueKind);
        Assert.Equal(lease.LeaseId, resumedJson.GetProperty("lease").GetProperty("leaseId").GetString());
        Assert.Equal(HostLeaseStates.Running,
            (await store.GetRemoteJobRunProjectionAsync(ownerPrincipal.PrincipalId, run.RunId))!.Lease!.State);
    }

    [Fact]
    public async Task Canceling_a_job_with_an_offered_lease_withdraws_it_before_poll_and_ack()
    {
        var (store, ownerPrincipal, run, lease) = await CreateDispatchedLeaseAsync(
            "cancel-owner", "host-cancel", "job-cancel");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await SetHostKeyAsync(ownerPrincipal.PrincipalId, "host-cancel", key);
        Assert.True(await store.SetJobDesiredStateAsync(
            ownerPrincipal.PrincipalId, "job-cancel", 1, "CANCELED"));
        var projection = await store.GetRemoteJobRunProjectionAsync(ownerPrincipal.PrincipalId, run.RunId);
        Assert.Equal(HostLeaseStates.Revoked, projection!.Lease!.State);
        Assert.Equal("CANCELED", (await store.GetJobRunAsync(ownerPrincipal.PrincipalId, run.RunId))!.State);

        var now = DateTimeOffset.UtcNow;
        using var poll = await SendSignedAsync(key, "host-cancel", HostAcceptedMessageOperations.Poll,
            "-", "/host-channel/poll", new { maxWaitSeconds = 1, activeAttempt = (object?)null },
            "message-cancel-poll", 1, now.ToUnixTimeSeconds());
        Assert.Equal(HttpStatusCode.OK, poll.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await Json(poll)).GetProperty("command").ValueKind);

        using var ack = await SendSignedAsync(key, "host-cancel", HostAcceptedMessageOperations.LeaseAck,
            lease.LeaseId, $"/host-channel/leases/{lease.LeaseId}/ack", new
            {
                leaseVersion = lease.Version,
                localAttemptId = "attempt-cancel",
                accepted = true,
                rejectionCode = (string?)null,
            }, "message-cancel-ack", 2, now.ToUnixTimeSeconds() + 1);
        Assert.Equal(HttpStatusCode.Conflict, ack.StatusCode);
    }

    [Fact]
    public async Task Unknown_then_started_reconciliation_keeps_and_resumes_the_same_live_lease()
    {
        var (store, ownerPrincipal, run, lease) = await CreateDispatchedLeaseAsync("reconcile-owner", "host-reconcile", "job-reconcile");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await SetHostKeyAsync(ownerPrincipal.PrincipalId, "host-reconcile", key);
        var now = DateTimeOffset.UtcNow;
        const string localAttemptId = "attempt-reconcile";

        using var ack = await SendSignedAsync(key, "host-reconcile", HostAcceptedMessageOperations.LeaseAck, lease.LeaseId, $"/host-channel/leases/{lease.LeaseId}/ack", new
        {
            leaseVersion = lease.Version,
            localAttemptId,
            accepted = true,
            rejectionCode = (string?)null,
        }, "message-reconcile-ack", 1, now.ToUnixTimeSeconds());
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);

        using var unknown = await SendSignedAsync(key, "host-reconcile", HostAcceptedMessageOperations.LeaseReconcile, lease.LeaseId, $"/host-channel/leases/{lease.LeaseId}/reconcile", new
        {
            leaseVersion = lease.Version + 1,
            localAttemptId,
            observedState = "UNKNOWN",
            outputSha256 = new string('a', 64),
        }, "message-reconcile-unknown", 2, now.ToUnixTimeSeconds() + 1);
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        Assert.Equal("WAITING", (await Json(unknown)).GetProperty("resolution").GetString());
        var afterUnknown = await store.GetRemoteJobRunProjectionAsync(ownerPrincipal.PrincipalId, run.RunId);
        Assert.Equal(HostLeaseStates.Disconnected, afterUnknown!.Lease!.State);
        Assert.Equal(new string('a', 64), afterUnknown.Lease.OutputSha256);
        Assert.Equal(JobRunBlockerCodes.HostDisconnected, afterUnknown.Blocker!.Code);
        Assert.Equal("RUNNING", (await store.GetJobRunAsync(ownerPrincipal.PrincipalId, run.RunId))!.State);

        using var started = await SendSignedAsync(key, "host-reconcile", HostAcceptedMessageOperations.LeaseReconcile, lease.LeaseId, $"/host-channel/leases/{lease.LeaseId}/reconcile", new
        {
            leaseVersion = afterUnknown.Lease.Version,
            localAttemptId,
            observedState = "STARTED",
        }, "message-reconcile-started", 3, now.ToUnixTimeSeconds() + 2);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        Assert.Equal("RESUME", (await Json(started)).GetProperty("resolution").GetString());
        var resumed = await store.GetRemoteJobRunProjectionAsync(ownerPrincipal.PrincipalId, run.RunId);
        Assert.Equal(HostLeaseStates.Running, resumed!.Lease!.State);
        Assert.Null(resumed.Blocker);
        Assert.Equal("RUNNING", (await store.GetJobRunAsync(ownerPrincipal.PrincipalId, run.RunId))!.State);
    }

    [Fact]
    public async Task Not_started_after_an_accepted_step_started_event_becomes_reconciliation_required_instead_of_requeueing()
    {
        var (store, ownerPrincipal, run, lease) = await CreateDispatchedLeaseAsync("not-started-owner", "host-not-started", "job-not-started");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await SetHostKeyAsync(ownerPrincipal.PrincipalId, "host-not-started", key);
        var now = DateTimeOffset.UtcNow;
        const string localAttemptId = "attempt-not-started";

        using var ack = await SendSignedAsync(key, "host-not-started", HostAcceptedMessageOperations.LeaseAck, lease.LeaseId, $"/host-channel/leases/{lease.LeaseId}/ack", new
        {
            leaseVersion = lease.Version,
            localAttemptId,
            accepted = true,
            rejectionCode = (string?)null,
        }, "message-not-started-ack", 1, now.ToUnixTimeSeconds());
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);

        using var events = await SendSignedAsync(key, "host-not-started", HostAcceptedMessageOperations.LeaseEvents, lease.LeaseId, $"/host-channel/leases/{lease.LeaseId}/events", new
        {
            leaseVersion = lease.Version + 1,
            localAttemptId,
            events = new[]
            {
                new { eventId = "event-step", sequence = 1, type = "STEP_STARTED", occurredAt = now.AddSeconds(1), summary = "started", data = new { phase = "inspect" } },
            },
        }, "message-not-started-events", 2, now.ToUnixTimeSeconds() + 1);
        Assert.Equal(HttpStatusCode.OK, events.StatusCode);

        using var reconcile = await SendSignedAsync(key, "host-not-started", HostAcceptedMessageOperations.LeaseReconcile, lease.LeaseId, $"/host-channel/leases/{lease.LeaseId}/reconcile", new
        {
            leaseVersion = lease.Version + 2,
            localAttemptId,
            observedState = "NOT_STARTED",
        }, "message-not-started-reconcile", 3, now.ToUnixTimeSeconds() + 2);
        Assert.Equal(HttpStatusCode.OK, reconcile.StatusCode);
        Assert.Equal("RECONCILIATION_REQUIRED", (await Json(reconcile)).GetProperty("resolution").GetString());
        var projection = await store.GetRemoteJobRunProjectionAsync(ownerPrincipal.PrincipalId, run.RunId);
        Assert.Equal(HostLeaseStates.ReconciliationRequired, projection!.Lease!.State);
        Assert.Equal("RECONCILIATION_REQUIRED", (await store.GetJobRunAsync(ownerPrincipal.PrincipalId, run.RunId))!.State);
    }

    [Fact]
    public async Task Not_started_after_an_accepted_artifact_cannot_requeue_or_promote_legacy_requeued_output()
    {
        var (store, ownerPrincipal, run, lease) = await CreateDispatchedLeaseAsync(
            "artifact-not-started-owner",
            "host-artifact-not-started",
            "job-artifact-not-started");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await SetHostKeyAsync(ownerPrincipal.PrincipalId, "host-artifact-not-started", key);
        var now = DateTimeOffset.UtcNow;
        const string localAttemptId = "attempt-artifact-not-started";

        using var ack = await SendSignedAsync(key, "host-artifact-not-started", HostAcceptedMessageOperations.LeaseAck,
            lease.LeaseId, $"/host-channel/leases/{lease.LeaseId}/ack", new
            {
                leaseVersion = lease.Version,
                localAttemptId,
                accepted = true,
                rejectionCode = (string?)null,
            }, "message-artifact-not-started-ack", 1, now.ToUnixTimeSeconds());
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);

        var normalized = RemoteHostOutputNormalizer.Normalize(
            Encoding.UTF8.GetBytes("execution began"),
            RemoteHostProtocol.MaximumArtifactBodyBytes);
        using var upload = await SendSignedAsync(key, "host-artifact-not-started", HostAcceptedMessageOperations.LeaseArtifact,
            lease.LeaseId, $"/host-channel/leases/{lease.LeaseId}/artifacts", new
            {
                leaseVersion = lease.Version + 1,
                localAttemptId,
                artifactId = "artifact-not-started",
                kind = "TEXT",
                mediaType = "text/plain",
                summary = "execution evidence",
                declaredSize = normalized.SizeBytes,
                declaredSha256 = normalized.Sha256,
                retention = "RUN",
                textContent = "execution began",
            }, "message-artifact-not-started-upload", 2, now.ToUnixTimeSeconds() + 1);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);

        using var reconcile = await SendSignedAsync(key, "host-artifact-not-started", HostAcceptedMessageOperations.LeaseReconcile,
            lease.LeaseId, $"/host-channel/leases/{lease.LeaseId}/reconcile", new
            {
                leaseVersion = lease.Version + 1,
                localAttemptId,
                observedState = "NOT_STARTED",
            }, "message-artifact-not-started-reconcile", 3, now.ToUnixTimeSeconds() + 2);
        Assert.Equal(HttpStatusCode.OK, reconcile.StatusCode);
        Assert.Equal("RECONCILIATION_REQUIRED", (await Json(reconcile)).GetProperty("resolution").GetString());
        var projection = await store.GetRemoteJobRunProjectionAsync(ownerPrincipal.PrincipalId, run.RunId);
        Assert.Equal(HostLeaseStates.ReconciliationRequired, projection!.Lease!.State);
        Assert.Equal("RECONCILIATION_REQUIRED", (await store.GetJobRunAsync(ownerPrincipal.PrincipalId, run.RunId))!.State);

        await MarkLeaseAsLegacyNotStartedRequeueAsync(ownerPrincipal.PrincipalId, lease.LeaseId);
        using var verify = await SendJsonAsync("artifact-not-started-owner", HttpMethod.Post,
            "/api/v1/host-artifacts/artifact-not-started/verify",
            new { expectedVersion = 1 },
            "verify-artifact-not-started");
        Assert.Equal(HttpStatusCode.Conflict, verify.StatusCode);
        Assert.Equal(0L, await ReadHostArtifactEvidenceCountAsync(
            ownerPrincipal.PrincipalId,
            "artifact-not-started"));
    }

    [Fact]
    public async Task Revoke_order_preserves_offered_complete_and_acknowledged_contract_outcomes()
    {
        var offered = await CreateDispatchedLeaseAsync("revoke-offered-owner", "host-revoke-offered", "job-revoke-offered");
        using (var offeredKey = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            await SetHostKeyAsync(offered.OwnerPrincipal.PrincipalId, "host-revoke-offered", offeredKey);
            var offeredHost = await offered.Store.GetRemoteHostDetailAsync(offered.OwnerPrincipal.PrincipalId, "host-revoke-offered");
            await offered.Store.RevokeRemoteHostAsync(offered.OwnerPrincipal.PrincipalId, "host-revoke-offered", offeredHost!.Host.Version, "revoke-offered-key", RequestHash(new { expectedVersion = offeredHost.Host.Version }), DateTimeOffset.UtcNow);
            var offeredProjection = await offered.Store.GetRemoteJobRunProjectionAsync(offered.OwnerPrincipal.PrincipalId, offered.Run.RunId);
            Assert.Equal(HostLeaseStates.Revoked, offeredProjection!.Lease!.State);
            Assert.Equal("QUEUED", (await offered.Store.GetJobRunAsync(offered.OwnerPrincipal.PrincipalId, offered.Run.RunId))!.State);
            using var ack = await SendSignedAsync(offeredKey, "host-revoke-offered", HostAcceptedMessageOperations.LeaseAck, offered.Lease.LeaseId, $"/host-channel/leases/{offered.Lease.LeaseId}/ack", new
            {
                leaseVersion = offered.Lease.Version,
                localAttemptId = "attempt-offered",
                accepted = true,
                rejectionCode = (string?)null,
            }, "message-revoke-offered-ack", 1, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            Assert.Equal(HttpStatusCode.Conflict, ack.StatusCode);
        }

        var completed = await CreateDispatchedLeaseAsync("revoke-complete-owner", "host-revoke-complete", "job-revoke-complete");
        using (var completedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            await SetHostKeyAsync(completed.OwnerPrincipal.PrincipalId, "host-revoke-complete", completedKey);
            var now = DateTimeOffset.UtcNow;
            const string localAttemptId = "attempt-complete-first";
            using var ack = await SendSignedAsync(completedKey, "host-revoke-complete", HostAcceptedMessageOperations.LeaseAck, completed.Lease.LeaseId, $"/host-channel/leases/{completed.Lease.LeaseId}/ack", new
            {
                leaseVersion = completed.Lease.Version,
                localAttemptId,
                accepted = true,
                rejectionCode = (string?)null,
            }, "message-complete-first-ack", 1, now.ToUnixTimeSeconds());
            Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
            using var events = await SendSignedAsync(completedKey, "host-revoke-complete", HostAcceptedMessageOperations.LeaseEvents, completed.Lease.LeaseId, $"/host-channel/leases/{completed.Lease.LeaseId}/events", new
            {
                leaseVersion = completed.Lease.Version + 1,
                localAttemptId,
                events = new[] { new { eventId = "event-complete", sequence = 1, type = "STEP_STARTED", occurredAt = now.AddSeconds(1), summary = "started", data = new { phase = "inspect" } } },
            }, "message-complete-first-events", 2, now.ToUnixTimeSeconds() + 1);
            Assert.Equal(HttpStatusCode.OK, events.StatusCode);
            const string output = "ok";
            using var complete = await SendSignedAsync(completedKey, "host-revoke-complete", HostAcceptedMessageOperations.LeaseComplete, completed.Lease.LeaseId, $"/host-channel/leases/{completed.Lease.LeaseId}/complete", new
            {
                leaseVersion = completed.Lease.Version + 2,
                localAttemptId,
                outcome = "SUCCEEDED",
                output,
                outputSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(output))),
                truncated = false,
            }, "message-complete-first-complete", 3, now.ToUnixTimeSeconds() + 2);
            Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
            var host = await completed.Store.GetRemoteHostDetailAsync(completed.OwnerPrincipal.PrincipalId, "host-revoke-complete");
            await completed.Store.RevokeRemoteHostAsync(completed.OwnerPrincipal.PrincipalId, "host-revoke-complete", host!.Host.Version, "revoke-complete-key", RequestHash(new { expectedVersion = host.Host.Version }), now.AddSeconds(3));
            var projection = await completed.Store.GetRemoteJobRunProjectionAsync(completed.OwnerPrincipal.PrincipalId, completed.Run.RunId);
            Assert.Equal(HostLeaseStates.Completed, projection!.Lease!.State);
            Assert.Equal("SUCCEEDED", (await completed.Store.GetJobRunAsync(completed.OwnerPrincipal.PrincipalId, completed.Run.RunId))!.State);
        }

        var acknowledged = await CreateDispatchedLeaseAsync("revoke-ack-owner", "host-revoke-ack", "job-revoke-ack");
        using (var acknowledgedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            await SetHostKeyAsync(acknowledged.OwnerPrincipal.PrincipalId, "host-revoke-ack", acknowledgedKey);
            var now = DateTimeOffset.UtcNow;
            const string localAttemptId = "attempt-ack-first";
            using var ack = await SendSignedAsync(acknowledgedKey, "host-revoke-ack", HostAcceptedMessageOperations.LeaseAck, acknowledged.Lease.LeaseId, $"/host-channel/leases/{acknowledged.Lease.LeaseId}/ack", new
            {
                leaseVersion = acknowledged.Lease.Version,
                localAttemptId,
                accepted = true,
                rejectionCode = (string?)null,
            }, "message-ack-first-ack", 1, now.ToUnixTimeSeconds());
            Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
            var host = await acknowledged.Store.GetRemoteHostDetailAsync(acknowledged.OwnerPrincipal.PrincipalId, "host-revoke-ack");
            await acknowledged.Store.RevokeRemoteHostAsync(acknowledged.OwnerPrincipal.PrincipalId, "host-revoke-ack", host!.Host.Version, "revoke-ack-key", RequestHash(new { expectedVersion = host.Host.Version }), now.AddSeconds(1));
            var projection = await acknowledged.Store.GetRemoteJobRunProjectionAsync(acknowledged.OwnerPrincipal.PrincipalId, acknowledged.Run.RunId);
            Assert.Equal(HostLeaseStates.Revoked, projection!.Lease!.State);
            Assert.Equal("RECONCILIATION_REQUIRED", (await acknowledged.Store.GetJobRunAsync(acknowledged.OwnerPrincipal.PrincipalId, acknowledged.Run.RunId))!.State);
            using var complete = await SendSignedAsync(acknowledgedKey, "host-revoke-ack", HostAcceptedMessageOperations.LeaseComplete, acknowledged.Lease.LeaseId, $"/host-channel/leases/{acknowledged.Lease.LeaseId}/complete", new
            {
                leaseVersion = acknowledged.Lease.Version + 1,
                localAttemptId,
                outcome = "SUCCEEDED",
                output = (string?)null,
                outputSha256 = (string?)null,
                truncated = false,
            }, "message-ack-first-complete", 2, now.ToUnixTimeSeconds() + 2);
            Assert.Equal(HttpStatusCode.Conflict, complete.StatusCode);
        }
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private async Task<HttpResponseMessage> SendJsonAsync(string? owner, HttpMethod method, string path, object body, string? key)
    {
        var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        if (owner is not null) request.Headers.Add(DevHeader, owner);
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendSignedAsync(
        ECDsa key,
        string hostId,
        string operation,
        string targetId,
        string path,
        object body,
        string messageId,
        long sequence,
        long timestamp)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
        var bodyHash = RemoteHostProtocol.ComputeBodyHash(bytes);
        var signatureBytes = key.SignData(
            RemoteHostProtocol.BuildCanonicalSigningInput("POST", operation, targetId, hostId, 1, 1, messageId, sequence, timestamp, bodyHash),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add("X-Tessera-Host-Id", hostId);
        request.Headers.Add("X-Tessera-Host-Protocol-Version", "1");
        request.Headers.Add("X-Tessera-Host-Key-Version", "1");
        request.Headers.Add("X-Tessera-Host-Operation", operation);
        request.Headers.Add("X-Tessera-Host-Target-Id", targetId);
        request.Headers.Add("X-Tessera-Host-Message-Id", messageId);
        request.Headers.Add("X-Tessera-Host-Sequence", sequence.ToString());
        request.Headers.Add("X-Tessera-Host-Timestamp", timestamp.ToString());
        request.Headers.Add("X-Tessera-Host-Body-SHA256", bodyHash);
        request.Headers.Add("X-Tessera-Host-Signature", Base64Url(NormalizeLowS(signatureBytes)));
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
        Assert.DoesNotContain("signature", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/Users/", body, StringComparison.Ordinal);
        Assert.DoesNotContain("/Volumes/", body, StringComparison.Ordinal);
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

    private static byte[] NormalizeLowS(byte[] signature)
    {
        var order = new BigInteger(
            Convert.FromHexString("FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551"),
            isUnsigned: true,
            isBigEndian: true);
        var halfOrder = order / 2;
        var s = new BigInteger(signature.AsSpan(32, 32), isUnsigned: true, isBigEndian: true);
        if (s <= halfOrder)
            return signature;
        var lowS = order - s;
        signature.AsSpan(32, 32).Clear();
        lowS.ToByteArray(isUnsigned: true, isBigEndian: true)
            .CopyTo(signature.AsSpan(64 - lowS.GetByteCount(isUnsigned: true)));
        return signature;
    }

    private async Task SeedOnlineHostAsync(SqliteKernelStore store, string owner, string hostId, string resourceId)
    {
        var secret = RemoteHostValidation.CreateClaimSecret();
        var now = DateTimeOffset.UtcNow;
        await store.CreateHostPairingAsync(owner, $"pairing-{hostId}", RemoteHostValidation.HashClaimSecret(secret), $"key-{hostId}", new string('a', 64), now, now.AddMinutes(5));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(false);
        var publicKey = RemoteHostValidation.NormalizeP256PublicJwk(
            $$"""{"kty":"EC","crv":"P-256","x":"{{Base64Url(parameters.Q.X!)}}","y":"{{Base64Url(parameters.Q.Y!)}}"}""");
        var claim = new HostClaim(publicKey, "KEYCHAIN_THIS_DEVICE_ONLY", "macOS", "arm64", "1.0.0", "1",
            [new("host.repo.identity", "1", new string('a', 64), "READ_ONLY")],
            [new(resourceId, "REPOSITORY", "Repo", new string('b', 64), "AVAILABLE")]);
        var claimed = await store.ClaimHostPairingAsync($"pairing-{hostId}", secret, claim, $"claim-{hostId}", new string('b', 64), now.AddSeconds(1));
        var code = RemoteHostValidation.DeriveConfirmationCode($"pairing-{hostId}", publicKey);
        await store.ConfirmHostPairingAsync(owner, $"pairing-{hostId}", claimed.Pairing!.Version, code, hostId, hostId,
            [new("host.repo.identity", "1")], [new(resourceId, "READ_ONLY")], $"confirm-{hostId}", new string('c', 64), now.AddSeconds(2));
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "product.db")};Foreign Keys=True;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE remote_hosts SET lifecycle='ONLINE',connection_status='ONLINE',last_accepted_sequence=0 WHERE owner_principal_id=$owner AND host_id=$host;";
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$host", hostId);
        await command.ExecuteNonQueryAsync();
    }

    private async Task SetHostKeyAsync(string owner, string hostId, ECDsa key)
    {
        var parameters = key.ExportParameters(false);
        var publicKey = RemoteHostValidation.NormalizeP256PublicJwk(
            $$"""{"kty":"EC","crv":"P-256","x":"{{Base64Url(parameters.Q.X!)}}","y":"{{Base64Url(parameters.Q.Y!)}}"}""");
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "product.db")};Foreign Keys=True;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE remote_hosts SET public_key_jwk=$jwk,key_version=1,last_accepted_sequence=0,lifecycle='ONLINE',connection_status='ONLINE' WHERE owner_principal_id=$owner AND host_id=$host;";
        command.Parameters.AddWithValue("$jwk", publicKey.CanonicalJson);
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$host", hostId);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<(SqliteKernelStore Store, PrincipalRef OwnerPrincipal, ProductJobRun Run, HostWorkLease Lease)> CreateDispatchedLeaseAsync(
        string ownerSubject,
        string hostId,
        string jobId,
        string location = JobExecutionLocations.Host,
        string? preferredHostId = null)
    {
        var store = _app.Services.GetRequiredService<SqliteKernelStore>();
        var ownerPrincipal = PrincipalRef.Create("https://dev.tessera.local", "dev", ownerSubject, ownerSubject, DateTimeOffset.UtcNow);
        await store.AddAsync(ownerPrincipal);
        var (run, lease) = await CreateDispatchedLeaseForOwnerAsync(
            store,
            ownerPrincipal,
            hostId,
            jobId,
            location,
            preferredHostId);
        return (store, ownerPrincipal, run, lease);
    }

    private async Task<(ProductJobRun Run, HostWorkLease Lease)> CreateDispatchedLeaseForOwnerAsync(
        SqliteKernelStore store,
        PrincipalRef ownerPrincipal,
        string hostId,
        string jobId,
        string location = JobExecutionLocations.Host,
        string? preferredHostId = null)
    {
        await SeedOnlineHostAsync(store, ownerPrincipal.PrincipalId, hostId, "repo-main");
        var now = DateTimeOffset.UtcNow;
        var job = new ProductJob(ownerPrincipal.PrincipalId, jobId, "Host job", "Inspect repo", "ACTIVE", "READY", null,
            new JobSchedule("once", now, null, "UTC", null), null, "{}", [], [("host.repo.identity", "1")], [], now, now, 1);
        await store.AddJobAsync(job);
        await store.PutJobExecutionPolicyAsync(new(
            ownerPrincipal.PrincipalId,
            jobId,
            location,
            preferredHostId ?? hostId,
            [(RemoteHostValidation.SupportedCapabilityId, RemoteHostValidation.SupportedCapabilityVersion)],
            ["repo-main"],
            JobExecutionFallbackPolicies.None,
            1), 0);
        var run = await store.CreateRunOccurrenceAsync(ownerPrincipal.PrincipalId, jobId, now);
        Assert.NotNull(run);
        var projection = await store.GetRemoteJobRunProjectionAsync(ownerPrincipal.PrincipalId, run!.RunId);
        var scheduler = new R2SchedulerService(
            store, new InMemoryCredentialStore(), new NoopTransport(), NullLogger<R2SchedulerService>.Instance);
        for (var attempt = 0; projection?.Lease is null && attempt < 20; attempt++)
        {
            await scheduler.DispatchQueuedAsync(CancellationToken.None);
            if (projection?.Lease is null) await Task.Delay(10);
            projection = await store.GetRemoteJobRunProjectionAsync(ownerPrincipal.PrincipalId, run.RunId);
        }
        Assert.NotNull(projection?.Lease);
        return (run, projection!.Lease!);
    }

    private async Task<long> ReadLeaseEventCountAsync(string owner, string leaseId)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "product.db")};Foreign Keys=True;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM host_lease_events WHERE owner_principal_id=$owner AND lease_id=$lease;";
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$lease", leaseId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> ReadHostArtifactCountAsync(string owner, string artifactId)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "product.db")};Foreign Keys=True;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM host_artifacts WHERE owner_principal_id=$owner AND artifact_id=$artifact;";
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$artifact", artifactId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> ReadHostArtifactReceiptCountAsync(string owner, string artifactId)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "product.db")};Foreign Keys=True;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM host_artifact_receipts WHERE owner_principal_id=$owner AND artifact_id=$artifact;";
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$artifact", artifactId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> ReadHostArtifactEvidenceCountAsync(string owner, string artifactId)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "product.db")};Foreign Keys=True;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM evidence WHERE owner_principal_id=$owner AND source_type='host.artifact' AND source_native_id=$artifact;";
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$artifact", artifactId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<string> ReadHostArtifactContentAsync(string owner, string artifactId)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "product.db")};Foreign Keys=True;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT text_content FROM host_artifact_contents WHERE owner_principal_id=$owner AND artifact_id=$artifact;";
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$artifact", artifactId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task DeleteHostArtifactReceiptAsync(string owner, string artifactId)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "product.db")};Foreign Keys=True;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM host_artifact_receipts WHERE owner_principal_id=$owner AND artifact_id=$artifact;";
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$artifact", artifactId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private async Task MarkLeaseAsLegacyNotStartedRequeueAsync(string owner, string leaseId)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "product.db")};Foreign Keys=True;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE host_work_leases
            SET state='EXPIRED',failure_code='reconciled_not_started',version=version+1
            WHERE owner_principal_id=$owner AND lease_id=$lease;
            """;
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$lease", leaseId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private async Task SeedHostArtifactMetadataAsync(string owner, string runId, string leaseId, int count)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "product.db")};Foreign Keys=True;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE sequence(value) AS (
                SELECT 1
                UNION ALL
                SELECT value + 1 FROM sequence WHERE value < $count
            )
            INSERT INTO host_artifacts(
                owner_principal_id,artifact_id,run_id,lease_id,action_id,kind,media_type,
                summary,size_bytes,sha256,retention,content_state,redacted,truncated,
                created_at,expires_at,version)
            SELECT $owner,printf('artifact-seed-%03d',value),$run,$lease,NULL,'TEXT','text/plain',
                   'seed',0,$sha256,'RUN','AVAILABLE',0,0,$createdAt,NULL,1
            FROM sequence;
            """;
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$lease", leaseId);
        command.Parameters.AddWithValue("$count", count);
        command.Parameters.AddWithValue("$sha256", Convert.ToHexStringLower(SHA256.HashData([])));
        command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
        Assert.Equal(count, await command.ExecuteNonQueryAsync());
    }

    private async Task<long> ReadAcceptedHostMessageCountAsync(string owner, string hostId, string messageId)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "product.db")};Foreign Keys=True;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM host_accepted_messages WHERE owner_principal_id=$owner AND host_id=$host AND message_id=$message;";
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$host", hostId);
        command.Parameters.AddWithValue("$message", messageId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> ReadLastAcceptedHostSequenceAsync(string owner, string hostId)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "product.db")};Foreign Keys=True;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT last_accepted_sequence FROM remote_hosts WHERE owner_principal_id=$owner AND host_id=$host;";
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$host", hostId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task ForceLeaseExpiryAsync(string owner, string leaseId, DateTimeOffset executeUntil)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "product.db")};Foreign Keys=True;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE host_work_leases SET execute_until=$executeUntil WHERE owner_principal_id=$owner AND lease_id=$lease;";
        command.Parameters.AddWithValue("$executeUntil", executeUntil.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$lease", leaseId);
        await command.ExecuteNonQueryAsync();
    }

    private async Task DeleteSchedulerLeaseAsync(string owner, string runId, long fence)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "product.db")};Foreign Keys=True;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM scheduler_leases WHERE owner_principal_id=$owner AND run_id=$run AND fence=$fence;";
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$fence", fence);
        await command.ExecuteNonQueryAsync();
    }

    private async Task RevokeActiveHostCapabilityGrantAsync(string owner, string hostId)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "product.db")};Foreign Keys=True;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE host_capability_grants SET revoked_at=$now WHERE owner_principal_id=$owner AND host_id=$host AND revoked_at IS NULL;";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$host", hostId);
        await command.ExecuteNonQueryAsync();
    }

    private static string RequestHash<T>(T body)
        => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(body)));

    private sealed class NoopTransport : IHttpTransport
    {
        public Task<TransportResponse> SendAsync(string method, string url, IReadOnlyDictionary<string, string> headers, string? body, CancellationToken cancellationToken = default)
            => Task.FromResult(new TransportResponse(502, new Dictionary<string, string>(), "{}"));
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}