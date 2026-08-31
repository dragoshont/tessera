using System.Text.RegularExpressions;
using Xunit;

namespace Tessera.Architecture.Tests;

/// <summary>
/// Source fence for the safe-method principal-registration contract (RFC 9110 §9.2.1).
///
/// A GET/HEAD/OPTIONS/TRACE request is <em>safe</em>: it must not change product state.
/// Authentication boundary helpers used to register the caller's principal row on every
/// request, so a first authenticated read persisted. Registration now happens in exactly
/// one place — <c>PrincipalRegistration.RegisterForMutationAsync</c> — which skips safe
/// methods, and every authentication boundary/resolver routes through it.
///
/// These tests keep that structural fix from silently regressing: a new (or restored)
/// direct principal <c>AddAsync</c> call at a request boundary fails the build.
/// </summary>
public sealed class PrincipalRegistrationBoundaryFenceTests
{
    private static readonly string Root = FindRoot();

    /// <summary>The single helper allowed to materialize a principal row.</summary>
    private const string RegistrationHelper = "src/Tessera.Broker/PrincipalRegistration.cs";

    /// <summary>
    /// Matches a principal registration call: <c>AddAsync(principal, ...)</c> or
    /// <c>AddAsync(PrincipalRef.Create(...), ...)</c>. Repository/interface declarations
    /// (<c>AddAsync(PrincipalRef principal, ...)</c>) are deliberately not matched.
    /// </summary>
    private static readonly Regex PrincipalAdd = new(
        @"AddAsync\s*\(\s*(?:principal\b|PrincipalRef\s*\.\s*Create)",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    [Fact]
    public void Principal_registration_exists_only_in_the_mutation_registration_helper()
    {
        var offenders = EnumerateBrokerSources()
            .Where(file => PrincipalAdd.IsMatch(File.ReadAllText(file)))
            .Select(Relative)
            .Where(path => path != RegistrationHelper)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Principal registration must go through PrincipalRegistration.RegisterForMutationAsync "
                + "so a safe (GET/HEAD/OPTIONS/TRACE) request never writes. Offending files: "
                + string.Join(", ", offenders));
    }

    [Fact]
    public void The_registration_helper_gates_on_safe_methods_and_owns_the_only_add()
    {
        var source = File.ReadAllText(Path.Combine(Root, RegistrationHelper.Replace('/', Path.DirectorySeparatorChar)));
        Assert.Equal(1, PrincipalAdd.Count(source));
        foreach (var safeMethod in new[] { "IsGet", "IsHead", "IsOptions", "IsTrace" })
            Assert.Contains(safeMethod, source, StringComparison.Ordinal);
        Assert.Contains("IsSafeRequest(context)", source, StringComparison.Ordinal);
    }

    /// <summary>Every known authentication boundary/resolver that resolves a caller.</summary>
    [Theory]
    [InlineData("src/Tessera.Broker/R2ProductEndpoints.cs")]
    [InlineData("src/Tessera.Broker/RemoteHostEndpoints.cs")]
    [InlineData("src/Tessera.Broker/ContinuityEndpoints.cs")]
    [InlineData("src/Tessera.Broker/IntegrationCatalogEndpoints.cs")]
    [InlineData("src/Tessera.Broker/SetupEndpoints.cs")]
    [InlineData("src/Tessera.Broker/ModelGatewayEndpoints.cs")]
    [InlineData("src/Tessera.Broker/RealtimeVoiceEndpoints.cs")]
    [InlineData("src/Tessera.Broker/PluginHostRuntime.cs")]
    public void Every_authentication_boundary_registers_through_the_helper(string relativePath)
    {
        var source = File.ReadAllText(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        Assert.Contains("PrincipalRegistration.RegisterForMutationAsync", source, StringComparison.Ordinal);
        Assert.False(PrincipalAdd.IsMatch(source), $"{relativePath} registers a principal directly.");
    }

    private static IEnumerable<string> EnumerateBrokerSources()
        => Directory.EnumerateFiles(
                Path.Combine(Root, "src", "Tessera.Broker"),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string Relative(string path)
        => Path.GetRelativePath(Root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Tessera.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
