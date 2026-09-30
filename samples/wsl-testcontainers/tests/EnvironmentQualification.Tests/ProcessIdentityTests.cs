using System.Runtime.InteropServices;
using Xunit;

namespace EnvironmentQualification.Tests;

public sealed class ProcessIdentityTests
{
    [Fact]
    public void Testhost_runs_on_linux()
    {
        Assert.True(OperatingSystem.IsLinux(), RuntimeInformation.OSDescription);
    }

    [Fact]
    public void Ubuntu_24_04_is_the_testhost_distribution()
    {
        var operatingSystemRelease = File.ReadAllText("/etc/os-release");

        Assert.Contains("VERSION_ID=\"24.04\"", operatingSystemRelease);
    }

    [Theory]
    [InlineData("DOCKER_HOST")]
    [InlineData("TESTCONTAINERS_HOST_OVERRIDE")]
    [InlineData("TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE")]
    [InlineData("TESTCONTAINERS_RYUK_DISABLED")]
    public void Docker_endpoint_overrides_are_not_set(string variableName)
    {
        var value = Environment.GetEnvironmentVariable(variableName);

        Assert.True(
            string.IsNullOrWhiteSpace(value),
            $"{variableName} must not be configured.");
    }
}
