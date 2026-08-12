namespace Tessera.Core.Kernel;

public sealed record ModelRequest(
    string Purpose,
    ContextEnvelope Context,
    string StructuredOutputSchema,
    int SizeBudget,
    IReadOnlyList<string> PolicyConstraints);

public sealed record ModelResult(
    string StructuredOutput,
    decimal Confidence,
    IReadOnlyList<string> ContextReferencesUsed,
    string AdapterId,
    string AdapterVersion,
    IReadOnlyDictionary<string, string> Diagnostics);

public interface IModelAdapter
{
    string AdapterId { get; }

    string Version { get; }

    ValueTask<ModelResult> GenerateAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default);
}
