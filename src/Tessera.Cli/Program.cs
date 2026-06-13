using Tessera.Broker;
using Tessera.Core.Configuration;

namespace Tessera.Cli;

/// <summary>The <c>tessera</c> command-line entry point.</summary>
internal static class Program
{
    private const string Version = "0.1.0";

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            return Usage();
        }

        return args[0] switch
        {
            "version" or "--version" or "-v" => PrintVersion(),
            "validate" => Validate(args),
            "serve" => await ServeAsync(args).ConfigureAwait(false),
            "--help" or "-h" or "help" => Usage(),
            _ => Unknown(args[0]),
        };
    }

    private static int PrintVersion()
    {
        Console.WriteLine($"tessera {Version}");
        return 0;
    }

    private static int Validate(string[] args)
    {
        var configPath = ArgValue(args, "--config");
        var grantsPath = ArgValue(args, "--grants");

        var config = ConfigLoader.LoadConfig(configPath);
        var problems = config.Validate();

        var policyPath = grantsPath ?? config.Policy.Document;
        var policy = ConfigLoader.LoadPolicy(policyPath);

        Console.WriteLine($"config:  {configPath ?? "(defaults)"}");
        Console.WriteLine($"  identity mode : {config.Identity.Mode}");
        Console.WriteLine($"  listen        : {config.Server.Host}:{config.Server.Port}");
        Console.WriteLine($"  policy default: {config.Policy.Default}");
        Console.WriteLine($"  oidc audience : {(config.Identity.Oidc.DelegationEnabled ? "set (delegation enabled)" : "unset (delegation FAILS CLOSED)")}");
        Console.WriteLine($"policy:  {policyPath}  ({policy.Grants.Count} grant(s), {policy.Bindings.Count} binding(s), {policy.Recipes.Count} recipe(s))");

        if (problems.Count > 0)
        {
            Console.WriteLine("\nNOT OK — fix these:");
            foreach (var problem in problems)
            {
                Console.WriteLine($"  x {problem}");
            }

            return 1;
        }

        Console.WriteLine("\nOK — configuration is valid and fail-closed.");
        if (policy.Grants.Count == 0)
        {
            Console.WriteLine("note: no grants loaded yet, so every request will be denied.");
        }

        return 0;
    }

    private static async Task<int> ServeAsync(string[] args)
    {
        Microsoft.AspNetCore.Builder.WebApplication app;
        try
        {
            app = await BrokerHost.BuildAppAsync(args).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            await Console.Error.WriteLineAsync($"refusing to serve — {ex.Message}").ConfigureAwait(false);
            return 1;
        }

        await app.RunAsync().ConfigureAwait(false);
        return 0;
    }

    private static int Usage()
    {
        Console.WriteLine("""
            tessera — secretless, identity-aware credential broker

            usage:
              tessera version
              tessera validate [--config tessera.json] [--grants grants.json]
              tessera serve    [--config tessera.json] [--grants grants.json]
            """);
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"unknown command '{command}'. Try 'tessera --help'.");
        return 2;
    }

    private static string? ArgValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
