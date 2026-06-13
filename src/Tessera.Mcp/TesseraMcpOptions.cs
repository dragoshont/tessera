namespace Tessera.Mcp;

/// <summary>Options for the chat-facing MCP server.</summary>
public sealed class TesseraMcpOptions
{
    /// <summary>
    /// The caller id used for the shared chat workload (the "WHO" for the
    /// chat→Tessera hop). Trusted by NetworkPolicy + the verified end-user token
    /// (review C2). Grants for delegated chat access name this caller.
    /// </summary>
    public string ChatCallerId { get; init; } = "chat://librechat";

    /// <summary>
    /// Optional header the chat forwards the end-user token in, if not the standard
    /// <c>Authorization: Bearer</c>. Checked before <c>Authorization</c> when set.
    /// </summary>
    public string? AlternateTokenHeader { get; init; }

    /// <summary>The MCP server name advertised to clients.</summary>
    public string ServerName { get; init; } = "tessera";

    /// <summary>The MCP server version advertised to clients.</summary>
    public string ServerVersion { get; init; } = "0.1.0";
}
