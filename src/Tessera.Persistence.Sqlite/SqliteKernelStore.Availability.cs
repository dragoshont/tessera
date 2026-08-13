using Microsoft.Data.Sqlite;
using System.Text.Json;
using Tessera.Core.Product;

namespace Tessera.Persistence.Sqlite;

public sealed partial class SqliteKernelStore
{
    public async ValueTask<ExecutionDecision> CheckAsync(
        ExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pluginVersion = request.PluginId == "model-provider" && request.PluginVersion == "1"
            ? "1.0.0"
            : request.PluginVersion;
        var localContract = request.PluginId == "local" && pluginVersion == "1.0.0" && request.AccountId is null
            && (request.ConversationId is not null || request.JobId is not null)
            ? LocalCapabilityContract(request.CapabilityId, request.CapabilityVersion)
            : null;
        var plugin = localContract is null
            ? await GetPluginInstallationAsync(
                request.OwnerPrincipalId, request.PluginId, pluginVersion, cancellationToken).ConfigureAwait(false)
            : null;
        if (localContract is null && (plugin is null || !plugin.Enabled))
            return new(false, "plugin_disabled");

        var contract = localContract ?? CapabilityContract(plugin!.ManifestJson, request.CapabilityId, request.CapabilityVersion);
        if (contract is null) return new(false, "capability_unavailable");

        ConnectedAccount? account = null;
        if (contract.Value.AccountRequired || request.AccountId is not null)
        {
            if (request.AccountId is null) return new(false, "account_ambiguous");
            account = await GetConnectedAccountAsync(request.OwnerPrincipalId, request.AccountId, cancellationToken).ConfigureAwait(false);
            if (account?.Lifecycle != AccountLifecycle.Connected
                || account.PluginId != request.PluginId
                || account.PluginVersion != pluginVersion
                || !account.CapabilityBindings.Any(binding => binding.PluginId == request.PluginId
                    && binding.PluginVersion == pluginVersion
                    && binding.CapabilityId == request.CapabilityId
                    && binding.CapabilityVersion == request.CapabilityVersion)
                || contract.Value.RequiredPermissions.Except(account.Permissions, StringComparer.Ordinal).Any())
                return new(false, "account_unavailable");
        }

        if (request.PluginId == "model-provider")
        {
            var profiles = await ListModelProfilesAsync(request.OwnerPrincipalId, cancellationToken).ConfigureAwait(false);
            if (account is null || !profiles.Any(profile => profile.Enabled
                && profile.AccountId == account.AccountId
                && profile.Model == request.TargetScope))
                return new(false, "invalid_model");
        }

        if (request.JobId is not null)
        {
            var job = await GetJobAsync(request.OwnerPrincipalId, request.JobId, cancellationToken).ConfigureAwait(false);
            if (job is null || job.DesiredState != "ACTIVE") return new(false, "job_not_active");
            if (request.AccountId is not null && !job.AccountGrants.Contains(request.AccountId, StringComparer.Ordinal))
                return new(false, "job_account_not_granted");
            if (!job.CapabilityGrants.Contains((request.CapabilityId, request.CapabilityVersion)))
                return new(false, "job_capability_not_granted");
            if (!string.Equals(contract.Value.SideEffectClass, "ReadOnly", StringComparison.OrdinalIgnoreCase)
                && !job.SideEffectGrants.Contains(contract.Value.SideEffectClass, StringComparer.Ordinal))
                return new(false, "job_side_effect_not_granted");
        }
            else if(request.ConversationId is not null)
            {
                var grants=await GetConversationGrantsAsync(request.OwnerPrincipalId,request.ConversationId,cancellationToken).ConfigureAwait(false);
                if(request.AccountId is not null&&!grants.Accounts.Contains(request.AccountId,StringComparer.Ordinal))return new(false,"conversation_account_not_granted");
                if(!grants.Capabilities.Contains((request.CapabilityId,request.CapabilityVersion)))return new(false,"conversation_capability_not_granted");
            }
        return new(true);
    }

    private static (bool AccountRequired, string[] RequiredPermissions, string SideEffectClass)? LocalCapabilityContract(
        string capabilityId, string capabilityVersion)
    {
        if (capabilityVersion != "1") return null;
        return capabilityId switch
        {
            "local.time" or "local.memory.why" => (false, [], "ReadOnly"),
            "local.memory.remember" or "local.memory.correct" => (false, [], "LocalReversible"),
            _ => null,
        };
    }

    private static (bool AccountRequired, string[] RequiredPermissions, string SideEffectClass)? CapabilityContract(
        string manifestJson, string capabilityId, string capabilityVersion)
    {
        try
        {
            using var document = JsonDocument.Parse(manifestJson);
            if (!document.RootElement.TryGetProperty("Capabilities", out var capabilities)
                && !document.RootElement.TryGetProperty("capabilities", out capabilities)) return null;
            foreach (var capability in capabilities.EnumerateArray())
            {
                var id = Property(capability, "Id", "id");
                var version = Property(capability, "Version", "version");
                if (id != capabilityId || version != capabilityVersion) continue;
                var accountRequired = BooleanProperty(capability, "AccountRequired", "accountRequired");
                var effect = Property(capability, "SideEffectClass", "sideEffectClass") ?? string.Empty;
                var permissions = ArrayProperty(capability, "RequiredPermissions", "requiredPermissions");
                return (accountRequired, permissions, effect);
            }
        }
        catch (JsonException) { }
        return null;
    }

    private static string? Property(JsonElement element, string first, string second)
        => element.TryGetProperty(first, out var value) || element.TryGetProperty(second, out value)
            ? value.GetString() : null;

    private static bool BooleanProperty(JsonElement element, string first, string second)
        => (element.TryGetProperty(first, out var value) || element.TryGetProperty(second, out value))
            && value.ValueKind == JsonValueKind.True;

    private static string[] ArrayProperty(JsonElement element, string first, string second)
        => element.TryGetProperty(first, out var value) || element.TryGetProperty(second, out value)
            ? value.EnumerateArray().Select(item => item.GetString()).Where(item => item is not null).Cast<string>().ToArray()
            : [];

    private static async Task<bool> ExistsAsync(SqliteConnection connection, string sql, ExecutionRequest request, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        command.Parameters.AddWithValue("$owner", request.OwnerPrincipalId);
        command.Parameters.AddWithValue("$plugin", request.PluginId);
        command.Parameters.AddWithValue("$pluginVersion", request.PluginVersion);
        command.Parameters.AddWithValue("$capability", request.CapabilityId);
        command.Parameters.AddWithValue("$capabilityVersion", request.CapabilityVersion);
        command.Parameters.AddWithValue("$account", (object?)request.AccountId ?? DBNull.Value);
        command.Parameters.AddWithValue("$job", (object?)request.JobId ?? DBNull.Value);
        return await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not null;
    }
}