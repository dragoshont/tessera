using Tessera.Core.Configuration;
using Tessera.Core.Recipes;
using Xunit;

namespace Tessera.Core.Tests;

public sealed class ConfigTests
{
    [Fact]
    public void Default_config_is_valid_and_fail_closed()
    {
        var config = new TesseraConfig();
        Assert.Empty(config.Validate());
        Assert.Equal("deny", config.Policy.Default);
        Assert.False(config.Egress.Enabled);
    }

    [Fact]
    public void Policy_default_allow_is_rejected_as_fail_open()
    {
        var config = new TesseraConfig { Policy = new PolicyOptions { Default = "allow" } };
        Assert.Contains(config.Validate(), p => p.Contains("fail-open", StringComparison.Ordinal));
    }

    [Fact]
    public void Dev_mode_off_loopback_is_rejected()
    {
        var config = new TesseraConfig
        {
            Server = new ServerOptions { Host = "0.0.0.0", Port = 8080 },
            Identity = new IdentityOptions { Mode = "dev" },
        };
        Assert.Contains(config.Validate(), p => p.Contains("loopback", StringComparison.Ordinal));
    }

    [Fact]
    public void Oidc_mode_requires_an_issuer()
    {
        var config = new TesseraConfig { Identity = new IdentityOptions { Mode = "oidc" } };
        Assert.Contains(config.Validate(), p => p.Contains("issuer", StringComparison.Ordinal));
    }

    [Fact]
    public void Egress_enabled_requires_an_allow_list()
    {
        var config = new TesseraConfig { Egress = new EgressOptions { Enabled = true } };
        Assert.Contains(config.Validate(), p => p.Contains("allow-list", StringComparison.Ordinal));
    }

    [Fact]
    public void Refresh_enabled_requires_egress_enabled()
    {
        // The rotation owner can't reach an upstream with egress off — reject the
        // fail-open-looking combo rather than silently doing nothing.
        var config = new TesseraConfig { Refresh = new RefreshOptions { Enabled = true } };
        Assert.Contains(config.Validate(), p => p.Contains("egress.enabled is false", StringComparison.Ordinal));
    }

    [Fact]
    public void Refresh_enabled_with_egress_and_a_positive_interval_is_valid()
    {
        var config = new TesseraConfig
        {
            Egress = new EgressOptions { Enabled = true, AllowedHosts = ["api.example.com"] },
            Refresh = new RefreshOptions { Enabled = true, IntervalSeconds = 900, AcknowledgeSingleWriter = true },
        };
        Assert.Empty(config.Validate());
    }

    [Fact]
    public void Refresh_enabled_requires_acknowledging_the_single_writer_invariant()
    {
        // No leader election: enabling rotation must consciously assert single-replica.
        var config = new TesseraConfig
        {
            Egress = new EgressOptions { Enabled = true, AllowedHosts = ["api.example.com"] },
            Refresh = new RefreshOptions { Enabled = true, IntervalSeconds = 900 }, // ack missing
        };
        Assert.Contains(config.Validate(), p => p.Contains("acknowledgeSingleWriter", StringComparison.Ordinal));
    }

    [Fact]
    public void Refresh_is_off_by_default()
    {
        Assert.False(new RefreshOptions().Enabled);
        Assert.Empty(new TesseraConfig().Validate());
    }

    [Fact]
    public void Out_of_range_port_is_rejected()
    {
        var config = new TesseraConfig { Server = new ServerOptions { Port = 70000 } };
        Assert.Contains(config.Validate(), p => p.Contains("out of range", StringComparison.Ordinal));
    }

    [Fact]
    public void Delegation_is_disabled_until_an_audience_is_set()
    {
        Assert.False(new OidcOptions().DelegationEnabled);
        Assert.True(new OidcOptions { Audience = "api://system" }.DelegationEnabled);
    }

    [Fact]
    public void LoadConfig_reads_json_with_comments_and_applies_env_overrides()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                {
                  // server settings
                  "server": { "host": "0.0.0.0", "port": 8080 },
                  "identity": { "mode": "oidc", "oidc": { "issuer": "https://issuer/v2.0" } },
                  "policy": { "default": "deny" },
                }
                """);

            var env = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["TESSERA_SERVER_PORT"] = "9090",
                ["TESSERA_OIDC_AUDIENCE"] = "api://system",
            };

            var config = ConfigLoader.LoadConfig(path, env);

            Assert.Equal("0.0.0.0", config.Server.Host);
            Assert.Equal(9090, config.Server.Port); // env override applied
            Assert.Equal("oidc", config.Identity.Mode);
            Assert.Equal("api://system", config.Identity.Oidc.Audience);
            Assert.True(config.Identity.Oidc.DelegationEnabled);
            Assert.Empty(config.Validate());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadPolicy_missing_file_is_deny_all()
    {
        var policy = ConfigLoader.LoadPolicy("/no/such/file.json");
        Assert.Same(LoadedPolicy.Empty, policy);
    }

    [Fact]
    public void Portal_admins_load_from_json_and_survive_env_overrides()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                {
                  "server": { "host": "127.0.0.1", "port": 8080 },
                  "portal": { "admins": ["alice@example.com"] }
                }
                """);

            // An UNRELATED env override forces ApplyEnvironmentOverrides to rebuild the
            // config — the portal section must NOT be dropped (regression guard).
            var env = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["TESSERA_SERVER_PORT"] = "9090",
            };

            var config = ConfigLoader.LoadConfig(path, env);

            Assert.Equal(9090, config.Server.Port);
            Assert.Contains("alice@example.com", config.Portal.Admins);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Portal_admins_can_be_set_by_env()
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["TESSERA_PORTAL_ADMINS"] = "alice@example.com, bob@example.com",
        };

        var config = ConfigLoader.LoadConfig(null, env);

        Assert.Equal(2, config.Portal.Admins.Count);
        Assert.Contains("bob@example.com", config.Portal.Admins);
    }

    [Fact]
    public void LoadPolicy_reads_grants_bindings_and_recipes()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                {
                  "grants": [
                    { "caller": "spiffe://tessera.local/chatbot", "onBehalfOf": "alice@example.com",
                      "target": "health-portal", "actions": ["read:*"] }
                  ],
                  "bindings": [
                    { "target": "health-portal", "onBehalfOf": "alice@example.com", "credential": "health-portal-session" }
                  ],
                  "recipes": [
                    { "target": "health-portal", "driver": "browser", "egress": "none",
                      "actions": ["read:results"], "description": "patient portal" }
                  ]
                }
                """);

            var policy = ConfigLoader.LoadPolicy(path);

            Assert.Single(policy.Grants);
            Assert.Equal("alice@example.com", policy.Grants[0].OnBehalfOf);
            Assert.Single(policy.Bindings);
            Assert.Equal("health-portal-session", policy.Bindings[0].Credential);
            Assert.Single(policy.Recipes);
            Assert.Equal(EgressMode.None, policy.Recipes[0].Egress);
            Assert.Equal("read:results", Assert.Single(policy.Recipes[0].ExposedActions));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadPolicy_parses_http_egress_recipe()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                {
                  "recipes": [
                    { "target": "calendar", "egress": "http", "upstreamBaseUrl": "https://api.example.com",
                      "injection": "bearer", "actions": ["read:events", "write:events.create"] }
                  ]
                }
                """);

            var recipe = Assert.Single(ConfigLoader.LoadPolicy(path).Recipes);
            Assert.Equal(EgressMode.Http, recipe.Egress);
            Assert.Equal(InjectionKind.BearerToken, recipe.Injection);
            Assert.Equal("https://api.example.com", recipe.UpstreamBaseUrl);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
