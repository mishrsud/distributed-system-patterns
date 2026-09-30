# Enterprise qualification results

Pass/fail record for every qualification gate in the [design spec](../../../docs/superpowers/specs/2026-09-20-wsl-testcontainers-developer-environment-design.md#qualification-gates), run on the managed enterprise laptop against the versions in [`platform-manifest.md`](platform-manifest.md).

- **Overall status:** NOT QUALIFIED — the managed-laptop matrix has not been executed
- **Qualification owner:** PENDING (Platform Engineering)
- **Qualification date:** PENDING
- **Managed image / manifest revision:** PENDING

A gate is `PASS` only with an evidence link (TRX file, CI run URL, or screen recording stored in the approved location). This document must never contain secrets, proxy URLs, or raw environment dumps.

## Gate summary

| # | Gate | Status | Evidence |
|---|---|---|---|
| 1 | Identity: both IDEs and the WSL CLI prove Linux, Ubuntu 24.04, SDK 10.0.401, and a Linux source path | NOT RUN | — |
| 2 | Discovery: Visual Studio, VS Code, WSL CLI, and GitHub Actions discover the same test set | NOT RUN | — |
| 3 | Debugging: both IDEs hit breakpoints before and after container start, during a real request, and during disposal | NOT RUN | — |
| 4 | Docker boundary: the testhost uses `/var/run/docker.sock`; no TCP listener or named-pipe relay | NOT RUN | — |
| 5 | Dependencies: SQL Server, PostgreSQL, and LocalStack start concurrently and perform real operations on random ports | NOT RUN | — |
| 6 | Cleanup: Ryuk cleans up after success, assertion failure, cancellation, and forced testhost kill | NOT RUN | — |
| 7 | Lifecycle: repeat after IDE restart, `wsl --shutdown`, Windows restart, and clean package restore | NOT RUN | — |
| 8 | Network/security: off VPN, on VPN, after VPN reconnect; proxy, registry, CA, DNS, debugger, random ports | NOT RUN | — |
| 9 | Filesystem: WSL ext4 qualified; `/mnt/c` performance and path caveats recorded | NOT RUN | — |
| 10 | CI parity: same assembly on the Linux GitHub Actions runner with equivalent SDK, packages, images, and cleanup | NOT RUN | — |

## 1. CLI reference result (WSL)

```bash
cd ~/src/<repository>/samples/wsl-testcontainers
./scripts/preflight.sh --require-localstack
dotnet test WslTestcontainers.slnx --no-restore --logger trx --results-directory TestResults/cli
```

| Measure | Value |
|---|---|
| Preflight result | PENDING |
| Tests discovered / passed / failed / skipped | PENDING (expected discovered: 18) |
| Duration | PENDING |
| Docker Engine / API | PENDING |
| Peak WSL memory (`free -m` sampled during the run) | PENDING |
| Disk growth (`df -h /` and `docker system df` before and after) | PENDING |
| Remaining `org.testcontainers` resources after 30 s | PENDING (expected: none) |
| Listener on 2375/2376 (`ss -lntp`) | PENDING (expected: none) |

## 2. Visual Studio

Select the `WSL-Ubuntu-24.04` environment in Test Explorer.

| Check | Result |
|---|---|
| Visual Studio build | PENDING |
| Test count matches CLI | PENDING |
| `ProcessIdentityTests` pass (Linux testhost) | PENDING |
| Debug `SqlServerTests.Round_trips_a_dependency_record`: breakpoints before start, during query, during disposal | PENDING |
| Debug `LocalStackServiceTests.S3_round_trips_utf8_content`: breakpoints before start, during request, during disposal | PENDING |
| Repeat after Visual Studio restart | PENDING |
| Repeat after `wsl --shutdown` | PENDING |

Any failure blocks promotion of that Visual Studio build.

## 3. VS Code

Open with `code .` from WSL. Confirm the status bar shows `WSL: Ubuntu-24.04`.

| Check | Result |
|---|---|
| VS Code / C# Dev Kit / C# versions | PENDING |
| C# Dev Kit runs in the `WSL: Ubuntu-24.04` extension host | PENDING |
| Test count and results match CLI | PENDING |
| Debug the same SQL Server and LocalStack tests | PENDING |

## 4. Lifecycle, VPN, and abnormal cleanup

| Scenario | Off VPN | On VPN | After reconnect | After Windows restart |
|---|---|---|---|---|
| Full suite passes | PENDING | PENDING | PENDING | PENDING |
| Force-kill testhost after all three dependencies start; Ryuk removes containers, networks, volumes | PENDING | PENDING | PENDING | PENDING |

Forced-kill procedure: start the full suite, and wait until `docker ps` shows the SQL Server, PostgreSQL, and LocalStack containers. Then run `pkill -9 -f testhost`. Wait 30 seconds and confirm `docker ps -a --filter label=org.testcontainers`, `docker network ls --filter label=org.testcontainers`, and `docker volume ls --filter label=org.testcontainers` are empty.

## 5. Filesystem

| Location | Duration | Failures / path issues |
|---|---|---|
| `~/src` (WSL ext4, supported baseline) | PENDING | PENDING |
| `/mnt/c/...` (comparison only) | PENDING | PENDING |

## 6. GitHub Actions comparison

| Measure | WSL CLI | GitHub Actions |
|---|---|---|
| Run link | — | PENDING |
| Runner image version | — | PENDING |
| Tests discovered | PENDING | PENDING |
| SDK | PENDING | PENDING |
| Docker Engine / API | PENDING | PENDING |

Differences must be explained. An unexplained missing test fails qualification.

## Residual risks

- **Visual Studio remote testing is an experimental preview.** Qualification covers only the exact build recorded in the manifest.
- **LocalStack fidelity gaps** are listed in [`localstack-fidelity.md`](localstack-fidelity.md). EventBridge target resource policies and IAM beyond single-action identity policies still need real-AWS contract tests.
- **CI container logs are usually empty on failure.** Ryuk removes containers before the diagnostics step runs. The LocalStack SQS/EventBridge timeout path embeds sanitized container logs in the TRX failure message, but SQL Server and PostgreSQL readiness failures do not. Rerun locally to capture them if needed.
- **The GitHub Actions runner is pinned to `ubuntu-24.04`**, not `ubuntu-latest` as the plan stated, to keep parity with the Ubuntu 24.04 identity test.
- **The test runner uses the VSTest path** (`UseMicrosoftTestingPlatformRunner=false`, `IsTestingPlatformApplication=false`). The .NET 10 SDK rejects `dotnet test` over VSTest for projects on Microsoft.Testing.Platform. xunit.v3 4.x is on that platform by default. Moving to the platform later changes CLI filter and logger syntax, and it needs re-qualification.

## Pre-qualification development evidence

The following was observed on 2026-10-01 during implementation on a developer MacBook (macOS, Arm64, Docker Desktop context `desktop-linux`, Engine 29.7.2). It is **not** qualification evidence for any gate. It only shows the sample is ready for the managed-laptop run.

| Observation | Result |
|---|---|
| `dotnet restore --locked-mode` and `dotnet build` (0 warnings) on Linux in `mcr.microsoft.com/dotnet/sdk:10.0.401-noble` | Pass |
| Tests discovered on Linux | 18 |
| `ProcessIdentityTests` (Linux, Ubuntu 24.04, no Docker overrides) in the Ubuntu 24.04 SDK container | Pass |
| `ProcessIdentityTests.Testhost_runs_on_linux` on macOS | Fails as designed (unsupported host) |
| `SqlServerTests` and `PostgreSqlTests` against real containers from the macOS host | Pass |
| Testcontainers from inside the SDK container through the Docker Desktop socket | Fails: `ResourceReaperException` (Ryuk initialization cancelled). Probable cause: container-to-host networking through the Docker Desktop socket; not investigated further, and not representative of WSL or GitHub-hosted runners until proven there. |
| LocalStack tests without `LOCALSTACK_AUTH_TOKEN` | Fail with the prerequisite message (no silent skip) |
| LocalStack tests with a token | Not run — no token available |
| `scripts/preflight.sh` emits all 11 identifiers and never prints the token (macOS and Ubuntu 24.04 container) | Pass |
