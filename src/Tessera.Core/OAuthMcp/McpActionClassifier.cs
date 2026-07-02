using System.Text.Json;

namespace Tessera.Core.OAuthMcp;

/// <summary>The action plane an MCP request lands on: a read (no upstream mutation) or a
/// write (a mutating tool call — default-deny + step-up).</summary>
public enum McpAccess
{
    /// <summary>No upstream mutation (protocol/discovery/query).</summary>
    Read,

    /// <summary>A mutating tool call — the manage plane (step-up).</summary>
    Write,
}

/// <summary>A parsed JSON-RPC MCP request: the method and (for <c>tools/call</c>) the tool.</summary>
/// <param name="Method">The JSON-RPC <c>method</c> (e.g. <c>tools/list</c>, <c>tools/call</c>).</param>
/// <param name="ToolName">The <c>params.name</c> for a <c>tools/call</c>; null otherwise.</param>
public sealed record McpCall(string Method, string? ToolName);

/// <summary>
/// Classifies an MCP request as read vs write for the action plane (ADR 0027 §P2b).
/// Fronting an MCP through the raw HTTP proxy is wrong because that proxy maps
/// <c>POST ⇒ manage</c> (step-up), but MCP carries reads (<c>tools/list</c>, query tool
/// calls) over POST. Here the JSON-RPC method/tool drives the plane instead of the HTTP
/// verb: every method that is not a <c>tools/call</c> is a read (MCP protocol + discovery
/// methods do not mutate the upstream resource), and a <c>tools/call</c> is a write iff the
/// tool is declared mutating. An UNDECLARED tool is a write (fail-safe / default-deny) so a
/// new or unknown tool can never silently execute as a read.
/// </summary>
public static class McpActionClassifier
{
    /// <summary>Parse a JSON-RPC request body. Null when it is not a single JSON-RPC object
    /// with a string <c>method</c> (a batch array or malformed body ⇒ null ⇒ caller refuses).</summary>
    public static McpCall? Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("method", out var m)
                || m.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var method = m.GetString()!;
            string? tool = null;
            if (method == "tools/call"
                && doc.RootElement.TryGetProperty("params", out var p)
                && p.ValueKind == JsonValueKind.Object
                && p.TryGetProperty("name", out var n)
                && n.ValueKind == JsonValueKind.String)
            {
                tool = n.GetString();
            }

            return new McpCall(method, tool);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Classify a call. Everything that is not a <c>tools/call</c> is a read. A
    /// <c>tools/call</c> is a write iff <paramref name="declaredTools"/> maps its tool to
    /// <c>true</c> — i.e. the tool sits on the <c>manage:</c> plane (ADR 0019), the same
    /// axis the PDP authorizes and orthogonal to step-up risk. A declared read tool is a
    /// read; an <b>undeclared</b> tool (or a malformed <c>tools/call</c> with no name) is a
    /// write (fail-safe / default-deny).
    /// </summary>
    public static McpAccess Classify(McpCall call, IReadOnlyDictionary<string, bool> declaredTools)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(declaredTools);

        if (call.Method != "tools/call")
        {
            return McpAccess.Read;
        }

        if (call.ToolName is null)
        {
            return McpAccess.Write;
        }

        return declaredTools.TryGetValue(call.ToolName, out var isWrite)
            ? (isWrite ? McpAccess.Write : McpAccess.Read)
            : McpAccess.Write;
    }
}
