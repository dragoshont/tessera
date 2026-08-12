using System.Text.Json;
using System.Text;

namespace Tessera.Core.Kernel;

public enum SideEffectClass
{
    ReadOnly,
    LocalReversible,
    ExternalReversible,
    ExternalCommunication,
    HighImpact,
}

public enum IdempotencySupport
{
    None,
    Keyed,
    ProviderNative,
}

public enum VerificationSupport
{
    None,
    ProviderState,
    ExternalOutcome,
}

public enum CapabilityOutcome
{
    Succeeded,
    Failed,
    UnknownOutcome,
}

public sealed record CapabilityDescriptor(
    string CapabilityId,
    string Version,
    string Description,
    string InputSchema,
    string OutputSchema,
    SideEffectClass SideEffectClass,
    IReadOnlyList<string> RequiredPermissions,
    IReadOnlyList<SensitivityClass> AllowedDataClasses,
    IdempotencySupport IdempotencySupport,
    VerificationSupport VerificationSupport)
{
    public static CapabilityDescriptor Create(
        string capabilityId,
        string version,
        string description,
        string inputSchema,
        string outputSchema,
        SideEffectClass sideEffectClass,
        IEnumerable<string> requiredPermissions,
        IEnumerable<SensitivityClass> allowedDataClasses,
        IdempotencySupport idempotencySupport,
        VerificationSupport verificationSupport)
        => new(
            KernelValidation.Text(capabilityId, nameof(capabilityId), 256),
            KernelValidation.Text(version, nameof(version), 64),
            KernelValidation.Text(description, nameof(description), 1024),
            KernelValidation.Text(inputSchema, nameof(inputSchema), 16384),
            KernelValidation.Text(outputSchema, nameof(outputSchema), 16384),
            sideEffectClass,
            KernelValidation.References(requiredPermissions, nameof(requiredPermissions)),
            Array.AsReadOnly(allowedDataClasses.Distinct().Order().ToArray()),
            idempotencySupport,
            verificationSupport);
}

public sealed record CapabilityInvocation(
    string OwnerPrincipalId,
    string TaskOrWorkflowId,
    string CapabilityId,
    string CapabilityVersion,
    string TargetScope,
    JsonElement Input,
    string? AuthorizationId,
    string? IdempotencyKey);

public sealed record CapabilityRuntimeIdentity(
    string ServerId,
    string ServerName,
    string ServerVersion,
    string ExternalToolName);

public sealed record CapabilityResult(
    CapabilityOutcome Outcome,
    JsonElement Output,
    string? ProviderReceipt,
    string? VerificationMetadata,
    string? FailureCode,
    CapabilityRuntimeIdentity? RuntimeIdentity = null);

public interface ICapability
{
    CapabilityDescriptor Descriptor { get; }

    ValueTask<CapabilityResult> InvokeAsync(
        CapabilityInvocation invocation,
        CancellationToken cancellationToken = default);
}

public sealed class CapabilityRegistry
{
    private readonly Dictionary<(string Id, string Version), ICapability> _capabilities = [];

    public void Register(ICapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        var key = (capability.Descriptor.CapabilityId, capability.Descriptor.Version);
        if (!_capabilities.TryAdd(key, capability))
        {
            throw new InvalidOperationException($"Capability {key.CapabilityId}/{key.Version} is already registered.");
        }
    }

    public ICapability Resolve(string capabilityId, string version)
    {
        if (!_capabilities.TryGetValue((capabilityId, version), out var capability))
        {
            throw new KeyNotFoundException($"Capability {capabilityId}/{version} is not registered.");
        }

        return capability;
    }

    public IReadOnlyList<CapabilityDescriptor> ListDescriptors()
        => _capabilities.Values
            .Select(capability => capability.Descriptor)
            .OrderBy(descriptor => descriptor.CapabilityId, StringComparer.Ordinal)
            .ThenBy(descriptor => descriptor.Version, StringComparer.Ordinal)
            .ToArray();
}

public sealed class DeterministicCapability(
    CapabilityDescriptor descriptor,
    Func<CapabilityInvocation, CapabilityResult> handler) : ICapability
{
    private readonly Func<CapabilityInvocation, CapabilityResult> _handler = handler
        ?? throw new ArgumentNullException(nameof(handler));

    public CapabilityDescriptor Descriptor { get; } = descriptor
        ?? throw new ArgumentNullException(nameof(descriptor));

    public ValueTask<CapabilityResult> InvokeAsync(
        CapabilityInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(invocation.CapabilityId, Descriptor.CapabilityId, StringComparison.Ordinal)
            || !string.Equals(invocation.CapabilityVersion, Descriptor.Version, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Invocation does not match the capability descriptor.");
        }

        if (Descriptor.IdempotencySupport != IdempotencySupport.None
            && string.IsNullOrWhiteSpace(invocation.IdempotencyKey))
        {
            throw new InvalidOperationException("This capability requires an idempotency key.");
        }

        if (Descriptor.SideEffectClass != SideEffectClass.ReadOnly
            && string.IsNullOrWhiteSpace(invocation.AuthorizationId))
        {
            throw new UnauthorizedAccessException("A side-effecting capability requires authorization.");
        }

        return ValueTask.FromResult(_handler(invocation));
    }
}

public static class CapabilityPayloadHash
{
    public static string Compute(JsonElement input)
        => ActionPayloadHash.Compute(Encoding.UTF8.GetBytes(input.GetRawText()));
}

/// <summary>
/// The only Kernel path for invoking a side-effecting capability. It binds the
/// actual structured input to the already-authorized durable action before any
/// capability code runs.
/// </summary>
public sealed class ActionExecutionService(IActionExecutionRepository repository)
{
    private readonly IActionExecutionRepository _repository = repository
        ?? throw new ArgumentNullException(nameof(repository));

    public async ValueTask<CapabilityResult> InvokeAsync(
        string actionId,
        long expectedVersion,
        ICapability capability,
        CapabilityInvocation invocation,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(invocation);
        var started = await _repository.TryStartAuthorizedAsync(
            invocation.OwnerPrincipalId,
            actionId,
            expectedVersion,
            invocation.AuthorizationId,
            invocation.CapabilityId,
            invocation.CapabilityVersion,
            CapabilityPayloadHash.Compute(invocation.Input),
            invocation.TargetScope,
            invocation.IdempotencyKey,
            startedAt,
            cancellationToken).ConfigureAwait(false);
        if (started is null)
        {
            throw new UnauthorizedAccessException("Capability invocation does not match the current durable authorized action.");
        }

        return await capability.InvokeAsync(invocation, cancellationToken).ConfigureAwait(false);
    }
}