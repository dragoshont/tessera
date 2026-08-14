using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Tessera.Core.Kernel;
using Tessera.Core.Product;
using Xunit;

namespace Tessera.Persistence.Sqlite.Tests;

public sealed class RemoteHostPersistenceTests
{
    [Fact]
    public async Task Pairing_secrets_have_256_bit_entropy_and_only_the_hash_is_persisted()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync();
        var owner = await AddOwnerAsync(store, "secret-owner");
        var first = RemoteHostValidation.CreateClaimSecret();
        var second = RemoteHostValidation.CreateClaimSecret();
        Assert.NotEqual(first, second);
        Assert.Equal(32, Decode(first).Length);
        Assert.Equal(32, Decode(second).Length);

        var now = DateTimeOffset.UtcNow;
        var hash = RemoteHostValidation.HashClaimSecret(first);
        var created = await CreatePairingAsync(store, owner, "pairing-secret", hash, now, now.AddMinutes(5), "pairing-secret-key");
        Assert.False(created.Replayed);
        var replay = await CreatePairingAsync(store, owner, "pairing-retry-ignored", hash, now, now.AddMinutes(5), "pairing-secret-key");
        Assert.True(replay.Replayed);
        Assert.Equal(created.Receipt!.ResponseBodyJson, replay.Receipt!.ResponseBodyJson);
        var conflict = await CreatePairingAsync(store, owner, "pairing-conflict-ignored",
            RemoteHostValidation.HashClaimSecret(second), now, now.AddMinutes(5), "pairing-secret-key");
        Assert.Equal("idempotency_conflict", conflict.Error);
        await using var connection = new SqliteConnection($"Data Source={database.Path};Mode=ReadOnly");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT claim_secret_hash,requested_host_json FROM host_pairings WHERE pairing_id='pairing-secret';";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(RemoteHostValidation.HashClaimSecret(first), reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
        Assert.DoesNotContain(first, await File.ReadAllTextAsync(database.Path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Claim_enforces_ttl_attempt_bound_and_single_consumption()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync();
        var owner = await AddOwnerAsync(store, "claim-owner");
        var now = DateTimeOffset.UtcNow;
        var expiredSecret = RemoteHostValidation.CreateClaimSecret();
        await CreatePairingAsync(store, owner, "pairing-expired", RemoteHostValidation.HashClaimSecret(expiredSecret), now, now.AddMinutes(1));
        var expired = await ClaimAsync(store, "pairing-expired", expiredSecret, Claim(), now.AddMinutes(2), "expired-claim");
        Assert.Equal("pairing_expired", expired.Error);
        Assert.Equal(HostPairingStates.Expired, (await store.GetHostPairingAsync(owner, "pairing-expired"))!.State);

        var cancelSecret = RemoteHostValidation.CreateClaimSecret();
        await CreatePairingAsync(store, owner, "pairing-cancel-expired", RemoteHostValidation.HashClaimSecret(cancelSecret), now, now.AddMinutes(1));
        var cancelExpired = await CancelAsync(
            store, owner, "pairing-cancel-expired", 1, now.AddMinutes(2), "cancel-expired");
        Assert.Equal("pairing_expired", cancelExpired.Error);

        var boundedSecret = RemoteHostValidation.CreateClaimSecret();
        await CreatePairingAsync(store, owner, "pairing-bounded", RemoteHostValidation.HashClaimSecret(boundedSecret), now, now.AddMinutes(5));
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var rejected = await ClaimAsync(store, "pairing-bounded",
                RemoteHostValidation.CreateClaimSecret(), Claim(), now.AddSeconds(attempt), $"bounded-{attempt}");
            Assert.Equal(attempt == 5 ? "pairing_attempts_exceeded" : "pairing_invalid_request", rejected.Error);
        }
        var bounded = await store.GetHostPairingAsync(owner, "pairing-bounded");
        Assert.Equal(5, bounded!.FailedClaims);
        Assert.Equal(HostPairingStates.Canceled, bounded.State);
        Assert.Equal("pairing_canceled", (await ClaimAsync(store,
            "pairing-bounded", boundedSecret, Claim(), now.AddSeconds(6), "bounded-after")).Error);

        var consumedSecret = RemoteHostValidation.CreateClaimSecret();
        await CreatePairingAsync(store, owner, "pairing-consumed", RemoteHostValidation.HashClaimSecret(consumedSecret), now, now.AddMinutes(5));
        Assert.True((await ClaimAsync(store, "pairing-consumed", consumedSecret, Claim(), now.AddSeconds(1), "consumed-first")).Succeeded);
        Assert.Equal("pairing_consumed", (await ClaimAsync(store,
            "pairing-consumed", consumedSecret, Claim(), now.AddSeconds(2), "consumed-second")).Error);
    }

    [Fact]
    public async Task Confirmation_is_owner_version_code_and_requested_grant_scoped_and_revocation_preserves_history()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync();
        var owner = await AddOwnerAsync(store, "host-owner");
        var other = await AddOwnerAsync(store, "other-owner");
        var now = DateTimeOffset.UtcNow;
        var secret = RemoteHostValidation.CreateClaimSecret();
        await CreatePairingAsync(store, owner, "pairing-confirm", RemoteHostValidation.HashClaimSecret(secret), now, now.AddMinutes(5));
        var claimed = (await ClaimAsync(store, "pairing-confirm", secret, Claim(), now.AddSeconds(1), "confirm-claim")).Pairing!;
        Assert.Null(await store.GetHostPairingAsync(other, "pairing-confirm"));
        Assert.Empty(await store.ListRemoteHostsAsync(other));

        var mismatch = await ConfirmAsync(store, owner, "pairing-confirm", claimed.Version, "000000",
            "host-confirm", "My Mac", [], [], now.AddSeconds(2), "confirm-mismatch");
        var expectedCode = RemoteHostValidation.DeriveConfirmationCode("pairing-confirm", claimed.RequestedHost!.PublicKey);
        if (expectedCode == "000000")
            Assert.True(mismatch.Succeeded);
        else
            Assert.Equal("pairing_confirmation_mismatch", mismatch.Error);
        if (mismatch.Succeeded) return;

        var afterMismatch = (await store.GetHostPairingAsync(owner, "pairing-confirm"))!;
        var notRequested = await ConfirmAsync(store, owner, "pairing-confirm", afterMismatch.Version, expectedCode,
            "host-confirm", "My Mac", [], [new("repo-other", "READ_ONLY")],
            now.AddSeconds(3), "confirm-not-requested");
        Assert.Equal("pairing_grant_not_requested", notRequested.Error);

        var confirmed = await ConfirmAsync(store, owner, "pairing-confirm", afterMismatch.Version, expectedCode,
            "host-confirm", "My Mac", [new("host.repo.identity", "1")], [new("repo-main", "READ_ONLY")],
            now.AddSeconds(4), "confirm-success");
        Assert.True(confirmed.Succeeded);
        Assert.Null(await store.GetRemoteHostDetailAsync(other, "host-confirm"));
        var detail = confirmed.Host!;
        Assert.Single(detail.CapabilityGrants, grant => grant.RevokedAt is null);
        Assert.Single(detail.ResourceGrants, grant => grant.RevokedAt is null);

        var replaced = await UpdateGrantsAsync(
            store, owner, "host-confirm", detail.Host.Version, [], [], now.AddSeconds(5), "remove-grants");
        Assert.Null(replaced.Error);
        Assert.All(replaced.Host!.CapabilityGrants, grant => Assert.NotNull(grant.RevokedAt));
        Assert.All(replaced.Host.ResourceGrants, grant => Assert.NotNull(grant.RevokedAt));

        var regranted = await UpdateGrantsAsync(store, owner, "host-confirm", replaced.Host.Host.Version,
            [new("host.repo.identity", "1")], [new("repo-main", "READ_ONLY")],
            now.AddSeconds(6), "regrant-host");
        Assert.Null(regranted.Error);
        Assert.Equal([1L, 2L], regranted.Host!.CapabilityGrants.Select(grant => grant.Version));
        Assert.Equal([1L, 2L], regranted.Host.ResourceGrants.Select(grant => grant.Version));
        Assert.Single(regranted.Host.CapabilityGrants, grant => grant.RevokedAt is null && grant.Version == 2);
        Assert.Single(regranted.Host.ResourceGrants, grant => grant.RevokedAt is null && grant.Version == 2);

        var crossOwnerUpdate = await UpdateGrantsAsync(store, other, "host-confirm",
            regranted.Host.Host.Version, [], [], now.AddSeconds(7), "other-update");
        Assert.Equal("host_not_found", crossOwnerUpdate.Error);
        var crossOwnerRevoke = await RevokeAsync(store, other, "host-confirm",
            regranted.Host.Host.Version, now.AddSeconds(8), "other-revoke");
        Assert.Equal("host_not_found", crossOwnerRevoke.Error);

        var revoked = await RevokeAsync(
            store, owner, "host-confirm", regranted.Host.Host.Version, now.AddSeconds(9), "revoke-host");
        Assert.Null(revoked.Error);
        Assert.Equal(RemoteHostLifecycles.Revoked, revoked.Host!.Host.Lifecycle);
        Assert.Single(revoked.Host.Capabilities);
        Assert.Single(revoked.Host.Resources);
        Assert.Equal(2, revoked.Host.CapabilityGrants.Count);
        Assert.Equal(2, revoked.Host.ResourceGrants.Count);
        Assert.All(revoked.Host.CapabilityGrants, grant => Assert.NotNull(grant.RevokedAt));
        Assert.All(revoked.Host.ResourceGrants, grant => Assert.NotNull(grant.RevokedAt));
    }

    [Fact]
    public async Task Confirmation_mismatch_is_versioned_and_five_failures_cancel_the_pairing()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync();
        var owner = await AddOwnerAsync(store, "confirmation-owner");
        var now = DateTimeOffset.UtcNow;
        var secret = RemoteHostValidation.CreateClaimSecret();
        await CreatePairingAsync(store, owner, "pairing-confirm-bounded",
            RemoteHostValidation.HashClaimSecret(secret), now, now.AddMinutes(5));
        var pairing = (await ClaimAsync(store,
            "pairing-confirm-bounded", secret, Claim(), now.AddSeconds(1), "bounded-confirm-claim")).Pairing!;
        var expected = RemoteHostValidation.DeriveConfirmationCode(pairing.PairingId, pairing.RequestedHost!.PublicKey);
        var mismatch = expected == "000000" ? "000001" : "000000";

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var result = await ConfirmAsync(store,
                owner, pairing.PairingId, pairing.Version, mismatch, "host-bounded", "My Mac",
                [], [], now.AddSeconds(attempt + 1), $"confirmation-{attempt}");
            Assert.Equal(attempt == 5 ? "pairing_attempts_exceeded" : "pairing_confirmation_mismatch", result.Error);
            pairing = (await store.GetHostPairingAsync(owner, pairing.PairingId))!;
            Assert.Equal(attempt, pairing.FailedConfirmations);
        }

        Assert.Equal(HostPairingStates.Canceled, pairing.State);
        Assert.Equal("pairing_canceled", (await ConfirmAsync(store,
            owner, pairing.PairingId, pairing.Version, expected, "host-bounded", "My Mac",
            [], [], now.AddSeconds(7), "confirmation-after")).Error);
    }

    [Fact]
    public async Task Concurrent_create_and_confirm_replay_exactly_and_changed_create_conflicts()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync();
        var owner = await AddOwnerAsync(store, "concurrent-owner");
        var now = DateTimeOffset.UtcNow;
        var secret = RemoteHostValidation.CreateClaimSecret();
        var secretHash = RemoteHostValidation.HashClaimSecret(secret);
        var exactHash = RequestHash(new { claimSecretHash = secretHash });

        var exactCreates = await Task.WhenAll(
            store.CreateHostPairingAsync(owner, "pairing-concurrent-a", secretHash,
                "concurrent-create", exactHash, now, now.AddMinutes(5)),
            database.CreateStore().CreateHostPairingAsync(owner, "pairing-concurrent-b", secretHash,
                "concurrent-create", exactHash, now, now.AddMinutes(5)));
        Assert.All(exactCreates, result => Assert.NotNull(result.Receipt));
        Assert.Equal(exactCreates[0].Receipt!.ResponseBodyJson, exactCreates[1].Receipt!.ResponseBodyJson);
        Assert.Single(exactCreates, result => result.Replayed);

        var changedOwner = await AddOwnerAsync(store, "changed-concurrent-owner");
        var otherSecretHash = RemoteHostValidation.HashClaimSecret(RemoteHostValidation.CreateClaimSecret());
        var changedCreates = await Task.WhenAll(
            store.CreateHostPairingAsync(changedOwner, "pairing-changed-a", secretHash,
                "changed-create", RequestHash(new { claimSecretHash = secretHash }), now, now.AddMinutes(5)),
            database.CreateStore().CreateHostPairingAsync(changedOwner, "pairing-changed-b", otherSecretHash,
                "changed-create", RequestHash(new { claimSecretHash = otherSecretHash }), now, now.AddMinutes(5)));
        Assert.Single(changedCreates, result => result.Succeeded);
        Assert.Single(changedCreates, result => result.Error == "idempotency_conflict");

        var createdPairing = exactCreates.Single(result => !result.Replayed).Pairing!;
        var claimed = (await ClaimAsync(store, createdPairing.PairingId, secret, Claim(),
            now.AddSeconds(1), "concurrent-claim")).Pairing!;
        var code = RemoteHostValidation.DeriveConfirmationCode(
            claimed.PairingId, claimed.RequestedHost!.PublicKey);
        var confirmHash = RequestHash(new
        {
            expectedVersion = claimed.Version,
            confirmationCode = code,
            displayName = "Concurrent Mac",
            capabilityGrants = Array.Empty<HostCapabilityGrantRequest>(),
            resourceGrants = Array.Empty<HostResourceGrantRequest>(),
        });
        var confirms = await Task.WhenAll(
            store.ConfirmHostPairingAsync(owner, claimed.PairingId, claimed.Version, code,
                "host-concurrent-a", "Concurrent Mac", [], [], "concurrent-confirm", confirmHash,
                now.AddSeconds(2)),
            database.CreateStore().ConfirmHostPairingAsync(owner, claimed.PairingId, claimed.Version, code,
                "host-concurrent-b", "Concurrent Mac", [], [], "concurrent-confirm", confirmHash,
                now.AddSeconds(2)));
        Assert.All(confirms, result => Assert.NotNull(result.Receipt));
        Assert.Equal(confirms[0].Receipt!.ResponseBodyJson, confirms[1].Receipt!.ResponseBodyJson);
        Assert.Single(confirms, result => result.Replayed);
        Assert.Single(await store.ListRemoteHostsAsync(owner));
    }

    [Fact]
    public async Task Deterministic_claim_rejection_replays_without_increment_and_precommit_failure_rolls_back()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync();
        var owner = await AddOwnerAsync(store, "rollback-owner");
        var now = DateTimeOffset.UtcNow;
        var secret = RemoteHostValidation.CreateClaimSecret();
        await CreatePairingAsync(store, owner, "pairing-rejection",
            RemoteHostValidation.HashClaimSecret(secret), now, now.AddMinutes(5));

        var wrongSecret = RemoteHostValidation.CreateClaimSecret();
        var claim = Claim();
        var first = await ClaimAsync(store, "pairing-rejection", wrongSecret, claim,
            now.AddSeconds(1), "rejected-claim");
        var replay = await ClaimAsync(store, "pairing-rejection", wrongSecret, claim,
            now.AddSeconds(2), "rejected-claim");
        Assert.Equal(400, first.Receipt!.ResponseStatus);
        Assert.Equal(first.Receipt.ResponseBodyJson, replay.Receipt!.ResponseBodyJson);
        Assert.True(replay.Replayed);
        Assert.Equal(1, (await store.GetHostPairingAsync(owner, "pairing-rejection"))!.FailedClaims);

        store.RemoteHostBeforeCommitAsync = _ => throw new InvalidOperationException("injected-before-commit");
        await Assert.ThrowsAsync<InvalidOperationException>(() => ClaimAsync(
            store, "pairing-rejection", secret, claim, now.AddSeconds(3), "rollback-claim"));
        store.RemoteHostBeforeCommitAsync = null;

        var afterRollback = (await store.GetHostPairingAsync(owner, "pairing-rejection"))!;
        Assert.Equal(HostPairingStates.Issued, afterRollback.State);
        Assert.Null(await store.GetIdempotencyReceiptAsync(
            owner, "host-pairing-claim", "rollback-claim"));
        Assert.True((await ClaimAsync(store, "pairing-rejection", secret, claim,
            now.AddSeconds(4), "rollback-claim")).Succeeded);
    }

    [Fact]
    public async Task Reused_claim_hash_is_receipted_replayable_and_does_not_create_a_second_pairing()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync();
        var owner = await AddOwnerAsync(store, "reused-hash-owner");
        var now = DateTimeOffset.UtcNow;
        var hash = RemoteHostValidation.HashClaimSecret(RemoteHostValidation.CreateClaimSecret());
        await CreatePairingAsync(store, owner, "pairing-original", hash, now, now.AddMinutes(5));

        var rejected = await CreatePairingAsync(store, owner, "pairing-reused", hash,
            now.AddSeconds(1), now.AddMinutes(5), "reused-hash");
        Assert.Equal("pairing_consumed", rejected.Error);
        Assert.Equal(409, rejected.Receipt!.ResponseStatus);
        Assert.Equal(rejected.Receipt, await store.GetIdempotencyReceiptAsync(
            owner, "host-pairing-create", "reused-hash"));

        var replay = await CreatePairingAsync(store, owner, "pairing-replay-ignored", hash,
            now.AddSeconds(2), now.AddMinutes(5), "reused-hash");
        Assert.True(replay.Replayed);
        Assert.Equal(rejected.Receipt.ResponseBodyJson, replay.Receipt!.ResponseBodyJson);
        var changed = await CreatePairingAsync(store, owner, "pairing-changed-ignored",
            RemoteHostValidation.HashClaimSecret(RemoteHostValidation.CreateClaimSecret()),
            now.AddSeconds(3), now.AddMinutes(5), "reused-hash");
        Assert.Equal("idempotency_conflict", changed.Error);

        await using var connection = new SqliteConnection($"Data Source={database.Path};Mode=ReadOnly");
        await connection.OpenAsync();
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT count(*) FROM host_pairings WHERE owner_principal_id=$owner;";
        count.Parameters.AddWithValue("$owner", owner);
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Duplicate_public_key_confirmation_is_receipted_replayable_and_never_creates_a_second_host()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync();
        var owner = await AddOwnerAsync(store, "duplicate-key-owner");
        var now = DateTimeOffset.UtcNow;
        var claim = Claim();

        var firstSecret = RemoteHostValidation.CreateClaimSecret();
        await CreatePairingAsync(store, owner, "pairing-first-key",
            RemoteHostValidation.HashClaimSecret(firstSecret), now, now.AddMinutes(5));
        var firstClaimed = (await ClaimAsync(store, "pairing-first-key", firstSecret, claim,
            now.AddSeconds(1), "claim-first-key")).Pairing!;
        var firstCode = RemoteHostValidation.DeriveConfirmationCode(firstClaimed.PairingId, claim.PublicKey);
        Assert.True((await ConfirmAsync(store, owner, firstClaimed.PairingId, firstClaimed.Version,
            firstCode, "host-first-key", "First Mac", [], [], now.AddSeconds(2), "confirm-first-key")).Succeeded);

        var secondSecret = RemoteHostValidation.CreateClaimSecret();
        await CreatePairingAsync(store, owner, "pairing-second-key",
            RemoteHostValidation.HashClaimSecret(secondSecret), now.AddSeconds(3), now.AddMinutes(5));
        var secondClaimed = (await ClaimAsync(store, "pairing-second-key", secondSecret, claim,
            now.AddSeconds(4), "claim-second-key")).Pairing!;
        var secondCode = RemoteHostValidation.DeriveConfirmationCode(secondClaimed.PairingId, claim.PublicKey);
        var rejected = await ConfirmAsync(store, owner, secondClaimed.PairingId, secondClaimed.Version,
            secondCode, "host-second-key", "Second Mac", [], [], now.AddSeconds(5), "confirm-second-key");
        Assert.Equal("pairing_consumed", rejected.Error);
        Assert.Equal(409, rejected.Receipt!.ResponseStatus);
        Assert.Equal(rejected.Receipt, await store.GetIdempotencyReceiptAsync(
            owner, "host-pairing-confirm", "confirm-second-key"));

        var replay = await ConfirmAsync(store, owner, secondClaimed.PairingId, secondClaimed.Version,
            secondCode, "host-replay-ignored", "Second Mac", [], [], now.AddSeconds(6), "confirm-second-key");
        Assert.True(replay.Replayed);
        Assert.Equal(rejected.Receipt.ResponseBodyJson, replay.Receipt!.ResponseBodyJson);
        var changed = await ConfirmAsync(store, owner, secondClaimed.PairingId, secondClaimed.Version,
            secondCode, "host-changed-ignored", "Changed Mac", [], [], now.AddSeconds(7), "confirm-second-key");
        Assert.Equal("idempotency_conflict", changed.Error);
        Assert.Single(await store.ListRemoteHostsAsync(owner));
    }

    [Fact]
    public async Task Grant_update_and_receipt_roll_back_together_before_commit()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync();
        var owner = await AddOwnerAsync(store, "grant-rollback-owner");
        var now = DateTimeOffset.UtcNow;
        var secret = RemoteHostValidation.CreateClaimSecret();
        await CreatePairingAsync(store, owner, "pairing-grant-rollback",
            RemoteHostValidation.HashClaimSecret(secret), now, now.AddMinutes(5));
        var claimed = (await ClaimAsync(store, "pairing-grant-rollback", secret, Claim(),
            now.AddSeconds(1), "claim-grant-rollback")).Pairing!;
        var code = RemoteHostValidation.DeriveConfirmationCode(claimed.PairingId, claimed.RequestedHost!.PublicKey);
        var confirmed = await ConfirmAsync(store, owner, claimed.PairingId, claimed.Version, code,
            "host-grant-rollback", "Rollback Mac", [new("host.repo.identity", "1")],
            [new("repo-main", "READ_ONLY")], now.AddSeconds(2), "confirm-grant-rollback");

        store.RemoteHostBeforeCommitAsync = _ => throw new InvalidOperationException("injected-before-commit");
        await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateGrantsAsync(store, owner,
            "host-grant-rollback", confirmed.Host!.Host.Version, [], [], now.AddSeconds(3), "rollback-grants"));
        store.RemoteHostBeforeCommitAsync = null;

        var unchanged = (await store.GetRemoteHostDetailAsync(owner, "host-grant-rollback"))!;
        Assert.Equal(confirmed.Host!.Host.Version, unchanged.Host.Version);
        Assert.Single(unchanged.CapabilityGrants, grant => grant.RevokedAt is null);
        Assert.Single(unchanged.ResourceGrants, grant => grant.RevokedAt is null);
        Assert.Null(await store.GetIdempotencyReceiptAsync(owner, "host-grants", "rollback-grants"));
        Assert.True((await UpdateGrantsAsync(store, owner, "host-grant-rollback",
            unchanged.Host.Version, [], [], now.AddSeconds(4), "rollback-grants")).Succeeded);
    }

    [Fact]
    public async Task Grant_update_does_not_claim_an_idle_offline_host_is_online()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync();
        var owner = await AddOwnerAsync(store, "offline-grant-owner");
        var now = DateTimeOffset.UtcNow;
        var secret = RemoteHostValidation.CreateClaimSecret();
        await CreatePairingAsync(store, owner, "pairing-offline-grant",
            RemoteHostValidation.HashClaimSecret(secret), now, now.AddMinutes(5));
        var claimed = (await ClaimAsync(store, "pairing-offline-grant", secret, Claim(),
            now.AddSeconds(1), "claim-offline-grant")).Pairing!;
        var code = RemoteHostValidation.DeriveConfirmationCode(claimed.PairingId, claimed.RequestedHost!.PublicKey);
        var confirmed = await ConfirmAsync(store, owner, claimed.PairingId, claimed.Version, code,
            "host-offline-grant", "Offline Mac", [new("host.repo.identity", "1")],
            [new("repo-main", "READ_ONLY")], now.AddSeconds(2), "confirm-offline-grant");
        Assert.Equal(RemoteHostLifecycles.Offline, confirmed.Host!.Host.Lifecycle);

        var changed = await UpdateGrantsAsync(store, owner, "host-offline-grant",
            confirmed.Host.Host.Version, [], [], now.AddSeconds(3), "update-offline-grant");

        Assert.True(changed.Succeeded);
        Assert.Equal(RemoteHostLifecycles.Offline, changed.Host!.Host.Lifecycle);
        Assert.Equal("OFFLINE", changed.Host.Host.ConnectionStatus);
    }

    private static HostClaim Claim()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(false);
        var jwk = RemoteHostValidation.NormalizeP256PublicJwk(
            $$"""{"kty":"EC","crv":"P-256","x":"{{Base64Url(parameters.Q.X!)}}","y":"{{Base64Url(parameters.Q.Y!)}}"}""");
        return new(jwk, "KEYCHAIN_THIS_DEVICE_ONLY", "macOS", "arm64", "1.0.0", "1",
            [new("host.repo.identity", "1", new string('a', 64), "READ_ONLY")],
            [new("repo-main", "REPOSITORY", "Tessera", new string('b', 64), "AVAILABLE")]);
    }

    private static async Task<string> AddOwnerAsync(SqliteKernelStore store, string subject)
    {
        var principal = PrincipalRef.Create("https://issuer.example", "tenant", subject, subject, DateTimeOffset.UtcNow);
        await store.AddAsync(principal);
        return principal.PrincipalId;
    }

    private static Task<HostPairingCreateResult> CreatePairingAsync(
        SqliteKernelStore store, string owner, string pairingId, string claimSecretHash,
        DateTimeOffset now, DateTimeOffset expiresAt, string? idempotencyKey = null)
    {
        var key = idempotencyKey ?? $"key-{pairingId}";
        var requestHash = Convert.ToHexStringLower(SHA256.HashData(
            System.Text.Encoding.ASCII.GetBytes(claimSecretHash)));
        return store.CreateHostPairingAsync(
            owner, pairingId, claimSecretHash, key, requestHash, now, expiresAt);
    }

    private static Task<HostPairingReceiptMutation> ClaimAsync(
        SqliteKernelStore store, string pairingId, string claimSecret, HostClaim claim,
        DateTimeOffset now, string idempotencyKey)
    {
        var requestHash = RequestHash(new { claimSecret, claim });
        return store.ClaimHostPairingAsync(
            pairingId, claimSecret, claim, idempotencyKey, requestHash, now);
    }

    private static Task<HostPairingReceiptMutation> ConfirmAsync(
        SqliteKernelStore store, string owner, string pairingId, long expectedVersion,
        string confirmationCode, string hostId, string displayName,
        IReadOnlyList<HostCapabilityGrantRequest> capabilities,
        IReadOnlyList<HostResourceGrantRequest> resources, DateTimeOffset now, string idempotencyKey)
    {
        var requestHash = RequestHash(new
        {
            expectedVersion, confirmationCode, displayName, capabilities, resources,
        });
        return store.ConfirmHostPairingAsync(owner, pairingId, expectedVersion, confirmationCode,
            hostId, displayName, capabilities, resources, idempotencyKey, requestHash, now);
    }

    private static Task<HostPairingReceiptMutation> CancelAsync(
        SqliteKernelStore store, string owner, string pairingId, long expectedVersion,
        DateTimeOffset now, string idempotencyKey)
        => store.CancelHostPairingAsync(owner, pairingId, expectedVersion, idempotencyKey,
            RequestHash(new { expectedVersion }), now);

    private static Task<RemoteHostReceiptMutation> UpdateGrantsAsync(
        SqliteKernelStore store, string owner, string hostId, long expectedVersion,
        IReadOnlyList<HostCapabilityGrantRequest> capabilities,
        IReadOnlyList<HostResourceGrantRequest> resources, DateTimeOffset now, string idempotencyKey)
        => store.UpdateRemoteHostGrantsAsync(owner, hostId, expectedVersion, capabilities, resources,
            idempotencyKey, RequestHash(new { expectedVersion, capabilities, resources }), now);

    private static Task<RemoteHostReceiptMutation> RevokeAsync(
        SqliteKernelStore store, string owner, string hostId, long expectedVersion,
        DateTimeOffset now, string idempotencyKey)
        => store.RevokeRemoteHostAsync(owner, hostId, expectedVersion, idempotencyKey,
            RequestHash(new { expectedVersion }), now);

    private static string RequestHash<T>(T request)
        => Convert.ToHexStringLower(SHA256.HashData(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(request)));

    private static byte[] Decode(string value)
        => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + "=");

    private static string Base64Url(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}