using System.Text;
using Tessera.Core.Product;
using Xunit;

namespace Tessera.Core.Tests;

public sealed class DevelopmentExecutionTests
{
    [Fact]
    public void Registry_exposes_only_repository_status_with_direct_argv_and_no_client_arguments()
    {
        Assert.True(DevelopmentCommandProfiles.TryResolve("repository.status", [], out var profile));
        Assert.Equal("READ_ONLY", profile!.Effect);
        Assert.Equal("/usr/bin/git", profile.Executable);
        Assert.Equal(["status", "--short", "--branch"], profile.ArgumentPrefix);
        Assert.Equal("0", profile.Environment["GIT_OPTIONAL_LOCKS"]);
        Assert.Equal("safe.directory", profile.Environment["GIT_CONFIG_KEY_0"]);
        Assert.Equal("/workspace", profile.Environment["GIT_CONFIG_VALUE_0"]);

        Assert.False(DevelopmentCommandProfiles.TryResolve("repository.write", [], out _));
        Assert.False(DevelopmentCommandProfiles.TryResolve("repository.status", ["--porcelain"], out _));
        Assert.False(DevelopmentCommandProfiles.TryResolve("/bin/sh", ["-c", "git status"], out _));
    }

    [Fact]
    public void Output_is_utf8_normalized_control_stripped_redacted_and_combined_bounded()
    {
        var log = Encoding.UTF8.GetBytes("ok\0\u0001\nAuthorization: Bearer visible-secret\n" + new string('x', 20_000)).Concat(
            new byte[] { 0xff, 0xfe, (byte)'\n' }).Concat(
            Encoding.UTF8.GetBytes("api_key=second-secret\n" + new string('y', 20_000))).ToArray();

        var normalized = DevelopmentOutputNormalizer.Normalize(log, 32 * 1024);

        Assert.InRange(Encoding.UTF8.GetByteCount(normalized.Text), 1, 32 * 1024);
        Assert.DoesNotContain('\0', normalized.Text);
        Assert.DoesNotContain("visible-secret", normalized.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("second-secret", normalized.Text, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", normalized.Text, StringComparison.Ordinal);
        Assert.True(normalized.Truncated);
    }
}