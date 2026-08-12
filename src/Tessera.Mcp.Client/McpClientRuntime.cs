using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SdkHttpClientTransport = ModelContextProtocol.Client.HttpClientTransport;

namespace Tessera.Mcp.Client;

public sealed class McpClientRuntime(
    Func<McpServerEndpoint, HttpClient> httpClientFactory,
    ILoggerFactory? loggerFactory = null) : IMcpClientRuntime
{
    private readonly ILoggerFactory _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;

    public McpClientRuntime(Func<HttpClient> httpClientFactory, ILoggerFactory? loggerFactory = null)
        : this(_ => httpClientFactory(), loggerFactory) { }

    public async Task<McpServerContract> DiscoverAsync(
        McpServerEndpoint endpoint,
        McpCallPolicy policy,
        CancellationToken cancellationToken = default)
    {
        Validate(endpoint, policy);
        using var timeout = Timeout(policy, cancellationToken);
        await using var client = await CreateAsync(endpoint, timeout.Token).ConfigureAwait(false);
        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token).ConfigureAwait(false);
        if (tools.Count > 256) throw new McpRuntimeException("mcp_toolset_incompatible");
        var contracts = tools.Select(tool =>
        {
            var input = BoundedClone(tool.ProtocolTool.InputSchema, policy.MaximumResultBytes);
            JsonElement? output = tool.ProtocolTool.OutputSchema is { } schema
                ? BoundedClone(schema, policy.MaximumResultBytes)
                : null;
            return new McpToolContract(tool.Name, input, output);
        }).ToArray();
        return new(endpoint.ServerId, client.ServerInfo?.Name, client.ServerInfo?.Version, contracts);
    }

    public async Task<McpInvocationResult> CallAsync(
        McpServerEndpoint endpoint,
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        McpCallPolicy policy,
        CancellationToken cancellationToken = default)
    {
        Validate(endpoint, policy);
        if (string.IsNullOrWhiteSpace(toolName) || toolName.Length > 256)
            throw new ArgumentException("A bounded tool name is required.", nameof(toolName));
        using var timeout = Timeout(policy, cancellationToken);
        var dispatched = false;
        try
        {
            await using var client = await CreateAsync(endpoint, timeout.Token).ConfigureAwait(false);
            dispatched = true;
            var result = await client.CallToolAsync(toolName, arguments, cancellationToken: timeout.Token).ConfigureAwait(false);
            if (result.IsError == true) return Failure(policy, dispatched, "provider_tool_error");
            var structured = StructuredResult(result, policy.MaximumResultBytes);
            return new(McpInvocationOutcome.Succeeded, structured, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(policy, dispatched, "provider_timeout");
        }
        catch (McpRuntimeException exception)
        {
            return Failure(policy, dispatched, exception.ErrorCode);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return Failure(policy, dispatched, "provider_unavailable");
        }
    }

    private async Task<McpClient> CreateAsync(McpServerEndpoint endpoint, CancellationToken cancellationToken)
    {
        var httpClient = httpClientFactory(endpoint);
        var transport = new SdkHttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = endpoint.Endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = endpoint.Headers is null
                ? null
                : new Dictionary<string, string>(endpoint.Headers, StringComparer.Ordinal),
            OwnsSession = true,
            MaxReconnectionAttempts = 0,
        }, httpClient, _loggerFactory, ownsHttpClient: true);
        try
        {
            return await McpClient.CreateAsync(transport, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static CancellationTokenSource Timeout(McpCallPolicy policy, CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(policy.Timeout);
        return source;
    }

    private static void Validate(McpServerEndpoint endpoint, McpCallPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (string.IsNullOrWhiteSpace(endpoint.ServerId)) throw new ArgumentException("Server ID is required.", nameof(endpoint));
        if (!endpoint.Endpoint.IsAbsoluteUri || endpoint.Endpoint.Scheme is not ("https" or "http"))
            throw new ArgumentException("An absolute HTTP MCP endpoint is required.", nameof(endpoint));
        if (endpoint.Endpoint.Scheme == "http" && !endpoint.AllowPrivateNetwork)
            throw new ArgumentException("Public MCP endpoints require HTTPS.", nameof(endpoint));
        if (!string.IsNullOrEmpty(endpoint.Endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Endpoint.Query) || !string.IsNullOrEmpty(endpoint.Endpoint.Fragment))
            throw new ArgumentException("MCP endpoints cannot contain user info, query, or fragment.", nameof(endpoint));
        if (policy.Timeout <= TimeSpan.Zero || policy.Timeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(policy));
        if (policy.MaximumResultBytes is < 1024 or > 4 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(policy));
    }

    private static JsonElement BoundedClone(JsonElement value, int maximumBytes)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > maximumBytes) throw new McpRuntimeException("provider_result_too_large");
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.Clone();
    }

    private static JsonElement StructuredResult(CallToolResult result, int maximumBytes)
    {
        if (result.StructuredContent is { } structured) return BoundedClone(structured, maximumBytes);
        if (result.Content is not [TextContentBlock text]) throw new McpRuntimeException("provider_malformed");
        if (Encoding.UTF8.GetByteCount(text.Text) > maximumBytes) throw new McpRuntimeException("provider_result_too_large");
        try
        {
            using var document = JsonDocument.Parse(text.Text);
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new McpRuntimeException("provider_malformed", exception);
        }
    }

    private static McpInvocationResult Failure(McpCallPolicy policy, bool dispatched, string errorCode)
        => policy.MutationDispatched && dispatched
            ? new(McpInvocationOutcome.UnknownOutcome, null, "unknown_outcome")
            : new(McpInvocationOutcome.Failed, null, errorCode);
}

public sealed class McpRuntimeException(string errorCode, Exception? innerException = null) : Exception(errorCode, innerException)
{
    public string ErrorCode { get; } = errorCode;
}