using Microsoft.Extensions.DependencyInjection;

namespace Tessera.Mcp;

/// <summary>DI wiring for the Tessera MCP server.</summary>
public static class McpServiceCollectionExtensions
{
    /// <summary>
    /// Registers the chat-facing MCP server (streamable HTTP) and its tools. The
    /// broker pipeline dependencies (<c>ITokenValidator</c>, <c>BrokerCore</c>,
    /// <c>PolicyDecisionPoint</c>, recipes) must already be registered by the host.
    /// Map the endpoint with <c>app.MapMcp("/mcp")</c>.
    /// </summary>
    public static IMcpServerBuilder AddTesseraMcp(this IServiceCollection services, TesseraMcpOptions options)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton(options);
        services.AddSingleton<TesseraMcpService>();

        return services
            .AddMcpServer(server =>
            {
                server.ServerInfo = new ModelContextProtocol.Protocol.Implementation
                {
                    Name = options.ServerName,
                    Version = options.ServerVersion,
                };
            })
            .WithHttpTransport()
            .WithTools<TesseraMcpTools>();
    }
}
