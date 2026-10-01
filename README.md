# Distributed System Patterns

Small, runnable, heavily documented samples of patterns that show up when you build distributed systems on
**.NET 10**, **SQL Server** and **AWS** (with **LocalStack** standing in for AWS locally).

Each sample is deliberately small and dependency-light so that every moving part is visible. The emphasis is on
*why* the design is the way it is, what it guarantees, and where it stops guaranteeing anything.

## What is in this repository

| Area | What it is | Start here |
| --- | --- | --- |
| **Transactional outbox** | Reliable event publication: write the business row and the event in one SQL Server transaction, then relay the event to Amazon EventBridge with at-least-once delivery. | [`transactional-outbox/README.md`](transactional-outbox/README.md) |
| **WSL Testcontainers** | A qualified developer environment (Windows 11, WSL 2, Ubuntu 24.04, native Docker Engine) and a working .NET 10 Testcontainers sample for SQL Server, PostgreSQL and LocalStack. | [`samples/wsl-testcontainers/README.md`](samples/wsl-testcontainers/README.md) |

The two areas are related: the outbox sample's integration tests need Docker, SQL Server and LocalStack, and the
WSL Testcontainers sample is the environment and testing approach for running exactly that kind of test on a
managed enterprise laptop and in CI.

## Repository layout

```text
.
|-- transactional-outbox/
|   |-- README.md                       Overview of both outbox samples (start here)
|   |-- gatherly/                       .NET 6 baseline: naive outbox with Quartz + MediatR
|   |-- dotnet10-eventbridge/           .NET 10 sample: leased SQL Server outbox -> EventBridge -> SQS
|   |   `-- README.md                   The full step-by-step tutorial
|   `-- docs/research/                  Primary-source research behind the design
|-- samples/
|   `-- wsl-testcontainers/             .NET 10 Testcontainers sample + WSL environment qualification
|       |-- README.md
|       |-- docs/                       Runbook, platform manifest, qualification results, LocalStack fidelity
|       `-- scripts/preflight.sh        Fast prerequisite check
|-- docs/
|   |-- research/                       Environment research (WSL 2, Windows IDE boundary)
|   `-- superpowers/
|       |-- specs/                      Design documents
|       `-- plans/                      Implementation plans
`-- .github/workflows/                  CI for the WSL Testcontainers sample
```

## Quick start

Pick the sample you want. Each has its own prerequisites; the common ones are Docker and the .NET 10 SDK.

**Run the transactional outbox end to end** (any OS with Docker Compose v2; about five minutes):

```bash
cd transactional-outbox/dotnet10-eventbridge
cp .env.example .env
docker compose up -d --wait
dotnet tool restore
dotnet tool run dotnet-ef database update --project src/TransactionalOutbox.Infrastructure --startup-project src/TransactionalOutbox.Web
dotnet run --project src/TransactionalOutbox.Web
```

Then place an order and read the event from SQS. The
[tutorial](transactional-outbox/dotnet10-eventbridge/README.md#place-and-retrieve-an-order) has the commands.

**Run the WSL Testcontainers sample** (inside the managed `Ubuntu-24.04` WSL distribution, or any Linux with
Docker):

```bash
cd samples/wsl-testcontainers
./scripts/preflight.sh --require-localstack
dotnet test WslTestcontainers.slnx
```

The LocalStack tests need a `LOCALSTACK_AUTH_TOKEN` in your environment. See the
[sample README](samples/wsl-testcontainers/README.md) for setup.

## Prerequisites at a glance

| Tool | Needed for | Notes |
| --- | --- | --- |
| .NET 10 SDK | Everything except `gatherly` | Both samples pin it in `global.json`. `gatherly` targets .NET 6. |
| Docker Engine with Compose v2 | Both samples | The WSL sample requires native Docker Engine in WSL; Docker Desktop is prohibited in that environment. |
| `curl` | Outbox tutorial | For calling the API |
| `LOCALSTACK_AUTH_TOKEN` | WSL sample LocalStack tests | Never commit it. The outbox sample's default LocalStack image runs without one. |

## Design documents and research

Each sample was designed before it was built. These documents explain the decisions and the evidence behind them.

| Document | Subject |
| --- | --- |
| [Transactional outbox design](docs/superpowers/specs/2026-09-20-transactional-outbox-design.md) | Goals, non-goals, claim algorithm, delivery semantics, verification strategy |
| [Transactional outbox plan](docs/superpowers/plans/2026-09-20-transactional-outbox.md) | Task-by-task implementation plan |
| [Outbox research](transactional-outbox/docs/research/dotnet-transactional-outbox.md) | Primary-source research: EF Core interceptors, SQL Server claiming, EventBridge `PutEvents` |
| [WSL Testcontainers design](docs/superpowers/specs/2026-09-20-wsl-testcontainers-developer-environment-design.md) | Architecture decision, rejected alternatives, qualification gates, CI design |
| [WSL Testcontainers plan](docs/superpowers/plans/2026-09-20-wsl-testcontainers-developer-environment.md) | Task-by-task implementation plan |
| [WSL 2 + .NET 10 + Testcontainers research](docs/research/windows-wsl2-dotnet10-testcontainers-development-environment.md) | Distribution choice, Docker in WSL, networking, filesystem |
| [Windows IDE and WSL boundary research](docs/research/windows-ide-wsl-testcontainers-boundary.md) | Why the test host runs in WSL rather than on Windows |

## Continuous integration

[`.github/workflows/wsl-testcontainers-sample.yml`](.github/workflows/wsl-testcontainers-sample.yml) runs the WSL
Testcontainers sample on a GitHub-hosted `ubuntu-24.04` runner whenever `samples/wsl-testcontainers/**` or the
workflow itself changes. It restores in locked mode and uses the runner's local Docker socket. The outbox sample
has no CI workflow yet; run its tests locally as described in its tutorial.

## Status and honest caveats

- **These are teaching samples, not production templates.** APIs are unauthenticated, credentials are disposable
  development values, and there is no metrics, tracing or alerting.
- **The outbox gives at-least-once delivery, not exactly-once.** Consumers must deduplicate. The tutorial
  demonstrates a duplicate delivery on purpose.
- **LocalStack is not AWS.** Passing tests against it does not prove parity with real AWS behaviour. The WSL
  sample records every known gap in a [fidelity record](samples/wsl-testcontainers/docs/localstack-fidelity.md).
- **The WSL environment is a pilot.** Its managed-laptop qualification matrix has not been executed yet, so the
  [qualification results](samples/wsl-testcontainers/docs/qualification-results.md) read "NOT QUALIFIED". The
  sample builds and its tests are ready to run; the sign-off is outstanding.
- **SQL Server runs under unsupported emulation on Apple silicon.** Microsoft supports its Linux container on
  x86-64 only. Fine for a local tutorial, not for conclusions about production.

## Contributing

- Keep samples small. Prefer the framework's built-in capability over a new package, and explain any dependency
  you do add.
- Pin versions: SDK in `global.json`, packages through central package management, container images in the
  image catalog or `compose.yaml`.
- Never commit secrets: no `.env` files (use `.env.example`), tokens, proxy credentials or AWS credentials.
- Update the relevant README, runbook or manifest in the same change as the code it describes.
- Run the relevant sample's tests before opening a pull request.
