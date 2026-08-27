using ProxyHarbor.Api;

namespace ProxyHarbor.Tests;

public sealed class CheckerNodeProvisionerTests
{
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
}
