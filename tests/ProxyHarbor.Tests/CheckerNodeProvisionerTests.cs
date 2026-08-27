using ProxyHarbor.Api;

namespace ProxyHarbor.Tests;

public sealed class CheckerNodeProvisionerTests
{
    private static readonly Uri ControlPlane = new("https://proxy.example.test");

    [Fact]
    public void AcceptsPublicIpv4RootUserAndBoundedWorkload()
    {
        CheckerNodeProvisioner.Validate(new CheckerNodeDeploymentRequest(
            "checker-eu-1", "1.1.1.1", 22, "root", "long-enough-password", 200, 400));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.10")]
    [InlineData("192.168.1.10")]
    [InlineData("169.254.1.1")]
    public void RejectsNonPublicHost(string host)
    {
        var request = new CheckerNodeDeploymentRequest(
            "checker-eu-1", host, 22, "root", "long-enough-password", 200, 400);

        Assert.Throws<ArgumentException>(() => CheckerNodeProvisioner.Validate(request));
    }

    [Theory]
    [InlineData("Root")]
    [InlineData("root;reboot")]
    [InlineData("root user")]
    public void RejectsUnsafeSshUsername(string username)
    {
        var request = new CheckerNodeDeploymentRequest(
            "checker-eu-1", "1.1.1.1", 22, username, "long-enough-password", 200, 400);

        Assert.Throws<ArgumentException>(() => CheckerNodeProvisioner.Validate(request));
    }

    [Theory]
    [InlineData(0, 400)]
    [InlineData(1001, 400)]
    [InlineData(200, 0)]
    [InlineData(200, 10001)]
    public void RejectsUnsafeWorkloadBounds(int concurrency, int batchSize)
    {
        var request = new CheckerNodeDeploymentRequest(
            "checker-eu-1", "1.1.1.1", 22, "root", "long-enough-password",
            concurrency, batchSize);

        Assert.Throws<ArgumentException>(() => CheckerNodeProvisioner.Validate(request));
    }

    [Fact]
    public void RejectsControlCharactersInTransientPassword()
    {
        var request = new CheckerNodeDeploymentRequest(
            "checker-eu-1", "1.1.1.1", 22, "root", "valid-password\n", 200, 400);

        Assert.Throws<ArgumentException>(() => CheckerNodeProvisioner.Validate(request));
    }

    [Fact]
    public void InstallScriptPrefersExistingDockerAndContainsHardenedSystemdFallback()
    {
        var options = new CheckerAgentDeploymentOptions();
        var script = CheckerNodeProvisioner.BuildInstallScript(
            Guid.Parse("11111111-2222-3333-4444-555555555555"), "dGVzdC10b2tlbg==", ControlPlane, options);

        Assert.Contains("if command -v docker >/dev/null 2>&1 && docker info", script, StringComparison.Ordinal);
        Assert.Contains("proxyharbor-checker-agent-next", script, StringComparison.Ordinal);
        Assert.Contains("systemctl enable proxyharbor-checker-agent.service", script, StringComparison.Ordinal);
        Assert.Contains("chown root:proxyharbor-checker /opt/proxyharbor-checker", script, StringComparison.Ordinal);
        Assert.Contains("chmod 0750 /opt/proxyharbor-checker", script, StringComparison.Ordinal);
        Assert.Contains("ProtectSystem=strict", DecodeUnit(script), StringComparison.Ordinal);
        Assert.Contains("NoNewPrivileges=true", DecodeUnit(script), StringComparison.Ordinal);
        Assert.Contains("sha256sum --check proxyharbor-checker-agent.tar.gz.sha256 >/dev/null", script, StringComparison.Ordinal);
        Assert.Contains("sha512sum --check", script, StringComparison.Ordinal);
        Assert.Contains("aspnetcore-runtime-10.0.11-$runtime_rid.tar.gz", script, StringComparison.Ordinal);
        Assert.Contains("^Microsoft.AspNetCore.App 10.0.11 ", script, StringComparison.Ordinal);
        Assert.Contains("runtime_replaced=1", script, StringComparison.Ordinal);
        Assert.Contains("mv /opt/proxyharbor-checker/dotnet.previous \"$runtime_root\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("apt-get install", script, StringComparison.Ordinal);
        Assert.DoesNotContain("dGVzdC10b2tlbg==\n", script, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsUnsafeNativeDeploymentConfiguration()
    {
        var options = new CheckerAgentDeploymentOptions
        {
            NativeAssetBaseUrl = "https://example.test/release?asset=agent"
        };

        Assert.Throws<InvalidOperationException>(() => CheckerNodeProvisioner.ValidateDeploymentOptions(options));
        options.NativeAssetBaseUrl = "https://example.test/release";
        options.NativeRuntimeLinuxX64Sha512 = "not-a-checksum";
        Assert.Throws<InvalidOperationException>(() => CheckerNodeProvisioner.ValidateDeploymentOptions(options));
    }

    private static string DecodeUnit(string script)
    {
        const string prefix = "printf '%s' '";
        const string suffix = "' | base64 -d > \"$work/proxyharbor-checker-agent.service\"";
        var suffixIndex = script.IndexOf(suffix, StringComparison.Ordinal);
        Assert.True(suffixIndex > 0);
        var prefixIndex = script.LastIndexOf(prefix, suffixIndex, StringComparison.Ordinal);
        Assert.True(prefixIndex >= 0);
        var encoded = script[(prefixIndex + prefix.Length)..suffixIndex];
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
    }
}
