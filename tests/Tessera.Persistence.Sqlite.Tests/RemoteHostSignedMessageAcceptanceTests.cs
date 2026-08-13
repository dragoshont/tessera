using System.Security.Cryptography;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tessera.Core.Kernel;
using Tessera.Core.Product;
using Xunit;

namespace Tessera.Persistence.Sqlite.Tests;

public sealed class RemoteHostSignedMessageAcceptanceTests
{
    private static readonly BigInteger P256Order = new(
        Convert.FromHexString("FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551"),
        isUnsigned: true,
        isBigEndian: true);
    private static readonly BigInteger P256HalfOrder = P256Order / 2;

    [Fact]
    public async Task Exact_replay_returns_stored_receipt_and_changed_duplicate_returns_host_replay()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync();
        var owner = await AddOwnerAsync(store, "accept-owner");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = await CreateOnlineHostAsync(database.Path, store, owner, key, "host-accept", lastAcceptedSequence: 0);
        var envelope = Envelope(key, "host-accept", HostAcceptedMessageOperations.Poll, "-", "message-1", 1, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Array.Empty<byte>());

        var first = await store.AcceptSignedHostMessageAsync(
            envelope,
            DateTimeOffset.FromUnixTimeSeconds(envelope.UnixTimestampSeconds),
            (_, _, _, _) => Task.FromResult(new HostMessageBusinessResponse(200, "{\"ok\":true}")));
        Assert.True(first.Succeeded);

        await SetLifecycleAsync(database.Path, owner, envelope.HostId, RemoteHostLifecycles.Revoked);

        var replay = await store.AcceptSignedHostMessageAsync(
            envelope,
            DateTimeOffset.FromUnixTimeSeconds(envelope.UnixTimestampSeconds + 1),
            (_, _, _, _) => Task.FromResult(new HostMessageBusinessResponse(500, "{\"unexpected\":true}")));
        Assert.True(replay.Succeeded);
        Assert.True(replay.Replayed);
        Assert.Equal(first.Receipt, replay.Receipt);

        var changedEnvelope = Envelope(
            key,
            "host-accept",
            HostAcceptedMessageOperations.LeaseAck,
            "lease-123",
            "message-1",
            1,
            envelope.UnixTimestampSeconds,
            Array.Empty<byte>());
        var changed = await store.AcceptSignedHostMessageAsync(
            changedEnvelope,
            DateTimeOffset.FromUnixTimeSeconds(envelope.UnixTimestampSeconds + 2),
            (_, _, _, _) => Task.FromResult(new HostMessageBusinessResponse(200, "{}")));
        Assert.Equal(RemoteHostSignedRequestErrors.HostReplay, changed.Error);
    }

    [Fact]
    public async Task Sequence_gaps_out_of_order_and_overflow_fail_without_consuming_state()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync();
        var owner = await AddOwnerAsync(store, "sequence-owner");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = await CreateOnlineHostAsync(database.Path, store, owner, key, "host-sequence", lastAcceptedSequence: 1);

        var gap = await store.AcceptSignedHostMessageAsync(
            Envelope(key, "host-sequence", HostAcceptedMessageOperations.Poll, "-", "message-gap", 3, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Array.Empty<byte>()),
            DateTimeOffset.UtcNow,
            (_, _, _, _) => Task.FromResult(new HostMessageBusinessResponse(200, "{}")));
        Assert.Equal(RemoteHostSignedRequestErrors.HostSequenceInvalid, gap.Error);

        var outOfOrder = await store.AcceptSignedHostMessageAsync(
            Envelope(key, "host-sequence", HostAcceptedMessageOperations.Poll, "-", "message-old", 1, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Array.Empty<byte>()),
            DateTimeOffset.UtcNow,
            (_, _, _, _) => Task.FromResult(new HostMessageBusinessResponse(200, "{}")));
        Assert.Equal(RemoteHostSignedRequestErrors.HostSequenceInvalid, outOfOrder.Error);

        await SetLastAcceptedSequenceAsync(database.Path, owner, "host-sequence", long.MaxValue);
        var overflow = await store.AcceptSignedHostMessageAsync(
            Envelope(key, "host-sequence", HostAcceptedMessageOperations.Poll, "-", "message-overflow", long.MaxValue, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Array.Empty<byte>()),
            DateTimeOffset.UtcNow,
            (_, _, _, _) => Task.FromResult(new HostMessageBusinessResponse(200, "{}")));
        Assert.Equal(RemoteHostSignedRequestErrors.HostSequenceInvalid, overflow.Error);

        Assert.Equal(long.MaxValue, await ReadLastAcceptedSequenceAsync(database.Path, owner, "host-sequence"));
        Assert.Equal(0, await CountAcceptedMessagesAsync(database.Path, owner, "host-sequence"));
    }

    [Fact]
    public async Task Duplicate_cross_owner_host_id_fails_closed_without_consuming_either_host()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync();
        var firstOwner = await AddOwnerAsync(store, "collision-owner-a");
        var secondOwner = await AddOwnerAsync(store, "collision-owner-b");
        using var firstKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var secondKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await CreateOnlineHostAsync(
            database.Path, store, firstOwner, firstKey, "host-collision", 0, "pairing-collision-a");
        await CreateOnlineHostAsync(
            database.Path, store, secondOwner, secondKey, "host-collision", 0, "pairing-collision-b");

        var envelope = Envelope(firstKey, "host-collision", HostAcceptedMessageOperations.Poll,
            "-", "message-collision", 1, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), []);
        var result = await store.AcceptSignedHostMessageAsync(
            envelope,
            DateTimeOffset.FromUnixTimeSeconds(envelope.UnixTimestampSeconds),
            (_, _, _, _) => Task.FromResult(new HostMessageBusinessResponse(200, "{}")));

        Assert.Equal(RemoteHostSignedRequestErrors.HostAuthInvalid, result.Error);
        Assert.Equal(0, await ReadLastAcceptedSequenceAsync(database.Path, firstOwner, "host-collision"));
        Assert.Equal(0, await ReadLastAcceptedSequenceAsync(database.Path, secondOwner, "host-collision"));
        Assert.Equal(0, await CountAcceptedMessagesAsync(database.Path, firstOwner, "host-collision"));
        Assert.Equal(0, await CountAcceptedMessagesAsync(database.Path, secondOwner, "host-collision"));
    }

    [Fact]
    public async Task Lifecycle_key_protocol_and_skew_fail_with_redacted_errors()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync();
        var owner = await AddOwnerAsync(store, "rejection-owner");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = await CreateOnlineHostAsync(database.Path, store, owner, key, "host-rejections", lastAcceptedSequence: 0);
        var now = DateTimeOffset.UtcNow;

        await SetLifecycleAsync(database.Path, owner, "host-rejections", RemoteHostLifecycles.Offline);
        Assert.Equal(RemoteHostSignedRequestErrors.HostAuthInvalid, (await store.AcceptSignedHostMessageAsync(
            Envelope(key, "host-rejections", HostAcceptedMessageOperations.Poll, "-", "message-offline", 1, now.ToUnixTimeSeconds(), Array.Empty<byte>()),
            now,
            (_, _, _, _) => Task.FromResult(new HostMessageBusinessResponse(200, "{}")))).Error);

        await SetLifecycleAsync(database.Path, owner, "host-rejections", RemoteHostLifecycles.Revoked);
        Assert.Equal(RemoteHostSignedRequestErrors.HostRevoked, (await store.AcceptSignedHostMessageAsync(
            Envelope(key, "host-rejections", HostAcceptedMessageOperations.Poll, "-", "message-revoked", 1, now.ToUnixTimeSeconds(), Array.Empty<byte>()),
            now,
            (_, _, _, _) => Task.FromResult(new HostMessageBusinessResponse(200, "{}")))).Error);

        await SetLifecycleAsync(database.Path, owner, "host-rejections", RemoteHostLifecycles.Online);
        using var attackerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Equal(RemoteHostSignedRequestErrors.HostAuthInvalid, (await store.AcceptSignedHostMessageAsync(
            Envelope(attackerKey, "host-rejections", HostAcceptedMessageOperations.Poll, "-", "message-key", 1, now.ToUnixTimeSeconds(), Array.Empty<byte>()),
            now,
            (_, _, _, _) => Task.FromResult(new HostMessageBusinessResponse(200, "{}")))).Error);

        Assert.Equal(RemoteHostSignedRequestErrors.HostAuthInvalid, (await store.AcceptSignedHostMessageAsync(
            Envelope(key, "host-rejections", HostAcceptedMessageOperations.Poll, "-", "message-hash", 1, now.ToUnixTimeSeconds(), Array.Empty<byte>())
                with { RequestHash = new string('a', 64) },
            now,
            (_, _, _, _) => Task.FromResult(new HostMessageBusinessResponse(200, "{}")))).Error);

        Assert.Equal(RemoteHostSignedRequestErrors.HostProtocolUnsupported, (await store.AcceptSignedHostMessageAsync(
            Envelope(key, "host-rejections", HostAcceptedMessageOperations.Poll, "-", "message-protocol", 1, now.ToUnixTimeSeconds(), Array.Empty<byte>()) with { ProtocolVersion = 0 },
            now,
            (_, _, _, _) => Task.FromResult(new HostMessageBusinessResponse(200, "{}")))).Error);

        Assert.Equal(RemoteHostSignedRequestErrors.HostProtocolUnsupported, (await store.AcceptSignedHostMessageAsync(
            Envelope(key, "host-rejections", HostAcceptedMessageOperations.Poll, "-", "message-key-version", 1, now.ToUnixTimeSeconds(), Array.Empty<byte>()) with { KeyVersion = 2 },
            now,
            (_, _, _, _) => Task.FromResult(new HostMessageBusinessResponse(200, "{}")))).Error);

        Assert.Equal(RemoteHostSignedRequestErrors.HostClockSkew, (await store.AcceptSignedHostMessageAsync(
            Envelope(key, "host-rejections", HostAcceptedMessageOperations.Poll, "-", "message-skew", 1, now.AddMinutes(-6).ToUnixTimeSeconds(), Array.Empty<byte>()),
            now,
            (_, _, _, _) => Task.FromResult(new HostMessageBusinessResponse(200, "{}")))).Error);
    }

    [Fact]
    public async Task Deterministic_rejections_commit_and_precommit_failures_roll_back_without_leaking_sensitive_values()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync();
        var owner = await AddOwnerAsync(store, "rollback-owner");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = await CreateOnlineHostAsync(database.Path, store, owner, key, "host-rollback", lastAcceptedSequence: 0);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var envelope = Envelope(key, "host-rollback", HostAcceptedMessageOperations.Poll, "-", "message-reject", 1, timestamp, Array.Empty<byte>());

        var rejection = await store.AcceptSignedHostMessageAsync(
            envelope,
            DateTimeOffset.FromUnixTimeSeconds(timestamp),
            (_, _, _, _) => Task.FromResult(new HostMessageBusinessResponse(409, RemoteHostSnapshotSerializer.SerializeProblem(409, "lease_busy"))));
        Assert.True(rejection.Succeeded);
        Assert.Equal(409, rejection.Receipt!.ResponseStatus);
        Assert.DoesNotContain(publicKey.CanonicalJson, rejection.Receipt.ResponseBodyJson, StringComparison.Ordinal);
        Assert.DoesNotContain(envelope.Signature, rejection.Receipt.ResponseBodyJson, StringComparison.Ordinal);
        Assert.Equal(1, await ReadLastAcceptedSequenceAsync(database.Path, owner, "host-rollback"));

        var replay = await store.AcceptSignedHostMessageAsync(
            envelope,
            DateTimeOffset.FromUnixTimeSeconds(timestamp + 1),
            (_, _, _, _) => Task.FromResult(new HostMessageBusinessResponse(200, "{}")));
        Assert.True(replay.Replayed);
        Assert.Equal(rejection.Receipt, replay.Receipt);

        var rollbackEnvelope = Envelope(key, "host-rollback", HostAcceptedMessageOperations.Poll, "-", "message-rollback", 2, timestamp + 2, Array.Empty<byte>());
        store.RemoteHostBeforeCommitAsync = _ => throw new InvalidOperationException("injected-before-commit");
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AcceptSignedHostMessageAsync(
            rollbackEnvelope,
            DateTimeOffset.FromUnixTimeSeconds(timestamp + 2),
            async (connection, transaction, host, token) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE remote_hosts SET connection_status='BUSY' WHERE owner_principal_id=$owner AND host_id=$host;";
                command.Parameters.AddWithValue("$owner", host.OwnerPrincipalId);
                command.Parameters.AddWithValue("$host", host.HostId);
                await command.ExecuteNonQueryAsync(token);
                return new HostMessageBusinessResponse(200, "{\"ok\":true}");
            }));
        store.RemoteHostBeforeCommitAsync = null;

        Assert.Equal(1, await ReadLastAcceptedSequenceAsync(database.Path, owner, "host-rollback"));
        Assert.Null(await ReadAcceptedMessageAsync(database.Path, owner, "host-rollback", "message-rollback"));
        Assert.Equal("ONLINE", await ReadConnectionStatusAsync(database.Path, owner, "host-rollback"));

        var oversizedEnvelope = Envelope(key, "host-rollback", HostAcceptedMessageOperations.Poll, "-", "message-oversized", 2, timestamp + 3, Array.Empty<byte>());
        var oversizedResponse = new HostMessageBusinessResponse(
            200,
            JsonSerializer.Serialize(new { value = new string('x', RemoteHostProtocol.MaximumBodyBytes) }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.AcceptSignedHostMessageAsync(
            oversizedEnvelope,
            DateTimeOffset.FromUnixTimeSeconds(timestamp + 3),
            (_, _, _, _) => Task.FromResult(oversizedResponse)));
        Assert.Equal(1, await ReadLastAcceptedSequenceAsync(database.Path, owner, "host-rollback"));
        Assert.Null(await ReadAcceptedMessageAsync(database.Path, owner, "host-rollback", "message-oversized"));
    }

    private static async Task<string> AddOwnerAsync(SqliteKernelStore store, string subject)
    {
        var principal = PrincipalRef.Create("https://issuer.example", "tenant", subject, subject, DateTimeOffset.UtcNow);
        await store.AddAsync(principal);
        return principal.PrincipalId;
    }

    private static HostSignedRequestEnvelope Envelope(
        ECDsa key,
        string hostId,
        string operation,
        string targetId,
        string messageId,
        long sequence,
        long timestamp,
        byte[] body)
    {
        var bodyHash = RemoteHostProtocol.ComputeBodyHash(body);
        var signatureBytes = key.SignData(
            RemoteHostProtocol.BuildCanonicalSigningInput(
                "POST",
                operation,
                targetId,
                hostId,
                1,
                1,
                messageId,
                sequence,
                timestamp,
                bodyHash),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var signature = Base64Url(NormalizeLowS(signatureBytes));
        return RemoteHostProtocol.ParseSignedRequest(
            "POST",
            operation,
            targetId,
            hostId,
            "1",
            "1",
            messageId,
            sequence.ToString(),
            timestamp.ToString(),
            bodyHash,
            signature);
    }

    private static async Task<P256PublicJwk> CreateOnlineHostAsync(
        string databasePath,
        SqliteKernelStore store,
        string owner,
        ECDsa key,
        string hostId,
        long lastAcceptedSequence,
        string? pairingId = null)
    {
        pairingId ??= $"pairing-{hostId}";
        var secret = RemoteHostValidation.CreateClaimSecret();
        var now = DateTimeOffset.UtcNow;
        await store.CreateHostPairingAsync(owner, pairingId, RemoteHostValidation.HashClaimSecret(secret), $"key-{hostId}", new string('a', 64), now, now.AddMinutes(5));
        var publicKey = RemoteHostValidation.NormalizeP256PublicJwk(JsonSerializer.Serialize(new
        {
            kty = "EC",
            crv = "P-256",
            x = Base64Url(key.ExportParameters(false).Q.X!),
            y = Base64Url(key.ExportParameters(false).Q.Y!),
        }));
        var claim = new HostClaim(publicKey, "KEYCHAIN_THIS_DEVICE_ONLY", "macOS", "arm64", "1.0.0", "1",
            [], []);
        var claimed = await store.ClaimHostPairingAsync(pairingId, secret, claim, $"claim-{hostId}", new string('b', 64), now.AddSeconds(1));
        var code = RemoteHostValidation.DeriveConfirmationCode(pairingId, publicKey);
        await store.ConfirmHostPairingAsync(owner, pairingId, claimed.Pairing!.Version, code, hostId, hostId, [], [], $"confirm-{hostId}", new string('c', 64), now.AddSeconds(2));
        await using var connection = new SqliteConnection($"Data Source={databasePath};Foreign Keys=True;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE remote_hosts SET lifecycle='ONLINE',connection_status='ONLINE',last_accepted_sequence=$sequence WHERE owner_principal_id=$owner AND host_id=$host;";
        command.Parameters.AddWithValue("$sequence", lastAcceptedSequence);
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$host", hostId);
        await command.ExecuteNonQueryAsync();
        return publicKey;
    }

    private static async Task SetLifecycleAsync(string databasePath, string owner, string hostId, string lifecycle)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Foreign Keys=True;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE remote_hosts SET lifecycle=$lifecycle,connection_status=$lifecycle WHERE owner_principal_id=$owner AND host_id=$host;";
        command.Parameters.AddWithValue("$lifecycle", lifecycle);
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$host", hostId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SetLastAcceptedSequenceAsync(string databasePath, string owner, string hostId, long sequence)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Foreign Keys=True;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE remote_hosts SET last_accepted_sequence=$sequence WHERE owner_principal_id=$owner AND host_id=$host;";
        command.Parameters.AddWithValue("$sequence", sequence);
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$host", hostId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ReadLastAcceptedSequenceAsync(string databasePath, string owner, string hostId)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT last_accepted_sequence FROM remote_hosts WHERE owner_principal_id=$owner AND host_id=$host;";
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$host", hostId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> ReadConnectionStatusAsync(string databasePath, string owner, string hostId)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT connection_status FROM remote_hosts WHERE owner_principal_id=$owner AND host_id=$host;";
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$host", hostId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> CountAcceptedMessagesAsync(string databasePath, string owner, string hostId)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM host_accepted_messages WHERE owner_principal_id=$owner AND host_id=$host;";
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$host", hostId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string?> ReadAcceptedMessageAsync(string databasePath, string owner, string hostId, string messageId)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT response_body_json FROM host_accepted_messages WHERE owner_principal_id=$owner AND host_id=$host AND message_id=$message;";
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$host", hostId);
        command.Parameters.AddWithValue("$message", messageId);
        return (string?)await command.ExecuteScalarAsync();
    }

    private static byte[] NormalizeLowS(byte[] signature)
    {
        var s = new BigInteger(signature.AsSpan(32, 32), isUnsigned: true, isBigEndian: true);
        if (s <= P256HalfOrder)
            return signature;
        var lowS = P256Order - s;
        signature.AsSpan(32, 32).Clear();
        lowS.ToByteArray(isUnsigned: true, isBigEndian: true)
            .CopyTo(signature.AsSpan(64 - lowS.GetByteCount(isUnsigned: true)));
        return signature;
    }

    private static string Base64Url(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}