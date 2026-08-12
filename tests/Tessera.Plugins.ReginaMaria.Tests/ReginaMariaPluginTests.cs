using System.Text.Json;
using Tessera.Core.Kernel;
using Tessera.Core.Product;
using Tessera.Core.Stores;
using Tessera.Mcp.Client;
using Tessera.Plugin.Abstractions;
using Tessera.Plugins.ReginaMaria;
using Tessera.Providers;
using Xunit;

namespace Tessera.Plugins.ReginaMaria.Tests;

public sealed class ReginaMariaPluginTests
{
    [Fact]
    public void Manifest_exposes_only_classified_scheduling_capabilities()
    {
        var manifest = new ReginaMariaPlugin().Manifest;
        Assert.Equal("regina-maria", manifest.PluginId);
        Assert.DoesNotContain(manifest.Capabilities, item => item.CapabilityId.Contains("medical", StringComparison.Ordinal));
        Assert.Equal(10, manifest.Capabilities.Count);
        Assert.All(manifest.Capabilities.Where(item => item.ExternalToolName is "rm_create_appointment" or "rm_cancel_appointment"), item =>
        {
            Assert.Equal(SideEffectClass.ExternalReversible, item.SideEffectClass);
            Assert.Equal(VerificationSupport.ProviderState, item.VerificationSupport);
            Assert.True(item.AccountRequired);
        });
    }

    [Fact]
    public void Every_manifest_capability_satisfies_the_generic_module_contract()
    {
        var plugin = new ReginaMariaPlugin();
        foreach (var capability in plugin.Manifest.Capabilities)
        {
            var exception = Record.Exception(() => PluginModuleDiscovery.Discover(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
                [new(
                    plugin.Manifest.PluginId,
                    plugin.Manifest.Version,
                    "missing.dll",
                    new string('0', 64),
                    PluginTrustState.BUILT_IN,
                    [capability])]
            ));
            Assert.True(exception is null, $"{capability.CapabilityId}: {exception}");
        }
    }

    [Fact]
    public async Task Proposal_uses_provider_preflight_as_the_canonical_approval_payload()
    {
        var runtime = new RecordingMcpRuntime((tool, _) => tool == "rm_prepare_appointment"
            ? Success(Prepared("signed-slot", "slot-1", "doctor-1"))
            : throw new InvalidOperationException(tool));
        var plugin = new ReginaMariaPlugin();
        var capability = await plugin.CreateCapabilityAsync(
            "reginamaria.appointment.propose_book",
            "1",
            Context(runtime));
        var result = await capability.InvokeAsync(Invocation(
            "reginamaria.appointment.propose_book",
            "appointment:book",
            BookingInput("Untrusted model label")));

        Assert.Equal(CapabilityOutcome.Succeeded, result.Outcome);
        Assert.Equal("Provider Doctor", result.Output.GetProperty("doctor").GetString());
        Assert.Equal("Provider Clinic", result.Output.GetProperty("location").GetString());
        Assert.Equal(123m, result.Output.GetProperty("price").GetDecimal());
        Assert.Equal("signed-slot", result.Output.GetProperty("slotReceipt").GetString());
        Assert.Equal(["rm_prepare_appointment"], runtime.Calls.Select(item => item.Tool));
    }

    [Fact]
    public async Task Approved_booking_injects_action_credential_and_requires_provider_readback()
    {
        var runtime = new RecordingMcpRuntime((tool, arguments) => tool switch
        {
            "rm_prepare_appointment" => Success(Prepared("signed-slot", "slot-1", "doctor-1")),
            "rm_create_appointment" => Success(JsonSerializer.SerializeToElement(new { booked = true, id = "booked-1" })),
            "rm_list_appointments" => Success(JsonSerializer.SerializeToElement(new
            {
                count = 1,
                appointments = new[]
                {
                    new { id = "booked-1", date = "2026-08-20", time = "17:00", doctor = "Provider Doctor", specialty = "Cardiology", location = "Provider Clinic", services = new[] { "Consultation" } },
                },
            })),
            _ => throw new InvalidOperationException(tool),
        });
        var plugin = new ReginaMariaPlugin();
        var capability = await plugin.CreateCapabilityAsync(
            "reginamaria.appointment.book",
            "1",
            Context(runtime, resolveActionCredential: true));
        var result = await capability.InvokeAsync(Invocation(
            "reginamaria.appointment.book",
            "appointment:book",
            BookingInput("Provider Doctor"),
            authorizationId: "action-1"));

        Assert.Equal(CapabilityOutcome.Succeeded, result.Outcome);
        Assert.Equal("booked-1", result.ProviderReceipt);
        Assert.Equal("provider_verified", result.VerificationMetadata);
        Assert.Equal(["rm_prepare_appointment", "rm_create_appointment", "rm_list_appointments"], runtime.Calls.Select(item => item.Tool));
        var mutation = runtime.Calls.Single(item => item.Tool == "rm_create_appointment");
        Assert.Equal("action-token", ((JsonElement)mutation.Arguments["_tessera_action_token"]!).GetString());
    }

    private static PluginCapabilityContext Context(RecordingMcpRuntime runtime, bool resolveActionCredential = false)
    {
        var now = DateTimeOffset.UtcNow;
        var account = new ConnectedAccount(
            "owner",
            "rm-owner",
            "regina-maria",
            "regina-maria",
            "1.0.0",
            "My Regina Maria",
            null,
            AccountLifecycle.Connected,
            "credential-ref",
            AccountHealth.Healthy,
            now,
            "{\"endpoint\":\"https://rm.example/mcp\"}",
            ["reginamaria.identity", "reginamaria.appointments.read", "reginamaria.availability.read", "reginamaria.appointments.write"],
            [],
            now,
            now,
            1);
        var credential = resolveActionCredential
            ? new CredentialBundle(Extra: new Dictionary<string, string> { ["action_credential_ref"] = "action-ref" })
            : new CredentialBundle();
        return new(
            account,
            credential,
            new NullTransport(),
            runtime,
            (reference, _) =>
            {
                Assert.Equal("action-ref", reference);
                return ValueTask.FromResult(new CredentialBundle(AccessToken: "action-token"));
            });
    }

    private static CapabilityInvocation Invocation(
        string capabilityId,
        string target,
        JsonElement input,
        string? authorizationId = null) => new(
            "owner",
            "test",
            capabilityId,
            "1",
            target,
            input,
            authorizationId,
            "idempotency-key");

    private static JsonElement BookingInput(string doctor) => JsonSerializer.SerializeToElement(new
    {
        slotReceipt = "signed-slot",
        intervalId = "slot-1",
        physicianId = "doctor-1",
        serviceId = "service-1",
        service = "Consultation",
        doctor,
        specialty = "Cardiology",
        location = "Provider Clinic",
        date = "2026-08-20",
        time = "17:00",
        mode = "in-clinic",
        price = 123,
        currency = "RON",
    });

    private static JsonElement Prepared(string receipt, string interval, string physician) => JsonSerializer.SerializeToElement(new
    {
        bookable = true,
        slot_receipt = receipt,
        interval_id = interval,
        physician_id = physician,
        service_id = "service-1",
        service = "Consultation",
        doctor = "Provider Doctor",
        specialty = "Cardiology",
        location = "Provider Clinic",
        date = "2026-08-20",
        time = "17:00",
        mode = "in-clinic",
        price = 123,
        currency = "RON",
    });

    private static McpInvocationResult Success(JsonElement output) => new(McpInvocationOutcome.Succeeded, output, null);

    private sealed class RecordingMcpRuntime(Func<string, IReadOnlyDictionary<string, object?>, McpInvocationResult> handler) : IMcpClientRuntime
    {
        public List<(string Tool, IReadOnlyDictionary<string, object?> Arguments)> Calls { get; } = [];

        public Task<McpServerContract> DiscoverAsync(McpServerEndpoint endpoint, McpCallPolicy policy, CancellationToken cancellationToken = default)
            => Task.FromResult(RmContract(endpoint.ServerId));

        public Task<McpInvocationResult> CallAsync(McpServerEndpoint endpoint, string toolName, IReadOnlyDictionary<string, object?> arguments, McpCallPolicy policy, CancellationToken cancellationToken = default)
        {
            Calls.Add((toolName, arguments));
            return Task.FromResult(handler(toolName, arguments));
        }
    }

    private static McpServerContract RmContract(string serverId)
        => new(serverId, "reginamaria-mcp", "0.5.37",
        [
            Tool("rm_session_status"),
            Tool("rm_account_identity"),
            Tool("rm_list_appointments"),
            Tool("rm_search_slots"),
            Tool("rm_prepare_appointment", "interval_id", "physician_id"),
            Tool("rm_create_appointment", "interval_id", "physician_id"),
            Tool("rm_cancel_appointment", "appointment_id"),
        ]);

    private static McpToolContract Tool(string name, params string[] properties)
    {
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = properties.ToDictionary(value => value, _ => (object)new { type = "string" }),
            additionalProperties = false,
        });
        return new(name, schema, schema);
    }

    private sealed class NullTransport : IHttpTransport
    {
        public Task<TransportResponse> SendAsync(string method, string url, IReadOnlyDictionary<string, string> headers, string? body, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}