using Tessera.Identity;

namespace Tessera.Broker.Tests;

/// <summary>An offline token validator keyed on opaque test token strings (caller plane tests).</summary>
internal sealed class FakeTokenValidator : ITokenValidator
{
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _tokens = new(StringComparer.Ordinal);

    public bool DelegationEnabled { get; init; } = true;

    public FakeTokenValidator AddUser(string token, string oid, string preferredUsername)
    {
        _tokens[token] = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["oid"] = oid,
            ["preferred_username"] = preferredUsername,
            ["iss"] = "https://login.microsoftonline.com/test/v2.0",
            ["tid"] = "test",
        };
        return this;
    }

    public FakeTokenValidator AddApp(string token, string appId)
    {
        _tokens[token] = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["azp"] = appId,
            ["appid"] = appId,
            ["idtyp"] = "app",
            ["iss"] = "https://login.microsoftonline.com/test/v2.0",
            ["tid"] = "test",
        };
        return this;
    }

    public Task<TesseraTokenResult> ValidateAsync(string token, CancellationToken cancellationToken = default)
    {
        if (!DelegationEnabled)
        {
            return Task.FromResult(TesseraTokenResult.Fail("delegation fail-closed: no OIDC audience configured (gate G2/C3)"));
        }

        return Task.FromResult(_tokens.TryGetValue(token, out var claims)
            ? TesseraTokenResult.Success(claims)
            : TesseraTokenResult.Fail("token rejected"));
    }
}
