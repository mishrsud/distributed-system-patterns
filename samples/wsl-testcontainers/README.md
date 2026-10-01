# WSL Testcontainers

A repeatable .NET 10 development environment for centrally managed **Windows 11** laptops, and a working
**Testcontainers** sample that proves it. You use Visual Studio or Visual Studio Code on Windows. Builds, tests,
the debugger, Testcontainers and the Docker Engine all run inside the managed **Ubuntu 24.04 WSL 2** distribution.

[Back to the repository README](../../README.md)

> **Status: pilot.** The sample builds and its tests are ready, but the managed-laptop qualification matrix has
> not been executed. [`docs/qualification-results.md`](docs/qualification-results.md) reads **NOT QUALIFIED**
> until every gate passes with linked evidence. Treat this as a pilot, not a supported platform.

## Contents

- [What this gives you](#what-this-gives-you)
- [Architecture](#architecture)
- [Constraints it is designed for](#constraints-it-is-designed-for)
- [Quick start](#quick-start)
- [Running the tests](#running-the-tests)
- [What the tests cover](#what-the-tests-cover)
- [IDE workflows](#ide-workflows)
- [Project layout](#project-layout)
- [Pinned versions](#pinned-versions)
- [Preflight checks](#preflight-checks)
- [LocalStack and fidelity](#localstack-and-fidelity)
- [Continuous integration](#continuous-integration)
- [Qualification gates](#qualification-gates)
- [Rules that are never relaxed](#rules-that-are-never-relaxed)
- [Troubleshooting](#troubleshooting)
- [Known limitations](#known-limitations)
- [Documentation map](#documentation-map)

## What this gives you

- A **reference workflow**: `dotnet test` from a WSL shell. Visual Studio, VS Code and CI must reproduce its
  results.
- **In-IDE test discovery, execution and debugging** with the test host inside WSL, from both Visual Studio and
  VS Code.
- Working **SQL Server**, **PostgreSQL** and **LocalStack** (S3, SQS, EventBridge, Secrets Manager, IAM)
  Testcontainers examples that perform real operations rather than only starting a container.
- A **preflight script** that fails fast, with actionable messages, before the expensive suite runs.
- A **platform manifest**, **runbook** and **qualification record** so the whole toolchain is versioned and
  supportable.
- An equivalent **Linux GitHub Actions** workflow.
- An executable **LocalStack fidelity record** that states where LocalStack is, and is not, a substitute for AWS.

## Architecture

The IDE is only a front end. The actual `dotnet`, testhost and debugger run in WSL and talk to Docker over the
native Unix socket.

```text
Visual Studio / VS Code UI on Windows
                |
                | Visual Studio WSL test environment
                | or VS Code WSL extension
                v
dotnet / testhost / debugger in Ubuntu-24.04 WSL
                |
                | unix:///var/run/docker.sock
                v
native Docker Engine in the same WSL distribution
                |
                +-- Ryuk
                +-- SQL Server
                +-- PostgreSQL
                +-- LocalStack
```

This preserves Linux process, path, networking and CI parity, and it never exposes the root-equivalent Docker
API across the Windows-to-WSL boundary.

### Rejected alternatives

| Alternative | Why it is rejected |
| --- | --- |
| Windows test host talking to Docker in WSL | Needs a network-exposed daemon, mutual TLS and client-key management, cross-boundary host resolution, and Linux-daemon bind-mount semantics. An unauthenticated `tcp://localhost:2375` listener is prohibited. |
| Third-party Windows named-pipe relay | No Microsoft, Docker or Testcontainers primary documentation establishes it as a supported integration. |
| WSL command line without IDE test support | In-IDE discovery and debugging are required. |

The full reasoning is in the [design document](../../docs/superpowers/specs/2026-09-20-wsl-testcontainers-developer-environment-design.md)
and the [boundary research](../../docs/research/windows-ide-wsl-testcontainers-boundary.md).

## Constraints it is designed for

- WSL 2 and Ubuntu 24.04 are provided by central IT. Engineers have **no Windows administrator rights** and no
  Microsoft Store access.
- Engineers have controlled `sudo` inside Ubuntu and may install APT packages.
- **Docker Desktop is prohibited.** Native rootful Docker Engine is already installed in WSL. Developers may join
  the `docker` group; the root-equivalent risk is explicitly accepted.
- Required container registries are reachable through the corporate proxy. Corporate resources need VPN. TLS
  inspection and corporate CA trust stay in force.
- Production runs Linux containers on AWS ECS. CI is GitHub Actions on Linux.

## Quick start

All commands run in a WSL `Ubuntu-24.04` shell. For full first-time setup (docker group, proxy, CA, token), follow
the [runbook](docs/runbook.md#first-time-setup).

1. **Confirm the distribution name.** In Windows PowerShell it must read exactly `Ubuntu-24.04`:

   ```powershell
   wsl.exe --list --verbose
   ```

2. **Check out on WSL ext4**, not under `/mnt/c`:

   ```bash
   mkdir -p ~/src && cd ~/src
   git clone <repository-url>
   cd <repository>/samples/wsl-testcontainers
   ```

3. **Provide the LocalStack token** from your approved secret store, outside any repository file:

   ```bash
   printf '\nexport LOCALSTACK_AUTH_TOKEN=%s\n' '<token from secret store>' >> ~/.profile
   chmod 600 ~/.profile
   ```

   Open a new login shell. Never echo it, paste it into chat or tickets, or commit it.

4. **Run preflight.** Every line must be `PASS` or `WARN`:

   ```bash
   ./scripts/preflight.sh --require-localstack
   ```

5. **Build and test:**

   ```bash
   dotnet restore WslTestcontainers.slnx --locked-mode
   dotnet build WslTestcontainers.slnx --no-restore
   dotnet test WslTestcontainers.slnx --no-build --logger "console;verbosity=normal"
   ```

## Running the tests

Run everything:

```bash
dotnet test WslTestcontainers.slnx
```

Run one area:

```bash
dotnet test tests/Integration.Tests --filter "FullyQualifiedName~SqlServerTests"
dotnet test tests/Integration.Tests --filter "FullyQualifiedName~PostgreSqlTests"
dotnet test tests/Integration.Tests --filter "FullyQualifiedName~LocalStack"
dotnet test tests/EnvironmentQualification.Tests
```

Capture a TRX report for diagnosis or evidence:

```bash
dotnet test WslTestcontainers.slnx --logger trx --results-directory TestResults
```

The suite discovers **18 tests**. A required test must never be silently skipped: the LocalStack tests fail with
a clear prerequisite message when `LOCALSTACK_AUTH_TOKEN` is missing, rather than being skipped.

> **Runner note.** The sample uses the VSTest path (`UseMicrosoftTestingPlatformRunner=false`,
> `IsTestingPlatformApplication=false`) because the .NET 10 SDK rejects `dotnet test` over VSTest for projects on
> Microsoft.Testing.Platform, and xunit.v3 4.x is on that platform by default. Moving to the platform later
> changes CLI filter and logger syntax and needs re-qualification.

## What the tests cover

### `EnvironmentQualification.Tests`: is the platform what we think it is?

| Test | Proves |
| --- | --- |
| `Testhost_runs_on_linux` | The testhost is Linux, not Windows `dotnet.exe`. Fails by design on Windows and macOS. |
| `Ubuntu_24_04_is_the_testhost_distribution` | The distribution is Ubuntu 24.04 |
| `Docker_endpoint_overrides_are_not_set` | `DOCKER_HOST`, `DOCKER_CONTEXT`, `DOCKER_TLS*`, `TESTCONTAINERS_*` overrides are unset |
| `Testcontainers_runs_a_container_through_the_local_linux_daemon` | Testcontainers can run a container through the local Unix socket |
| `Preflight_reports_every_check_exactly_once_and_succeeds` | `scripts/preflight.sh` emits every check once |
| `Preflight_never_prints_the_localstack_token` | The token never leaks into preflight output |

### `Integration.Tests`: do the dependencies really work?

| Test | Proves |
| --- | --- |
| `SqlServerTests.Round_trips_a_dependency_record` | Create a table, insert, query and assert against the pinned SQL Server image |
| `PostgreSqlTests.Round_trips_a_dependency_record` | The same workflow against PostgreSQL |
| `LocalStackServiceTests.S3_round_trips_utf8_content` | S3 bucket and object operations with exact UTF-8 content |
| `LocalStackServiceTests.Secrets_manager_round_trips_secret_string` | Secrets Manager create and retrieve |
| `LocalStackServiceTests.Sqs_delivers_sent_message` | SQS send and long-poll receive |
| `LocalStackServiceTests.EventBridge_delivers_matching_event_to_sqs` | An EventBridge custom-bus rule delivering to an SQS target |
| `LocalStackIamTests.User_is_denied_create_bucket_until_an_allow_policy_is_attached` | Representative IAM deny, then allow |
| `UniqueNameTests` | Per-test resource names are distinct, lowercase and hyphenated |

### Testcontainers conventions the fixtures follow

- Dependency image references live in **one image catalog** (`ImageCatalog.cs`), so substituting ECR images later
  needs no fixture changes.
- One container per fixture or collection; **unique** databases, buckets, queues, buses and secrets per test.
- **Random host ports**, read back from Testcontainers. Explicit readiness checks. Asynchronous disposal.
- **Ryuk stays enabled** for normal and abnormal cleanup.
- Avoid host bind mounts. Docker evaluates source paths on the Linux daemon host, so Windows drive and UNC paths
  are not valid sources. Prefer resource-copy APIs, generated resources and named volumes.

## IDE workflows

### Visual Studio 2026

1. Open `\\wsl.localhost\Ubuntu-24.04\home\<user>\src\<repository>\samples\wsl-testcontainers\WslTestcontainers.slnx`.
2. In Test Explorer, choose the **WSL-Ubuntu-24.04** target environment. It comes from the committed
   [`testEnvironments.json`](testEnvironments.json), whose `wslDistribution` must exactly match
   `wsl.exe --list --verbose`.
3. Run or debug tests. The testhost and debugger run inside WSL.

Visual Studio remote testing is an **experimental preview**. Use only the build recorded in the
[platform manifest](docs/platform-manifest.md). A failed qualification blocks promotion of that Visual Studio
build; it never justifies exposing Docker to a Windows test process.

### Visual Studio Code

1. From the WSL shell in this directory, run `code .`.
2. Confirm the status bar shows **`WSL: Ubuntu-24.04`**. If it does not, close the window and repeat step 1. A
   plain local Windows window is unsupported because it moves the test host out of Linux.
3. Install the recommended extensions ([`.vscode/extensions.json`](.vscode/extensions.json)). C# Dev Kit and the
   C# extension must be installed **in the WSL extension host**, not locally.
4. Use the Testing view to run and debug.

## Project layout

```text
samples/wsl-testcontainers/
|-- WslTestcontainers.slnx           Solution
|-- global.json                      Pins .NET SDK 10.0.401, rollForward latestPatch, no prerelease
|-- Directory.Build.props            C# 14, VSTest runner settings
|-- Directory.Packages.props         Central package versions
|-- testEnvironments.json            Visual Studio WSL test environment
|-- .vscode/                         Recommended extensions and settings
|-- scripts/
|   `-- preflight.sh                 Fast WSL / Docker / SDK prerequisite check
|-- src/
|   `-- SampleApplication/           Minimal DependencyRecord used by the fixtures
|-- tests/
|   |-- EnvironmentQualification.Tests/   Linux identity, SDK, socket, Docker, cleanup prerequisites
|   `-- Integration.Tests/                SQL Server, PostgreSQL, LocalStack fixtures and tests
`-- docs/
    |-- runbook.md                   Setup and operations
    |-- platform-manifest.md         Exact pinned versions and images
    |-- qualification-results.md     Pass/fail record for each gate
    `-- localstack-fidelity.md       What LocalStack does and does not prove
```

NuGet lock files (`packages.lock.json`) are committed and CI restores with `--locked-mode`.

## Pinned versions

The authoritative list is the [platform manifest](docs/platform-manifest.md). Highlights:

| Component | Version |
| --- | --- |
| .NET SDK | 10.0.401 (`rollForward: latestPatch`, no prerelease) |
| Testcontainers (core, MsSql, PostgreSql, LocalStack) | 4.15.0 |
| xunit.v3 / xunit.runner.visualstudio | 4.0.1 / 4.0.0 |
| SQL Server image | `mcr.microsoft.com/mssql/server:2025-CU8-GDR1-ubuntu-24.04` (linux/amd64 only) |
| PostgreSQL image | `postgres:18.6-alpine3.23` |
| LocalStack image | `localstack/localstack:2026.8.2` |
| Ryuk (pulled by Testcontainers) | `testcontainers/ryuk:0.14.0` |

Change a pin only through a compatibility update: change the source file and the manifest in the same commit,
re-run preflight and the full suite from WSL and CI, then re-run any affected qualification gate and record it.

## Preflight checks

`scripts/preflight.sh` emits one `PASS|WARN|FAIL <identifier> <message>` line per check and exits nonzero when any
check fails. It never prints environment variable values.

```bash
./scripts/preflight.sh                       # core checks
./scripts/preflight.sh --require-localstack  # also requires LOCALSTACK_AUTH_TOKEN
```

| Identifier | Verifies |
| --- | --- |
| `os` | Testhost is Linux (inside WSL, or a CI runner) |
| `ubuntu` | Distribution is Ubuntu 24.04 |
| `dotnet` | A Linux .NET SDK satisfies `global.json` (not a Windows binary) |
| `systemd` | systemd is PID 1 |
| `docker-service` | `docker.service` is active |
| `docker-socket` | `/var/run/docker.sock` exists and is accessible |
| `docker-cli` | The CLI reaches the daemon and no Docker endpoint overrides are set |
| `docker-tcp` | No listener on port 2375 or 2376 |
| `proxy`, `registry` | Daemon proxy and registry reachability |
| `localstack-token` | Token present (with `--require-localstack`) |

Failures fall into five categories, which decide who looks first: **environment**, **network/trust**,
**supply chain**, **dependency** and **cleanup**. The [runbook](docs/runbook.md#escalation-routing) maps each
identifier to an owner.

## LocalStack and fidelity

LocalStack is useful, not identical to AWS. The [fidelity record](docs/localstack-fidelity.md) classifies each
behaviour the sample exercises and points at the test that provides the evidence:

1. Demonstrably equivalent, suitable for routine LocalStack testing.
2. Partially emulated, with the limitation stated beside the test.
3. Insufficiently equivalent, needing an ephemeral real-AWS contract test.

| Behaviour | AWS contract-test need |
| --- | --- |
| S3 create and put/get | Low |
| Secrets Manager create and retrieve | Medium (rotation, KMS and resource policies not exercised) |
| SQS send and receive | Medium (visibility timeout, redrive, FIFO not exercised) |
| EventBridge rule to SQS target | High (rule matching and target resource-policy evaluation) |
| IAM identity-policy allow/deny | High (useful local evidence, not proof of AWS equivalence) |

Configuration: `ENFORCE_IAM=1`, dummy credentials `test`/`test`, plan tier **LocalStack Base** assumed. The auth
token comes from your WSL user environment or the GitHub Actions secret. **Every row currently reads "Not yet
executed"**; update it from a real run with a linked TRX or CI result.

## Continuous integration

[`.github/workflows/wsl-testcontainers-sample.yml`](../../.github/workflows/wsl-testcontainers-sample.yml) runs on
push to `main` and on pull requests that touch this sample, and can be run manually.

- Runs on a GitHub-hosted **`ubuntu-24.04`** runner, pinned deliberately: `ubuntu-latest` drift would break the
  Ubuntu 24.04 identity test.
- Installs the SDK from `global.json`, restores in `--locked-mode`, and uses the runner's local Docker socket with
  no `DOCKER_HOST`.
- Reads `LOCALSTACK_AUTH_TOKEN` from a repository secret. Add that secret before expecting the LocalStack tests to
  pass in CI.
- Uses the same `global.json`, central packages, image catalog and test set as the workstation.

## Qualification gates

The toolchain is supported only after all ten gates pass on the managed image, each with an evidence link:

| # | Gate |
| --- | --- |
| 1 | Identity: IDEs and CLI prove Linux, Ubuntu 24.04, the pinned SDK and a Linux source path |
| 2 | Discovery: Visual Studio, VS Code, CLI and CI discover the same tests |
| 3 | Debugging: breakpoints before and after container start, during a request, and during disposal |
| 4 | Docker boundary: Unix socket only; no TCP listener or named-pipe relay |
| 5 | Dependencies: SQL Server, PostgreSQL and LocalStack run concurrently on random ports |
| 6 | Cleanup: Ryuk cleans after success, assertion failure, cancellation and forced testhost kill |
| 7 | Lifecycle: repeat after IDE restart, `wsl --shutdown`, Windows restart and clean restore |
| 8 | Network/security: off VPN, on VPN, after reconnect; proxy, registry, CA and DNS |
| 9 | Filesystem: WSL ext4 qualified; `/mnt/c` caveats recorded |
| 10 | CI parity: same assembly on the Linux runner with equivalent SDK, packages, images and cleanup |

Qualification **fails** if port 2375 is listening, Ryuk is disabled on the workstation, a test silently runs
through Windows `dotnet`, or a required test is silently skipped. Current status for every gate is in
[`docs/qualification-results.md`](docs/qualification-results.md).

## Rules that are never relaxed

These are not supported even temporarily for troubleshooting:

- **No Docker TCP listener** on `2375` or `2376`, and no `-H tcp://` on `dockerd`.
- **Never disable Ryuk** (`TESTCONTAINERS_RYUK_DISABLED`).
- **Never commit secrets:** `LOCALSTACK_AUTH_TOKEN`, proxy credentials, AWS credentials, Docker client keys, ECR
  tokens. That includes `.env`, `launchSettings.json`, `.runsettings` and VS Code settings.
- **Never run the sample with Windows `dotnet.exe`** or a Windows-local test host.
- **No Docker Desktop** on managed laptops.

## Troubleshooting

Start with `./scripts/preflight.sh --require-localstack`; each `FAIL` carries its own fix. Common cases:

| Symptom | Likely cause and fix |
| --- | --- |
| `docker-socket` fails, "no access" | Not in the `docker` group. `sudo usermod -aG docker "$USER"`, then `wsl.exe --shutdown` and reopen the shell. |
| `systemd` fails | Enable `[boot] systemd=true` in `/etc/wsl.conf`, then `wsl.exe --shutdown`. |
| `docker-cli` fails with overrides | Unset the listed `DOCKER_*` / `TESTCONTAINERS_*` variables. |
| TLS error from `curl` or `docker pull` | Corporate CA is not trusted inside Ubuntu. A trusted Windows certificate is not enough. |
| Registry or DNS failure | Run `wsl.exe --shutdown`, reopen, retry; otherwise collect logs and escalate to Networking. |
| `Testhost_runs_on_linux` fails | You are running Windows or macOS `dotnet`. Run from the WSL shell. |
| LocalStack tests fail with a prerequisite message | `LOCALSTACK_AUTH_TOKEN` is not set in your login shell. |
| IDE lost its connection | Restart the IDE after `wsl --shutdown`; WSL connections do not survive it. |
| Leaked containers | Inspect, then remove only `org.testcontainers`-labelled resources. See the runbook. |

The [runbook](docs/runbook.md) has the full procedures: proxy and CA validation, VPN recovery, WSL restart,
stale-resource inspection and cleanup, safe log collection, and escalation routing. **Never** run
`docker system prune -a` or an unfiltered `docker volume prune`.

## Known limitations

- **Qualification is pending.** No gate has been run on the managed laptop yet.
- **Visual Studio remote testing is an experimental preview.** Qualification covers only the exact recorded build.
- **LocalStack results are unrecorded.** The fidelity matrix reads "Not yet executed" until it is run with a token.
- **CI container logs are usually empty on failure.** Ryuk removes containers before the diagnostics step. The
  LocalStack SQS/EventBridge timeout path embeds sanitized logs in the TRX failure message; SQL Server and
  PostgreSQL readiness failures do not, so rerun locally to capture them.
- **The SQL Server image is `linux/amd64` only.** It will not run natively on Arm64 hosts.
- **Testcontainers from inside a container through the Docker Desktop socket** failed with a `ResourceReaperException`
  during development on macOS. This is not representative of WSL or GitHub-hosted runners, but it has not been
  proven there either.
- **Images are pinned by readable tag**, not digest, for the pilot. Digests are recorded in the manifest; moving
  to ECR resolution is a later phase.
- **Out of scope for now:** mirroring images into ECR and Testcontainers image substitution, organization-wide
  rollout rings, large-scale performance tuning, a LocalStack Ultimate or Enterprise purchasing decision, and broad
  real-AWS contract-test infrastructure.

## Documentation map

| Document | Purpose |
| --- | --- |
| [`docs/runbook.md`](docs/runbook.md) | First-time setup, daily workflow, recovery, cleanup, escalation |
| [`docs/platform-manifest.md`](docs/platform-manifest.md) | Exact versions, images and digests under change control |
| [`docs/qualification-results.md`](docs/qualification-results.md) | Gate-by-gate pass/fail record and pre-qualification evidence |
| [`docs/localstack-fidelity.md`](docs/localstack-fidelity.md) | LocalStack behaviour matrix and AWS contract-test needs |
| [Design document](../../docs/superpowers/specs/2026-09-20-wsl-testcontainers-developer-environment-design.md) | Architecture decision, scope, gates, CI design |
| [Implementation plan](../../docs/superpowers/plans/2026-09-20-wsl-testcontainers-developer-environment.md) | Task-by-task build plan |
| [Environment research](../../docs/research/windows-wsl2-dotnet10-testcontainers-development-environment.md) | WSL 2, Docker and .NET 10 research |
| [Boundary research](../../docs/research/windows-ide-wsl-testcontainers-boundary.md) | Why the test host lives in WSL |

**Ownership.** Platform Engineering owns the compatibility baseline and first-line diagnosis; Principal
Engineering guides technical policy. Endpoint management, networking, security, AWS and application teams own
escalations for their layers.
