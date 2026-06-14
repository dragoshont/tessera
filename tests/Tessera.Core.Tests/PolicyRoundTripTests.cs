using Tessera.Core.Configuration;
using Tessera.Core.Policy;
using Tessera.Core.Resolution;
using Xunit;

namespace Tessera.Core.Tests;

/// <summary>
/// The policy document must round-trip the new plane + ownership fields (ADR 0019 /
/// 0020) faithfully: a value loaded from JSON survives a save + reload, and a
/// derived/default value stays omitted (a clean, reviewable diff — ADR 0008).
/// </summary>
public sealed class PolicyRoundTripTests
{
    private static string WriteTemp(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"tessera-policy-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Recipe_tool_plane_and_binding_owner_survive_a_save_reload()
    {
        var path = WriteTemp("""
            {
              "bindings": [
                { "target": "health-portal", "onBehalfOf": "alice@example.com", "credential": "hp-alice", "owner": "user" },
                { "target": "health-portal", "onBehalfOf": "kid@example.com", "credential": "hp-kid", "owner": "dependent", "guardian": "alice@example.com" },
                { "target": "media", "credential": "media-key" }
              ],
              "recipes": [
                {
                  "target": "media", "egress": "http", "upstreamBaseUrl": "https://media.example.com",
                  "tools": [
                    { "name": "search", "method": "GET", "path": "/search", "action": "read:search" },
                    { "name": "settings", "method": "POST", "path": "/settings", "action": "pay:settings", "plane": "manage" }
                  ]
                }
              ]
            }
            """);
        try
        {
            var loaded = ConfigLoader.LoadPolicy(path);

            // Owners parsed from the wire.
            var userBinding = loaded.Bindings.Single(b => b.Credential == "hp-alice");
            Assert.Equal(CredentialOwner.User, userBinding.Owner);
            var kidBinding = loaded.Bindings.Single(b => b.Credential == "hp-kid");
            Assert.Equal(CredentialOwner.Dependent, kidBinding.Owner);
            Assert.Equal("alice@example.com", kidBinding.Guardian);
            var serviceBinding = loaded.Bindings.Single(b => b.Credential == "media-key");
            Assert.Equal(CredentialOwner.Service, serviceBinding.Owner); // default, no token in JSON

            // The legacy-verb tool carries an explicit plane override; the namespaced
            // one derives its plane.
            var media = loaded.Recipes.Single(r => r.Target == "media");
            var settings = media.ExposedTools.Single(t => t.Name == "settings");
            Assert.Equal(ActionPlane.Manage, settings.EffectivePlane);
            var search = media.ExposedTools.Single(t => t.Name == "search");
            Assert.Equal(ActionPlane.Read, search.EffectivePlane);

            // Save + reload: the values survive.
            ConfigLoader.SavePolicy(path, loaded);
            var reloaded = ConfigLoader.LoadPolicy(path);
            Assert.Equal(CredentialOwner.User, reloaded.Bindings.Single(b => b.Credential == "hp-alice").Owner);
            Assert.Equal(CredentialOwner.Dependent, reloaded.Bindings.Single(b => b.Credential == "hp-kid").Owner);
            Assert.Equal("alice@example.com", reloaded.Bindings.Single(b => b.Credential == "hp-kid").Guardian);
            Assert.Equal(ActionPlane.Manage, reloaded.Recipes.Single(r => r.Target == "media").ExposedTools.Single(t => t.Name == "settings").EffectivePlane);

            // A clean diff: the default owner + the derived plane are omitted on write.
            var written = File.ReadAllText(path);
            Assert.DoesNotContain("\"owner\": \"service\"", written, StringComparison.Ordinal);
            Assert.DoesNotContain("\"plane\": \"read\"", written, StringComparison.Ordinal);
            // … but the non-default ones are persisted.
            Assert.Contains("\"owner\": \"user\"", written, StringComparison.Ordinal);
            Assert.Contains("\"plane\": \"manage\"", written, StringComparison.Ordinal);
            Assert.Contains("\"guardian\": \"alice@example.com\"", written, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Recipe_rotation_and_refresh_spec_survive_a_save_reload()
    {
        var path = WriteTemp("""
            {
              "recipes": [
                {
                  "target": "health-portal", "egress": "http", "upstreamBaseUrl": "https://api.health-portal.example/v1",
                  "injection": "cookies",
                  "rotation": { "owner": "tessera", "detail": "Tessera keeps this session warm" },
                  "refreshSpec": { "path": "auth/refresh", "method": "POST", "accessTokenField": "at", "refreshTokenField": "rt", "absorbSetCookie": true }
                }
              ]
            }
            """);
        try
        {
            var loaded = ConfigLoader.LoadPolicy(path);
            var recipe = loaded.Recipes.Single();
            Assert.Equal("tessera", recipe.Rotation!.Owner);
            Assert.NotNull(recipe.Refresh);
            Assert.Equal("auth/refresh", recipe.Refresh!.Path);
            Assert.Equal("at", recipe.Refresh.AccessTokenField);
            Assert.Equal("rt", recipe.Refresh.RefreshTokenField);

            ConfigLoader.SavePolicy(path, loaded);
            var reloaded = ConfigLoader.LoadPolicy(path).Recipes.Single();
            Assert.Equal("tessera", reloaded.Rotation!.Owner);
            Assert.Equal("auth/refresh", reloaded.Refresh!.Path);
            Assert.Equal("at", reloaded.Refresh.AccessTokenField);

            var written = File.ReadAllText(path);
            Assert.Contains("\"refreshSpec\"", written, StringComparison.Ordinal);
            Assert.Contains("\"path\": \"auth/refresh\"", written, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
