using Microsoft.Data.Sqlite;
using Tessera.Broker;
using Tessera.Core.Configuration;
using Tessera.Persistence.Sqlite;

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
            "backup" => await BackupAsync(args).ConfigureAwait(false),
            "verify-backup" => await VerifyBackupAsync(args).ConfigureAwait(false),
            "restore" => await RestoreAsync(args).ConfigureAwait(false),
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

    private static async Task<int> BackupAsync(string[] args)
    {
        var database=ArgValue(args,"--database");if(string.IsNullOrWhiteSpace(database))return Missing("--database");
        var output=ArgValue(args,"--output")??Path.Combine(Path.GetDirectoryName(Path.GetFullPath(database))!,"backups",$"tessera-product-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}.db");
        try{await new SqliteKernelStore(database).BackupAsync(output).ConfigureAwait(false);var verification=await SqliteKernelStore.VerifyBackupAsync(output).ConfigureAwait(false);Console.WriteLine($"backup: {Path.GetFullPath(output)}");PrintVerification(verification);return verification.IntegrityOk?0:1;}
        catch(Exception exception)when(exception is IOException or SqliteException or InvalidDataException or ArgumentException){await Console.Error.WriteLineAsync($"backup failed — {exception.Message}").ConfigureAwait(false);return 1;}
    }

    private static async Task<int> VerifyBackupAsync(string[] args)
    {
        var database=ArgValue(args,"--database");if(string.IsNullOrWhiteSpace(database))return Missing("--database");
        try{var verification=await SqliteKernelStore.VerifyBackupAsync(database).ConfigureAwait(false);PrintVerification(verification);return verification.IntegrityOk?0:1;}
        catch(Exception exception)when(exception is IOException or SqliteException or InvalidDataException or ArgumentException){await Console.Error.WriteLineAsync($"verification failed — {exception.Message}").ConfigureAwait(false);return 1;}
    }

    private static async Task<int> RestoreAsync(string[] args)
    {
        var backup=ArgValue(args,"--backup");if(string.IsNullOrWhiteSpace(backup))return Missing("--backup");var output=ArgValue(args,"--output");if(string.IsNullOrWhiteSpace(output))return Missing("--output");
        try{await SqliteKernelStore.RestoreBackupAsync(backup,output).ConfigureAwait(false);var verification=await SqliteKernelStore.VerifyBackupAsync(output).ConfigureAwait(false);Console.WriteLine($"restored: {Path.GetFullPath(output)}");PrintVerification(verification);return verification.IntegrityOk?0:1;}
        catch(Exception exception)when(exception is IOException or SqliteException or InvalidDataException or ArgumentException){await Console.Error.WriteLineAsync($"restore failed — {exception.Message}").ConfigureAwait(false);return 1;}
    }

    private static void PrintVerification(SqliteBackupVerification value)
        =>Console.WriteLine($"integrity: {(value.IntegrityOk?"ok":"failed")}; schema: v{value.SchemaVersion}; conversations: {value.Conversations}; memory: {value.Assertions}; jobs: {value.Jobs}; accounts: {value.Accounts}; actions: {value.Actions}");

    private static int Missing(string argument){Console.Error.WriteLine($"missing required argument {argument}");return 2;}

    private static int Usage()
    {
        Console.WriteLine("""
            tessera — secretless, identity-aware credential broker

            usage:
              tessera version
              tessera validate [--config tessera.json] [--grants grants.json]
              tessera serve    [--config tessera.json] [--grants grants.json]
              tessera backup        --database tessera-product.db [--output backup.db]
              tessera verify-backup --database backup.db
              tessera restore       --backup backup.db --output restored.db
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
