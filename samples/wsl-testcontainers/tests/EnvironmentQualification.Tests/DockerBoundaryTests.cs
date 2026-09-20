using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using Xunit;

namespace EnvironmentQualification.Tests;

public sealed class DockerBoundaryTests
{
    [Fact]
    public async Task Testcontainers_runs_a_container_through_the_local_linux_daemon()
    {
        Assert.True(
            File.Exists("/var/run/docker.sock"),
            "The local Linux Docker socket must exist at /var/run/docker.sock.");

        var dockerEndpoint = TestcontainersSettings.OS.DockerEndpointAuthConfig.Endpoint;
        var expectedDockerEndpoint = new Uri("unix:///var/run/docker.sock");

        Assert.True(
            string.Equals(expectedDockerEndpoint.AbsoluteUri, dockerEndpoint.AbsoluteUri, StringComparison.Ordinal),
            $"Testcontainers must resolve the local Unix Docker socket; actual scheme: '{dockerEndpoint.Scheme}'.");
        Assert.True(
            TestcontainersSettings.ResourceReaperEnabled,
            "Testcontainers Resource Reaper (Ryuk) must be enabled for cleanup.");

        await using var container = new ContainerBuilder(ImageCatalog.Qualification)
            .WithCommand("sh", "-c", "printf qualified")
            .WithWaitStrategy(
                Wait.ForUnixContainer().UntilMessageIsLogged(
                    "qualified",
                    waitStrategy => waitStrategy.WithMode(WaitStrategyMode.OneShot)))
            .Build();

        await container.StartAsync(TestContext.Current.CancellationToken);
        var logs = await container.GetLogsAsync(ct: TestContext.Current.CancellationToken);

        Assert.Contains("qualified", logs.Stdout);
    }
}
