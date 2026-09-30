using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace EnvironmentQualification.Tests;

public sealed partial class PreflightContractTests
{
    private static readonly string[] ExpectedCheckIdentifiers =
    [
        "os",
        "ubuntu",
        "dotnet",
        "systemd",
        "docker-service",
        "docker-socket",
        "docker-cli",
        "docker-tcp",
        "proxy",
        "registry",
        "localstack-token",
    ];

    [Fact]
    public async Task Preflight_reports_every_check_exactly_once_and_succeeds()
    {
        var result = await RunPreflightAsync(new Dictionary<string, string?>());

        var identifiers = result.Lines.Select(line =>
        {
            var match = CheckLine().Match(line);
            Assert.True(match.Success, $"Preflight line does not match 'PASS|WARN|FAIL <identifier> <message>': {line}");
            return match.Groups["identifier"].Value;
        }).ToArray();

        Assert.Equal(ExpectedCheckIdentifiers, identifiers);
        Assert.True(result.ExitCode == 0, $"Preflight failed:{Environment.NewLine}{result.Output}");
    }

    [Fact]
    public async Task Preflight_never_prints_the_localstack_token()
    {
        var sentinel = $"sentinel-{Guid.NewGuid():N}";

        var result = await RunPreflightAsync(new Dictionary<string, string?> { ["LOCALSTACK_AUTH_TOKEN"] = sentinel });

        Assert.DoesNotContain(sentinel, result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, result.Error, StringComparison.Ordinal);
    }

    private static async Task<PreflightResult> RunPreflightAsync(IReadOnlyDictionary<string, string?> environment)
    {
        var startInfo = new ProcessStartInfo("bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = FindSampleRoot(),
        };
        startInfo.ArgumentList.Add(Path.Combine("scripts", "preflight.sh"));
        startInfo.ArgumentList.Add("--format");
        startInfo.ArgumentList.Add("text");
        foreach (var (name, value) in environment)
        {
            startInfo.Environment[name] = value;
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start preflight.");
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        return new PreflightResult(process.ExitCode, await output, await error);
    }

    private static string FindSampleRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WslTestcontainers.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not locate the sample root containing WslTestcontainers.slnx.");
    }

    [GeneratedRegex("^(PASS|WARN|FAIL) (?<identifier>[a-z-]+) .+$")]
    private static partial Regex CheckLine();

    private sealed record PreflightResult(int ExitCode, string Output, string Error)
    {
        public string[] Lines { get; } = Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
