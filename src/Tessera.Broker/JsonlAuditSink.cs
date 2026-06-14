using System.Text.Json;
using Tessera.Core.Audit;
using Tessera.Core.Model;
using Tessera.Core.Resolution;

namespace Tessera.Broker;

/// <summary>
/// Writes one JSON line per decision to a stream (stdout when the path is <c>-</c>,
/// the right choice in a container). Secret-free by construction — it only ever
/// receives identifiers, an <see cref="Effect"/>, and a status enum.
/// </summary>
public sealed class JsonlAuditSink : IAuditSink, IDisposable
{
    private readonly TextWriter _writer;
    private readonly bool _ownsWriter;
    private readonly Lock _gate = new();

    private JsonlAuditSink(TextWriter writer, bool ownsWriter)
    {
        _writer = writer;
        _ownsWriter = ownsWriter;
    }

    /// <summary>Opens a sink at <paramref name="path"/> (<c>-</c>/empty ⇒ stdout).</summary>
    public static JsonlAuditSink Open(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "-")
        {
            return new JsonlAuditSink(Console.Out, ownsWriter: false);
        }

        var stream = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };
        return new JsonlAuditSink(stream, ownsWriter: true);
    }

    /// <inheritdoc/>
    public void Record(AccessRequest request, Decision decision, ResolvedCredential? credential)
    {
        var entry = AuditEntry.From(request, decision, credential);

        var line = JsonSerializer.Serialize(entry, AuditJson.Default);
        lock (_gate)
        {
            _writer.WriteLine(line);
            _writer.Flush();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_ownsWriter)
        {
            _writer.Dispose();
        }
    }
}

internal static class AuditJson
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };
}
