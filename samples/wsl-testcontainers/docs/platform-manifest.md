# Pilot platform manifest

This manifest is the single compatibility record for the WSL Testcontainers pilot. A platform combination is supported only when every value below is concrete and every qualification gate in [`qualification-results.md`](qualification-results.md) passes.

Rows marked **PENDING** must be captured from the managed enterprise laptop before promotion. Do not replace them with values from a developer's personal machine.

- **Status:** Draft — not yet qualified
- **Owner:** Platform Engineering (technical policy: Principal Engineering)
- **Image digests verified:** 2026-10-01 by Sudhanshu Mishra, using `docker buildx imagetools inspect` against the public vendor registries

## Workstation

| Component | Value | How to capture |
|---|---|---|
| Windows 11 build | PENDING | `winver` or `cmd /c ver` |
| WSL version | PENDING | `wsl.exe --version` |
| WSL distribution name | `Ubuntu-24.04` (must match exactly) — PENDING confirmation | `wsl.exe --list --verbose` |
| Ubuntu image revision | PENDING | `lsb_release -d` and the central IT image identifier |
| WSL memory / processors | Target 24–32 GB / 8–12 logical processors — PENDING actual | `%UserProfile%\.wslconfig`, `free -g`, `nproc` |
| Visual Studio 2026 build | PENDING | Help → About Microsoft Visual Studio |
| VS Code version | PENDING | `code --version` |
| WSL extension version | PENDING | Extensions view (`ms-vscode-remote.remote-wsl`) |
| C# Dev Kit version | PENDING | Extensions view in the `WSL: Ubuntu-24.04` host (`ms-dotnettools.csdevkit`) |
| C# extension version | PENDING | Extensions view in the `WSL: Ubuntu-24.04` host (`ms-dotnettools.csharp`) |
| Docker Engine / API version | PENDING | `docker version --format '{{.Server.Version}} {{.Server.APIVersion}}'` |
| Proxy configuration revision | PENDING | Central IT change record for `/etc/systemd/system/docker.service.d/` |
| Corporate CA bundle revision | PENDING | Central IT change record for `/usr/local/share/ca-certificates/` |
| Registry configuration revision | PENDING | Central IT change record for `/etc/docker/daemon.json` |

## .NET toolchain

| Component | Version | Source |
|---|---|---|
| .NET SDK | 10.0.401 (`rollForward: latestPatch`, no prerelease) | `global.json` |
| C# language | 14.0 | `Directory.Build.props` |
| Test runner path | VSTest through `xunit.runner.visualstudio` (`UseMicrosoftTestingPlatformRunner=false`, `IsTestingPlatformApplication=false`) | `Directory.Build.props` |
| Microsoft.NET.Test.Sdk | 18.10.0 | `Directory.Packages.props` |
| xunit.v3 | 4.0.1 | `Directory.Packages.props` |
| xunit.runner.visualstudio | 4.0.0 | `Directory.Packages.props` |
| Testcontainers (core, MsSql, PostgreSql, LocalStack) | 4.15.0 | `Directory.Packages.props` |
| Microsoft.Data.SqlClient | 7.1.0 | `Directory.Packages.props` |
| Npgsql | 10.0.3 | `Directory.Packages.props` |
| AWSSDK.S3 | 4.0.103.3 | `Directory.Packages.props` |
| AWSSDK.SQS | 4.0.100.14 | `Directory.Packages.props` |
| AWSSDK.EventBridge | 4.0.100.13 | `Directory.Packages.props` |
| AWSSDK.SecretsManager | 4.0.100.12 | `Directory.Packages.props` |
| AWSSDK.IdentityManagement | 4.0.103.6 | `Directory.Packages.props` |
| AWSSDK.SecurityToken | 4.0.101 | `Directory.Packages.props` |

Transitive versions are locked in each project's `packages.lock.json`; CI restores with `--locked-mode`.

## Container images

Tags stay readable in code for the pilot. Phase 3 moves resolution to ECR; at that point, pin by digest.

| Purpose | Tag | Index digest | `linux/amd64` digest |
|---|---|---|---|
| SQL Server | `mcr.microsoft.com/mssql/server:2025-CU8-GDR1-ubuntu-24.04` | Single-platform manifest (no index) | `sha256:b036b61e953e6e660f04514fc3f703b995a9cdda569cf96d07d3f751240f615a` |
| PostgreSQL | `postgres:18.6-alpine3.23` | `sha256:885cf05d376c7cf27afef02073e6bdac3841252537f16e244fd1c1e6a7c99fb1` | `sha256:3928680cee9028902891672c0a6ce84de58ed3eb1b987588e7c9424d8f08d21d` |
| LocalStack | `localstack/localstack:2026.8.2` | `sha256:3fe5b51caec82b966a34485b5b43efd68075ff8161f9b0a34091fad9abb0ff82` | `sha256:7e0d1fffbfe20cbc33cb8058f2d56f542778981f7c9fe6b113ec1f90293ab822` |
| Qualification | `alpine:3.23.3` | `sha256:25109184c71bdad752c8312a8623239686a9a2071e8825f20acb8f2198c3f659` | `sha256:59855d3dceb3ae53991193bd03301e082b2a7faa56a514b03527ae0ec2ce3a95` |
| Ryuk (pulled by Testcontainers 4.15.0) | `testcontainers/ryuk:0.14.0` | `sha256:7c1a8a9a47c780ed0f983770a662f80deb115d95cce3e2daa3d12115b8cd28f0` | `sha256:f0456560ea5b4acdbed0da0efc33b5f9dd6bc1e59f2337106826dcb5b0b0e981` |

The SQL Server image is published for `linux/amd64` only. It will not run natively on Arm64 hosts.

## LocalStack

| Item | Value |
|---|---|
| Plan | LocalStack Base (initial commercial assumption) |
| Image | `localstack/localstack:2026.8.2` |
| Token source | `LOCALSTACK_AUTH_TOKEN` from the WSL user profile or the `LOCALSTACK_AUTH_TOKEN` GitHub Actions secret; never committed |
| IAM enforcement | `ENFORCE_IAM=1` |
| Fidelity record | [`localstack-fidelity.md`](localstack-fidelity.md) |

## CI

| Item | Value |
|---|---|
| Workflow | `.github/workflows/wsl-testcontainers-sample.yml` |
| Runner | `ubuntu-24.04` (GitHub-hosted). Deliberate deviation from the plan's `ubuntu-latest`: `ProcessIdentityTests` asserts Ubuntu 24.04, so label drift would break parity. Record the resolved runner image version from each qualification run. |
| Docker endpoint | Runner's local `/var/run/docker.sock`; no `DOCKER_HOST` |

## Change control

Change a pinned value only through a compatibility update:

1. Update the pin in its source file and this manifest in the same commit.
2. Re-run `scripts/preflight.sh --require-localstack` and the full test suite from WSL, and the CI workflow.
3. Re-run any gate in `qualification-results.md` that the change could affect, and record the result.
