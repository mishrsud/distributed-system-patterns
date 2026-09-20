# WSL Testcontainers Developer Environment Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver the approved Phase 1 workstation qualification and Phase 2 .NET 10 Testcontainers sample for Visual Studio, VS Code, WSL CLI, and Linux GitHub Actions.

**Architecture:** Visual Studio or VS Code provides the Windows UI, but the .NET testhost and debugger run inside managed Ubuntu 24.04 WSL. Testcontainers uses the local WSL Docker Unix socket and starts fixture-scoped SQL Server, PostgreSQL, and LocalStack containers without a Docker TCP listener.

**Tech Stack:** .NET SDK 10.0.401, C# 14, xUnit.net v3, Testcontainers for .NET 4.15.0, SQL Server 2025, PostgreSQL 18.6, LocalStack 2026.8.2 Base, AWS SDK for .NET v4, GitHub Actions Ubuntu runners.

---

## File map

Create the following focused units under `samples/wsl-testcontainers/`:

- `global.json` — select .NET SDK 10.0.401.
- `Directory.Build.props` — common compiler and test properties.
- `Directory.Packages.props` — central package versions.
- `WslTestcontainers.slnx` — sample solution.
- `testEnvironments.json` — Visual Studio WSL Test Explorer environment.
- `.vscode/extensions.json` — Windows/WSL extension recommendations.
- `.vscode/settings.json` — make WSL terminal and test logs discoverable without secrets.
- `src/SampleApplication/DependencyRecord.cs` — minimal domain value written to both databases.
- `src/SampleApplication/SampleApplication.csproj` — production assembly used by integration tests.
- `tests/EnvironmentQualification.Tests/*` — process, SDK, Docker-boundary, and smoke tests.
- `tests/Integration.Tests/Infrastructure/ImageCatalog.cs` — one source of dependency image names.
- `tests/Integration.Tests/Infrastructure/UniqueName.cs` — collision-free logical resource names.
- `tests/Integration.Tests/SqlServer/*` — SQL Server fixture and real query test.
- `tests/Integration.Tests/PostgreSql/*` — PostgreSQL fixture and real query test.
- `tests/Integration.Tests/LocalStack/*` — LocalStack fixture, client factory, service tests, and IAM fidelity test.
- `scripts/preflight.sh` — fast WSL/Docker/SDK/configuration validation.
- `docs/platform-manifest.md` — exact pilot compatibility manifest.
- `docs/runbook.md` — developer and support operations.
- `docs/localstack-fidelity.md` — executable-evidence record for LocalStack gaps.
- `.github/workflows/wsl-testcontainers-sample.yml` — Linux CI parity workflow.

Do not modify `transactional-outbox/gatherly`; the sample remains independently runnable.

## Version pins used by this plan

The implementation begins with these researched September 2026 pins and changes them only through an explicit compatibility update:

```text
.NET SDK                         10.0.401
Microsoft.NET.Test.Sdk          18.10.0
xunit.v3                        4.0.1
xunit.runner.visualstudio       4.0.0
Testcontainers                  4.15.0
Testcontainers.MsSql            4.15.0
Testcontainers.PostgreSql       4.15.0
Testcontainers.LocalStack       4.15.0
Microsoft.Data.SqlClient        7.1.0
Npgsql                          10.0.3
AWSSDK.S3                       4.0.103.3
AWSSDK.SQS                      4.0.100.14
AWSSDK.EventBridge              4.0.100.13
AWSSDK.SecretsManager           4.0.100.12
AWSSDK.IdentityManagement       4.0.103.6
AWSSDK.SecurityToken            4.0.101
SQL Server image                mcr.microsoft.com/mssql/server:2025-CU8-GDR1-ubuntu-24.04
PostgreSQL image                postgres:18.6-alpine3.23
LocalStack image                localstack/localstack:2026.8.2
Qualification image             alpine:3.23.3
```

Before implementation, verify each image digest in the allowed vendor registry and record the `linux/amd64` digest in `docs/platform-manifest.md`. Tags remain readable in code for the pilot; Phase 3 will move resolution to ECR.

### Task 1: Scaffold the pinned solution and IDE configuration

**Files:**
- Create: `samples/wsl-testcontainers/global.json`
- Create: `samples/wsl-testcontainers/Directory.Build.props`
- Create: `samples/wsl-testcontainers/Directory.Packages.props`
- Create: `samples/wsl-testcontainers/WslTestcontainers.slnx`
- Create: `samples/wsl-testcontainers/testEnvironments.json`
- Create: `samples/wsl-testcontainers/.vscode/extensions.json`
- Create: `samples/wsl-testcontainers/.vscode/settings.json`
- Create: `samples/wsl-testcontainers/src/SampleApplication/SampleApplication.csproj`
- Create: `samples/wsl-testcontainers/src/SampleApplication/DependencyRecord.cs`
- Create: `samples/wsl-testcontainers/tests/EnvironmentQualification.Tests/EnvironmentQualification.Tests.csproj`
- Create: `samples/wsl-testcontainers/tests/Integration.Tests/Integration.Tests.csproj`

- [ ] **Step 1: Add the SDK and build contract**

```json
{
  "sdk": {
    "version": "10.0.401",
    "rollForward": "latestPatch",
    "allowPrerelease": false
  }
}
```

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>14.0</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: Add central package versions**

Create `Directory.Packages.props` with `ManagePackageVersionsCentrally=true` and one `PackageVersion` entry for every version in the table above. Do not version packages inside project files.

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="18.10.0" />
    <PackageVersion Include="xunit.v3" Version="4.0.1" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="4.0.0" />
    <PackageVersion Include="Testcontainers" Version="4.15.0" />
    <PackageVersion Include="Testcontainers.MsSql" Version="4.15.0" />
    <PackageVersion Include="Testcontainers.PostgreSql" Version="4.15.0" />
    <PackageVersion Include="Testcontainers.LocalStack" Version="4.15.0" />
    <PackageVersion Include="Microsoft.Data.SqlClient" Version="7.1.0" />
    <PackageVersion Include="Npgsql" Version="10.0.3" />
    <PackageVersion Include="AWSSDK.S3" Version="4.0.103.3" />
    <PackageVersion Include="AWSSDK.SQS" Version="4.0.100.14" />
    <PackageVersion Include="AWSSDK.EventBridge" Version="4.0.100.13" />
    <PackageVersion Include="AWSSDK.SecretsManager" Version="4.0.100.12" />
    <PackageVersion Include="AWSSDK.IdentityManagement" Version="4.0.103.6" />
    <PackageVersion Include="AWSSDK.SecurityToken" Version="4.0.101" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Add the minimal production assembly**

```csharp
namespace SampleApplication;

public sealed record DependencyRecord(Guid Id, string Value);
```

- [ ] **Step 4: Add the two test projects**

Both projects use `Microsoft.NET.Test.Sdk`, `xunit.v3`, and `xunit.runner.visualstudio` with `PrivateAssets="all"` on the runner. The integration project additionally references the Testcontainers modules, data clients, AWS clients, and `SampleApplication`.

- [ ] **Step 5: Add the solution file**

Run from `samples/wsl-testcontainers`:

```bash
dotnet new slnx -n WslTestcontainers
dotnet sln WslTestcontainers.slnx add src/SampleApplication/SampleApplication.csproj
dotnet sln WslTestcontainers.slnx add tests/EnvironmentQualification.Tests/EnvironmentQualification.Tests.csproj
dotnet sln WslTestcontainers.slnx add tests/Integration.Tests/Integration.Tests.csproj
```

Expected: `dotnet sln WslTestcontainers.slnx list` reports exactly three projects.

- [ ] **Step 6: Add Visual Studio's WSL environment**

```json
{
  "version": "1",
  "environments": [
    {
      "name": "WSL-Ubuntu-24.04",
      "type": "wsl",
      "wslDistribution": "Ubuntu-24.04"
    }
  ]
}
```

- [ ] **Step 7: Add VS Code recommendations**

Recommend `ms-vscode-remote.remote-wsl`, `ms-dotnettools.csdevkit`, and `ms-dotnettools.csharp`. Do not add a Windows-local Docker extension requirement.

- [ ] **Step 8: Restore and build**

Run: `dotnet restore WslTestcontainers.slnx && dotnet build WslTestcontainers.slnx --no-restore`

Expected: all three projects build with zero warnings and create `packages.lock.json` files. Commit the lock files.

- [ ] **Step 9: Commit**

```bash
git add samples/wsl-testcontainers
git commit -m "build: scaffold WSL Testcontainers sample"
```

### Task 2: Add environment qualification tests and the Docker boundary smoke test

**Files:**
- Create: `samples/wsl-testcontainers/tests/EnvironmentQualification.Tests/ProcessIdentityTests.cs`
- Create: `samples/wsl-testcontainers/tests/EnvironmentQualification.Tests/DockerBoundaryTests.cs`
- Create: `samples/wsl-testcontainers/tests/EnvironmentQualification.Tests/ImageCatalog.cs`

- [ ] **Step 1: Write failing Linux identity tests**

```csharp
public sealed class ProcessIdentityTests
{
    [Fact]
    public void Testhost_runs_on_linux()
        => Assert.True(OperatingSystem.IsLinux(), RuntimeInformation.OSDescription);

    [Fact]
    public void Ubuntu_24_04_is_the_testhost_distribution()
    {
        var release = File.ReadAllText("/etc/os-release");
        Assert.Contains("VERSION_ID=\"24.04\"", release);
    }

    [Theory]
    [InlineData("DOCKER_HOST")]
    [InlineData("TESTCONTAINERS_HOST_OVERRIDE")]
    [InlineData("TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE")]
    [InlineData("TESTCONTAINERS_RYUK_DISABLED")]
    public void Docker_endpoint_overrides_are_not_set(string name)
        => Assert.True(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)), name);
}
```

- [ ] **Step 2: Run from Windows and confirm the guard fails**

Run with Windows `dotnet test` intentionally once.

Expected: `Testhost_runs_on_linux` fails. This proves the guard catches the unsupported execution path.

- [ ] **Step 3: Add the pinned qualification image**

```csharp
internal static class ImageCatalog
{
    internal const string Qualification = "alpine:3.23.3";
}
```

- [ ] **Step 4: Write the Docker Unix-socket smoke test**

```csharp
[Fact]
public async Task Testcontainers_runs_a_container_through_the_local_linux_daemon()
{
    Assert.True(File.Exists("/var/run/docker.sock"));

    await using var container = new ContainerBuilder(ImageCatalog.Qualification)
        .WithCommand("sh", "-c", "printf qualified")
        .Build();

    await container.StartAsync(TestContext.Current.CancellationToken);
    var logs = await container.GetLogsAsync(ct: TestContext.Current.CancellationToken);

    Assert.Contains("qualified", logs.Stdout);
}
```

- [ ] **Step 5: Run inside WSL**

Run: `dotnet test tests/EnvironmentQualification.Tests --logger "console;verbosity=detailed"`

Expected: all tests pass, Ryuk and the Alpine container are removed, and `ss -lntp | grep -E ':(2375|2376)'` returns no Docker listener.

- [ ] **Step 6: Commit**

```bash
git add samples/wsl-testcontainers/tests/EnvironmentQualification.Tests
git commit -m "test: qualify WSL Linux and Docker boundary"
```

### Task 3: Add shared image and naming infrastructure

**Files:**
- Create: `samples/wsl-testcontainers/tests/Integration.Tests/Infrastructure/ImageCatalog.cs`
- Create: `samples/wsl-testcontainers/tests/Integration.Tests/Infrastructure/UniqueName.cs`
- Create: `samples/wsl-testcontainers/tests/Integration.Tests/Infrastructure/UniqueNameTests.cs`

- [ ] **Step 1: Write a failing unique-name test**

```csharp
[Fact]
public void Create_returns_lowercase_collision_resistant_names()
{
    var first = UniqueName.Create("sample");
    var second = UniqueName.Create("sample");

    Assert.StartsWith("sample-", first);
    Assert.Matches("^[a-z0-9-]+$", first);
    Assert.NotEqual(first, second);
}
```

- [ ] **Step 2: Implement the naming helper**

```csharp
internal static class UniqueName
{
    internal static string Create(string prefix)
        => $"{prefix}-{Guid.NewGuid():N}";
}
```

- [ ] **Step 3: Add the centralized images**

```csharp
internal static class ImageCatalog
{
    internal const string MsSql = "mcr.microsoft.com/mssql/server:2025-CU8-GDR1-ubuntu-24.04";
    internal const string PostgreSql = "postgres:18.6-alpine3.23";
    internal const string LocalStack = "localstack/localstack:2026.8.2";
}
```

- [ ] **Step 4: Run and commit**

Run: `dotnet test tests/Integration.Tests --filter FullyQualifiedName~UniqueNameTests`

Expected: PASS.

```bash
git add samples/wsl-testcontainers/tests/Integration.Tests/Infrastructure
git commit -m "test: centralize dependency images and resource names"
```

### Task 4: Implement the SQL Server fixture and real database test

**Files:**
- Create: `samples/wsl-testcontainers/tests/Integration.Tests/SqlServer/SqlServerFixture.cs`
- Create: `samples/wsl-testcontainers/tests/Integration.Tests/SqlServer/SqlServerCollection.cs`
- Create: `samples/wsl-testcontainers/tests/Integration.Tests/SqlServer/SqlServerTests.cs`

- [ ] **Step 1: Write the test against an unimplemented fixture**

The test must create a table, insert a `DependencyRecord`, read it back, and compare both fields. It must not assert only that a connection opens.

```csharp
[Collection(SqlServerCollection.Name)]
public sealed class SqlServerTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task Round_trips_a_dependency_record()
    {
        var expected = new DependencyRecord(Guid.NewGuid(), "sql-server");
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE Records (Id uniqueidentifier NOT NULL, Value nvarchar(100) NOT NULL);
            INSERT INTO Records (Id, Value) VALUES (@id, @value);
            SELECT Id, Value FROM Records WHERE Id = @id;
            """;
        command.Parameters.AddWithValue("@id", expected.Id);
        command.Parameters.AddWithValue("@value", expected.Value);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(expected, new DependencyRecord(reader.GetGuid(0), reader.GetString(1)));
    }
}
```

- [ ] **Step 2: Run and verify the compile failure**

Run: `dotnet test tests/Integration.Tests --filter FullyQualifiedName~SqlServerTests`

Expected: FAIL because `SqlServerFixture` and `SqlServerCollection` do not exist.

- [ ] **Step 3: Implement the fixture and collection**

Use `new MsSqlBuilder(ImageCatalog.MsSql).Build()`, start in `InitializeAsync`, expose `GetConnectionString()`, and dispose in `DisposeAsync`. Implement xUnit v3 `IAsyncLifetime` and collection fixtures so the container is shared only by this collection.

- [ ] **Step 4: Run twice to prove cleanup and repeatability**

Run twice: `dotnet test tests/Integration.Tests --filter FullyQualifiedName~SqlServerTests`

Expected: PASS both times; no SQL Server container remains afterward.

- [ ] **Step 5: Commit**

```bash
git add samples/wsl-testcontainers/tests/Integration.Tests/SqlServer
git commit -m "test: add SQL Server Testcontainers sample"
```

### Task 5: Implement the PostgreSQL fixture and real database test

**Files:**
- Create: `samples/wsl-testcontainers/tests/Integration.Tests/PostgreSql/PostgreSqlFixture.cs`
- Create: `samples/wsl-testcontainers/tests/Integration.Tests/PostgreSql/PostgreSqlCollection.cs`
- Create: `samples/wsl-testcontainers/tests/Integration.Tests/PostgreSql/PostgreSqlTests.cs`

- [ ] **Step 1: Write the failing round-trip test**

Mirror the SQL Server behavior using PostgreSQL types and parameter syntax. The core SQL is:

```sql
CREATE TABLE records (id uuid NOT NULL, value text NOT NULL);
INSERT INTO records (id, value) VALUES (@id, @value);
SELECT id, value FROM records WHERE id = @id;
```

- [ ] **Step 2: Run and verify the compile failure**

Run: `dotnet test tests/Integration.Tests --filter FullyQualifiedName~PostgreSqlTests`

Expected: FAIL because the fixture and collection are absent.

- [ ] **Step 3: Implement the fixture**

Use `new PostgreSqlBuilder(ImageCatalog.PostgreSql).Build()`, expose its connection string, and implement asynchronous start/disposal using the same collection-scoped lifecycle as SQL Server.

- [ ] **Step 4: Run twice and commit**

Expected: PASS twice with no PostgreSQL container remaining.

```bash
git add samples/wsl-testcontainers/tests/Integration.Tests/PostgreSql
git commit -m "test: add PostgreSQL Testcontainers sample"
```

### Task 6: Implement LocalStack fixture and S3, SQS, EventBridge, and Secrets Manager tests

**Files:**
- Create: `samples/wsl-testcontainers/tests/Integration.Tests/LocalStack/LocalStackFixture.cs`
- Create: `samples/wsl-testcontainers/tests/Integration.Tests/LocalStack/LocalStackCollection.cs`
- Create: `samples/wsl-testcontainers/tests/Integration.Tests/LocalStack/LocalStackClientFactory.cs`
- Create: `samples/wsl-testcontainers/tests/Integration.Tests/LocalStack/LocalStackServiceTests.cs`

- [ ] **Step 1: Write a failing auth-token prerequisite test**

```csharp
[Fact]
public void LocalStack_auth_token_is_available_without_being_logged()
{
    var token = Environment.GetEnvironmentVariable("LOCALSTACK_AUTH_TOKEN");
    Assert.False(string.IsNullOrWhiteSpace(token),
        "Set LOCALSTACK_AUTH_TOKEN in the WSL environment or CI secret store.");
}
```

Never include the token value in an assertion message, diagnostic object, snapshot, or environment dump.

- [ ] **Step 2: Implement the fixture**

```csharp
var token = Environment.GetEnvironmentVariable("LOCALSTACK_AUTH_TOKEN")
    ?? throw new InvalidOperationException("LOCALSTACK_AUTH_TOKEN is required.");

Container = new LocalStackBuilder(ImageCatalog.LocalStack)
    .WithEnvironment("LOCALSTACK_AUTH_TOKEN", token)
    .WithEnvironment("ENFORCE_IAM", "1")
    .Build();
```

Expose the container's HTTP connection string. Use `BasicAWSCredentials("test", "test")`, region `us-east-1`, the LocalStack service URL, and `ForcePathStyle=true` for S3.

- [ ] **Step 3: Write S3 and Secrets Manager tests**

S3 creates a unique bucket, uploads UTF-8 content, downloads it, and asserts exact content. Secrets Manager creates a unique secret, retrieves it, and asserts the exact value.

- [ ] **Step 4: Write the SQS test**

Create a unique queue, send a message with a unique body, long-poll until it is received, and assert its body. Bound polling to 20 seconds and include queue URL plus LocalStack logs on timeout.

- [ ] **Step 5: Write the EventBridge-to-SQS test**

Create a unique event bus, SQS queue, rule matching `source=sample.wsl`, and SQS target. Add the required queue resource policy, publish an event, poll SQS, and assert the delivered EventBridge envelope contains the unique detail value.

- [ ] **Step 6: Run the LocalStack services**

Run: `dotnet test tests/Integration.Tests --filter FullyQualifiedName~LocalStackServiceTests --logger "console;verbosity=detailed"`

Expected: all four service tests pass using one fixture-scoped LocalStack container. A missing token fails with the prerequisite message; it never silently skips.

- [ ] **Step 7: Commit**

```bash
git add samples/wsl-testcontainers/tests/Integration.Tests/LocalStack
git commit -m "test: add LocalStack service integration samples"
```

### Task 7: Add executable IAM allow/deny fidelity evidence

**Files:**
- Create: `samples/wsl-testcontainers/tests/Integration.Tests/LocalStack/LocalStackIamTests.cs`
- Create: `samples/wsl-testcontainers/docs/localstack-fidelity.md`

- [ ] **Step 1: Write the denied-before-allow test**

The test uses the root LocalStack client to create a unique IAM user and access key. A client using that key attempts `s3:CreateBucket` and must receive `AmazonS3Exception` with HTTP 403 while `ENFORCE_IAM=1`.

- [ ] **Step 2: Run and verify the first half fails until policy attachment exists**

Expected initial result: the denied assertion passes and the later allowed assertion fails because no allow policy is attached.

- [ ] **Step 3: Attach the exact allow policy and finish the test**

Create and attach this policy:

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": "s3:CreateBucket",
      "Resource": "*"
    }
  ]
}
```

Retry through the user's credentials and assert bucket creation succeeds. Delete the access key, user, policy attachment, and policy in a `finally` block.

- [ ] **Step 4: Record fidelity conclusions**

`docs/localstack-fidelity.md` must contain one row per S3, SQS, EventBridge, Secrets Manager, and IAM behavior with columns: behavior, LocalStack result, AWS contract-test need, known difference, plan tier, evidence test.

Classify IAM enforcement as useful local evidence but not proof of AWS equivalence. Link LocalStack's IAM coverage page and note any operation whose allow/deny direction is not recorded upstream.

- [ ] **Step 5: Run and commit**

Run: `dotnet test tests/Integration.Tests --filter FullyQualifiedName~LocalStackIamTests`

Expected: one denied request followed by one allowed request; PASS.

```bash
git add samples/wsl-testcontainers/tests/Integration.Tests/LocalStack samples/wsl-testcontainers/docs/localstack-fidelity.md
git commit -m "test: capture LocalStack IAM fidelity evidence"
```

### Task 8: Add the preflight, platform manifest, and runbook

**Files:**
- Create: `samples/wsl-testcontainers/scripts/preflight.sh`
- Create: `samples/wsl-testcontainers/docs/platform-manifest.md`
- Create: `samples/wsl-testcontainers/docs/runbook.md`
- Create: `samples/wsl-testcontainers/tests/EnvironmentQualification.Tests/PreflightContractTests.cs`

- [ ] **Step 1: Write contract tests for preflight output**

Start a child process for `scripts/preflight.sh --format text` and assert successful output includes exactly these check identifiers: `os`, `ubuntu`, `dotnet`, `systemd`, `docker-service`, `docker-socket`, `docker-cli`, `docker-tcp`, `proxy`, `registry`, `localstack-token`.

- [ ] **Step 2: Run and verify failure because the script is missing**

Expected: FAIL with a missing-file or process-start error.

- [ ] **Step 3: Implement preflight**

Use `set -eu`, emit one `PASS|WARN|FAIL <identifier> <message>` line per check, never print environment values, and return nonzero for wrong OS/distro/SDK, inactive Docker, missing socket access, a Docker listener on 2375/2376, or failed qualification-image pull. Treat a missing LocalStack token as `FAIL` only when `--require-localstack` is supplied.

- [ ] **Step 4: Write the pilot manifest**

Record the exact versions from this plan, verified image digests, the exact managed WSL distribution name, current Visual Studio 2026 build, VS Code/C# Dev Kit versions, Docker Engine/API version, proxy/CA revision identifiers, and qualification date/owner. Values obtained from the managed enterprise laptop must be concrete before promotion.

- [ ] **Step 5: Write the runbook**

Include: checkout under `~/src`, `code .`, Visual Studio WSL environment selection, CLI commands, token provisioning, Docker startup, proxy/CA validation, VPN reconnect, `wsl --shutdown` recovery, safe stale-resource inspection/removal, log collection, and escalation routing. Explicitly prohibit port 2375, Ryuk disabling, committed secrets, and Windows `dotnet` for this sample.

- [ ] **Step 6: Run and commit**

Run: `./scripts/preflight.sh --require-localstack && dotnet test tests/EnvironmentQualification.Tests`

Expected: PASS on the managed baseline.

```bash
git add samples/wsl-testcontainers/scripts samples/wsl-testcontainers/docs samples/wsl-testcontainers/tests/EnvironmentQualification.Tests
git commit -m "docs: add WSL platform preflight and runbook"
```

### Task 9: Add GitHub Actions parity

**Files:**
- Create: `.github/workflows/wsl-testcontainers-sample.yml`

- [ ] **Step 1: Add a deliberately incomplete CI job**

Create an Ubuntu job that restores and builds but initially omits `dotnet test`. Run it and confirm the job cannot satisfy the sample acceptance check.

- [ ] **Step 2: Complete the workflow**

The workflow must:

1. Use `ubuntu-latest`.
2. Check out source.
3. Install SDK `10.0.401` from the sample's `global.json`.
4. Verify Docker and assert no custom `DOCKER_HOST`.
5. Restore locked packages.
6. Run environment qualification tests.
7. Run SQL Server and PostgreSQL tests concurrently only within the bounded xUnit policy.
8. Run LocalStack tests only when the protected `LOCALSTACK_AUTH_TOKEN` secret is present; for required branch checks, absence must fail with a clear prerequisite message rather than skip.
9. Upload sanitized TRX, container logs, and environment diagnostics on failure.
10. Never print the LocalStack token or the full environment.

- [ ] **Step 3: Run the workflow**

Expected: all test projects pass on Linux; Docker uses the local Unix socket; cleanup leaves no labeled Testcontainers resources.

- [ ] **Step 4: Commit**

```bash
git add .github/workflows/wsl-testcontainers-sample.yml
git commit -m "ci: validate WSL Testcontainers sample on Linux"
```

### Task 10: Execute the enterprise qualification matrix

**Files:**
- Create: `samples/wsl-testcontainers/docs/qualification-results.md`
- Modify: `samples/wsl-testcontainers/docs/platform-manifest.md`
- Modify: `samples/wsl-testcontainers/docs/runbook.md`

- [ ] **Step 1: Establish the CLI reference result**

Run from WSL:

```bash
./scripts/preflight.sh --require-localstack
dotnet test WslTestcontainers.slnx --no-restore --logger trx --results-directory TestResults/cli
```

Record test count, duration, Docker version/API, peak WSL memory, disk growth, and cleanup result.

- [ ] **Step 2: Qualify Visual Studio**

Select `WSL-Ubuntu-24.04` in Test Explorer. Confirm the same test count as CLI. Debug one SQL Server test and one LocalStack test with breakpoints before container start, during a real request, and during disposal. Repeat after Visual Studio restart and `wsl --shutdown`.

Expected: Linux identity tests pass and the debugger remains attached to the WSL testhost. Any failure blocks promotion of that exact Visual Studio build.

- [ ] **Step 3: Qualify VS Code**

Open the repository in a WSL window, confirm C# Dev Kit runs in `WSL: Ubuntu-24.04`, run the same tests from Test Explorer, and debug the same two dependency tests.

Expected: identical discovery and passing results.

- [ ] **Step 4: Qualify lifecycle, VPN, and abnormal cleanup**

Repeat off VPN, on VPN, and after VPN reconnection. Force-kill a testhost after all three dependency containers start, then verify Ryuk removes labeled containers, networks, and volumes. Repeat after Windows restart.

- [ ] **Step 5: Qualify filesystem behavior**

Run the full sample from `~/src` and record the baseline. Copy it to `/mnt/c` only for comparison, record duration and any projection/path failures, and retain WSL ext4 as the recommended support target.

- [ ] **Step 6: Compare GitHub Actions**

Record test count and dependency versions. Differences from the WSL result must be explained; unexplained missing tests fail qualification.

- [ ] **Step 7: Publish results and commit**

The results document includes pass/fail for every design-spec gate, owner, date, exact versions, evidence links, residual Visual Studio preview risk, and open LocalStack fidelity gaps. It contains no secret or raw environment dump.

```bash
git add samples/wsl-testcontainers/docs
git commit -m "docs: record enterprise WSL qualification results"
```

## Final verification

Run from WSL after all tasks:

```bash
cd samples/wsl-testcontainers
./scripts/preflight.sh --require-localstack
dotnet restore WslTestcontainers.slnx --locked-mode
dotnet build WslTestcontainers.slnx --no-restore
dotnet test WslTestcontainers.slnx --no-build --logger "console;verbosity=normal"
docker ps --filter label=org.testcontainers --format '{{.ID}}'
ss -lntp | grep -E ':(2375|2376)' || true
```

Expected:

- Preflight succeeds without printing credentials.
- Restore, build, and every test pass.
- No Testcontainers resources remain after the test run.
- No Docker listener exists on ports 2375 or 2376.
- Visual Studio, VS Code, CLI, and GitHub Actions test counts match.
- `docs/localstack-fidelity.md` identifies where real-AWS contract tests remain necessary.

If this workspace still lacks Git metadata during execution, skip the commit commands and record that limitation; do not initialize or attach a remote repository without user direction.
