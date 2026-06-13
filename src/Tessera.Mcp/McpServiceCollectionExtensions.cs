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
            // STATELESS streamable-HTTP: every POST is a self-contained request whose
            // response is returned inline — no per-session SSE GET stream, no
            // Mcp-Session-Id to lose. This matches Tessera's model exactly (identity
            // is carried by the forwarded bearer on each call; the broker holds no
            // per-session state) and it removes the session-churn we saw with the
            // chat client: repeated `initialize` + "Failed to open SSE stream: Bad
            // Request" as it reconnected the stateful SSE channel. Each tool call now
            // runs in its own request context, so IHttpContextAccessor always sees
            // that call's Authorization header.
            .WithHttpTransport(httpOptions => httpOptions.Stateless = true)
            .WithTools<TesseraMcpTools>();
    }
}
