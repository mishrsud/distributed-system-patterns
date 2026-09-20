# WSL Testcontainers Developer Environment Design

**Status:** Proposed for implementation  
**Date:** 20 September 2026  
**Immediate scope:** Phase 1 and Phase 2  
**Platform owner:** Platform Engineering, guided by Principal Engineering

## Purpose

Provide a repeatable .NET 10 development environment on centrally managed Windows 11 enterprise laptops. Engineers use Visual Studio or Visual Studio Code on Windows while builds, tests, debugging, Testcontainers, and native Docker Engine run inside the managed Ubuntu 24.04 WSL 2 distribution.

The environment must make in-IDE discovery, execution, and debugging of WSL-hosted integration tests a first-class workflow. It must also provide working SQL Server, PostgreSQL, and LocalStack samples and an equivalent Linux GitHub Actions workflow.

## Confirmed constraints

- WSL 2 and Ubuntu 24.04 are already available to engineers through central IT.
- Engineers do not have Windows administrator rights or Microsoft Store access.
- Engineers have controlled `sudo` access inside Ubuntu and may install or update APT packages.
- Docker Desktop is prohibited. Native, rootful Docker Engine is already installed inside WSL.
- Developers may join the WSL `docker` group, with the root-equivalent privilege risk explicitly accepted.
- Required vendor container registries are permitted through the corporate proxy.
- Corporate resources require VPN access. TLS inspection, endpoint controls, and corporate CA trust remain in force.
- Production runs Linux containers in AWS ECS.
- GitHub Actions is the CI platform; GitHub-hosted Linux runners are preferred, with ephemeral self-hosted Linux runners where corporate connectivity requires them.
- .NET solutions use central package management.
- Source may be stored on WSL ext4 or mounted Windows storage, but WSL ext4 is the recommended baseline.

## Scope

### Phase 1: qualified workstation path

- Qualify Ubuntu 24.04, .NET 10, native Docker Engine, Testcontainers, Visual Studio, and VS Code as one versioned developer-platform combination.
- Support `dotnet test` inside WSL as the reference workflow.
- Support test discovery, execution, and debugging through Visual Studio Test Explorer with the test host inside WSL.
- Support the same IDE workflow through a VS Code WSL window and C# Dev Kit.
- Verify proxy, corporate CA, VPN, Docker daemon, registry pulls, random published ports, Ryuk, and cleanup behaviour.
- Publish a platform manifest, preflight checks, qualification suite, and initial operational runbook.

### Phase 2: working examples and CI parity

- Provide a standalone .NET 10 solution demonstrating Testcontainers with SQL Server, PostgreSQL, and LocalStack.
- Exercise S3, SQS, EventBridge, Secrets Manager, and representative IAM behaviour in LocalStack.
- Document every LocalStack fidelity limitation discovered by the sample.
- Add equivalent Linux GitHub Actions execution.
- Capture logs and environment diagnostics as safe test artifacts.

### Deferred

The following are explicitly outside the immediate implementation plan:

- Mirroring every dependency image into ECR.
- Centralized Testcontainers image substitution for ECR.
- Organization-wide rollout rings and update automation beyond the pilot manifest and runbook.
- Large-scale performance tuning beyond the initial qualification measurements.
- A final LocalStack Ultimate or Enterprise purchasing decision.
- Broad real-AWS contract-test infrastructure beyond documenting where it is required.

## Architecture decision

Use a Windows IDE front end with the actual .NET test process inside WSL:

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

This design preserves Linux process, path, networking, and CI parity. It does not expose the root-equivalent Docker API across the Windows-to-WSL boundary.

### Rejected alternatives

**Windows test host to WSL Docker:** Rejected as the supported baseline. It requires a network-exposed daemon, mutual TLS and client-key management, cross-boundary host resolution, and Linux-daemon bind-mount semantics. An unauthenticated listener such as `tcp://localhost:2375` is prohibited.

**Third-party Windows named-pipe relay:** Rejected. No Microsoft, Docker, or Testcontainers primary documentation establishes such a relay as a supported native-Docker-on-WSL integration.

**WSL command line without IDE test support:** Rejected because in-IDE WSL discovery and debugging are required.

## IDE workflows

### Visual Studio

Commit `testEnvironments.json` at the sample solution root:

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

The distribution name must exactly match `wsl.exe --list --verbose` on the managed workstation. Engineers select this environment in Test Explorer. Tests are then discovered, run, and debugged by Linux `dotnet` inside WSL.

Microsoft's current documentation labels Visual Studio remote testing an experimental preview. First-class organizational support therefore means version-pinning and qualifying the exact Visual Studio build, test platform, adapters, .NET SDK, and Ubuntu image. A failed qualification blocks promotion of that toolchain version; it does not justify exposing Docker to a Windows test process.

### Visual Studio Code

Install VS Code on Windows. Open the repository through the Microsoft WSL extension, preferably by running `code .` from the WSL checkout. Install C# Dev Kit and the C# extension in the `WSL: Ubuntu-24.04` extension host.

The status bar must identify the WSL distribution. Opening the same folder in a normal local Windows window is not a supported way to run the sample because it moves the extension and test host out of the Linux environment.

### Reference workflow

`dotnet test` from a WSL shell is the reference result. Visual Studio, VS Code, and CI must discover the same tests and produce equivalent results.

## WSL and Docker configuration

- Run Docker as a systemd-managed service.
- Use the native Unix socket at `/var/run/docker.sock`.
- Leave these variables unset in the baseline:
  - `DOCKER_HOST`
  - `DOCKER_CONTEXT`
  - `DOCKER_TLS`
  - `DOCKER_TLS_VERIFY`
  - `DOCKER_CERT_PATH`
  - `TESTCONTAINERS_HOST_OVERRIDE`
  - `TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE`
  - `TESTCONTAINERS_RYUK_DISABLED`
- Keep Ryuk enabled and privileged under the approved rootful-Docker model.
- Start with WSL NAT and default localhost forwarding. Mirrored networking is not required for the supported data path.
- Start with 24-32 GB of WSL memory and 8-12 logical processors, then record measurements from the heaviest sample run.
- Configure Docker log rotation and document safe stale-resource cleanup.
- Configure the Docker daemon proxy independently of the Windows IDE and shell.
- Install the corporate CA in Ubuntu and any required Docker registry trust directories. A managed Windows certificate alone is not evidence that Linux and containers trust it.
- Do not configure a Docker TCP listener.

## Filesystem policy

The recommended checkout is `~/src/<repository>` on WSL ext4. `/mnt/c` remains compatible but is not the performance baseline.

Avoid host bind mounts in portable test fixtures. Docker evaluates the source path on the Linux daemon host, so Windows drive and UNC paths are not valid native-Docker sources. Prefer Testcontainers resource-copy APIs, generated resources, and named volumes. Any retained bind mount must be qualified separately from WSL ext4, `/mnt/c`, and CI.

## Repository artifacts

The Phase 2 sample will live at:

```text
samples/wsl-testcontainers/
|-- WslTestcontainers.slnx
|-- global.json
|-- Directory.Packages.props
|-- testEnvironments.json
|-- .vscode/
|-- src/
|   `-- SampleApplication/
`-- tests/
    |-- EnvironmentQualification.Tests/
    `-- Integration.Tests/
```

Responsibilities:

- `global.json`: pin the approved .NET 10 SDK feature band and roll-forward policy.
- `Directory.Packages.props`: centrally pin Testcontainers, dependency modules, the test framework, adapters, and AWS SDK packages.
- `testEnvironments.json`: declare the Visual Studio WSL test environment.
- `.vscode/extensions.json`: recommend WSL, C# Dev Kit, and C# extensions.
- `EnvironmentQualification.Tests`: prove Linux identity, SDK/runtime, socket access, Docker operation, and cleanup prerequisites.
- `Integration.Tests`: demonstrate dependency fixtures and application-level assertions.

No repository file may contain proxy credentials, AWS credentials, Docker client keys, ECR tokens, or LocalStack authentication tokens.

## Testcontainers design

- Pin Testcontainers and module versions through central package management.
- Keep dependency image references in one image catalog so ECR substitution can be added later without rewriting fixtures.
- Use one container per test fixture or collection.
- Use unique databases, schemas, buckets, queues, buses, rules, secrets, and other logical resources per test.
- Use random host-port bindings and retrieve mapped ports from Testcontainers.
- Use explicit readiness checks and asynchronous disposal.
- Bound test parallelism based on measured laptop and runner capacity.
- Keep Ryuk enabled for normal and abnormal cleanup.
- Provide a diagnostic mode that retains relevant logs, not a default mode that leaks containers.

## Working sample behaviours

### SQL Server

Start the pinned Linux SQL Server image with the Testcontainers SQL Server module. Create a database object, insert data, query it, and assert the result. Do not depend solely on the container process being started; use module readiness plus a real query.

### PostgreSQL

Start the pinned PostgreSQL image through its Testcontainers module. Perform an equivalent create, insert, query, and assertion workflow.

### LocalStack

Start one approved LocalStack image per fixture. Obtain `LOCALSTACK_AUTH_TOKEN` from the WSL user environment or CI secret store. Never commit it. Calls to the local AWS endpoints use dummy access-key material.

The sample covers:

- S3 bucket and object operations.
- SQS queue send and receive operations.
- EventBridge bus, rule, and target behaviour relevant to the sample.
- Secrets Manager create and retrieve operations.
- Representative IAM allow and deny behaviour.

Every required AWS behaviour is classified as:

1. Demonstrably equivalent and suitable for routine LocalStack testing.
2. Partially emulated, with the limitation stated beside the test.
3. Insufficiently equivalent, requiring a future ephemeral real-AWS contract test.

LocalStack Base is the initial commercial assumption. Ultimate requires evidence such as advanced IAM testing, live AWS resource replication, a required unavailable service, or priority support. Enterprise requires features such as offline delivery, SSO/SCIM, centralized enterprise deployment, or enterprise support.

## Error handling and diagnostics

Preflight and qualification failures use distinct categories:

- **Environment:** wrong operating system, distribution, SDK, Docker state, socket permissions, or IDE execution context.
- **Network/trust:** proxy, DNS, registry, VPN, Hyper-V firewall, or corporate CA.
- **Supply chain:** unavailable image, digest mismatch, policy rejection, or LocalStack entitlement.
- **Dependency:** readiness timeout, resource pressure, port mapping, or unsupported emulated behaviour.
- **Cleanup:** failed Ryuk connection or leaked container, network, or volume.

The preflight command must fail before the expensive integration suite when a prerequisite is missing. Test artifacts may include container logs, `dotnet --info`, sanitized Docker information, testhost OS/path identity, and readiness diagnostics. They must not include authentication tokens, proxy credentials, AWS credentials, client keys, or sensitive environment dumps.

## Qualification gates

The platform combination is supported only after all gates pass on the managed image:

1. **Identity:** both IDEs and WSL CLI prove Linux, Ubuntu 24.04, the pinned .NET 10 SDK/runtime, and a Linux source path.
2. **Discovery:** Visual Studio, VS Code, WSL CLI, and GitHub Actions discover the same test set.
3. **Debugging:** both IDEs hit breakpoints before and after container startup, during a real dependency operation, and during disposal.
4. **Docker boundary:** the test process uses `/var/run/docker.sock`; no Docker TCP listener or Windows named-pipe relay exists.
5. **Dependencies:** SQL Server, PostgreSQL, and LocalStack start concurrently and perform representative operations using random ports.
6. **Cleanup:** Ryuk cleans resources after success, assertion failure, cancellation, and forced testhost termination.
7. **Lifecycle:** repeat qualification after IDE restart, `wsl --shutdown`, Windows restart, and clean package restore.
8. **Network/security:** repeat off VPN, on VPN, and after VPN reconnection; validate proxy, registry, CA, DNS, debugger, and random-port behaviour.
9. **Filesystem:** qualify WSL ext4; record `/mnt/c` performance and path caveats rather than promising identical behaviour.
10. **CI parity:** execute the same sample assembly on the selected Linux GitHub Actions runner with equivalent SDK, packages, images, isolation, and cleanup.

Qualification fails if port 2375 is listening, Ryuk is disabled on the persistent workstation, a test silently runs through Windows `dotnet`, or a required test is silently skipped.

## CI design

Use an Ubuntu GitHub-hosted runner initially. Use an ephemeral self-hosted Linux runner only when corporate access makes it necessary. CI uses its local Docker Unix socket and does not copy WSL-specific settings or a workstation `DOCKER_HOST` configuration.

CI consumes the same `global.json`, central package versions, image catalog, test filters, fixture parallelism, and LocalStack licence assumptions as the workstation. Secrets come from GitHub Actions or the approved enterprise secret mechanism.

## Ownership and operational support

Platform Engineering owns the complete compatibility baseline and initial diagnosis. Principal Engineering guides technical policy. Endpoint management, networking, security, AWS, and application teams are escalation owners for their respective layers.

The Phase 1 manifest records:

- Windows and WSL versions.
- Ubuntu image revision.
- .NET SDK feature band.
- Visual Studio and VS Code versions.
- C# Dev Kit, test platform, and adapter versions.
- Docker Engine and API range.
- Testcontainers and AWS SDK versions.
- Container image tags or digests.
- LocalStack plan and image.
- Proxy, CA, and registry configuration revision.

The initial runbook covers preflight, Docker startup, package and image restore, IDE environment selection, diagnostic collection, VPN recovery, WSL restart, stale-resource cleanup, LocalStack token handling, and escalation routing.

## Acceptance criteria

Phase 1 and Phase 2 are complete when:

- A newly provisioned engineer can follow the runbook without Windows administrator access.
- The preflight identifies missing or incorrect prerequisites with actionable messages.
- The complete sample passes from WSL CLI, Visual Studio Test Explorer, VS Code Test Explorer, and Linux GitHub Actions.
- Tests can be debugged from both IDEs while the Linux testhost controls native Docker through the Unix socket.
- The environment contains no insecure Docker listener or committed credential.
- SQL Server, PostgreSQL, and LocalStack tests demonstrate real operations and reliable cleanup.
- LocalStack limitations for the five named services are recorded from executable evidence.
- The platform manifest and support boundaries are published with the sample.

## Research basis

- [Environment research](../../research/windows-wsl2-dotnet10-testcontainers-development-environment.md)
- [Windows IDE and WSL Testcontainers boundary](../../research/windows-ide-wsl-testcontainers-boundary.md)
- [Microsoft: Visual Studio remote testing](https://learn.microsoft.com/en-us/visualstudio/test/remote-testing?view=visualstudio)
- [Microsoft: debug .NET applications in WSL](https://learn.microsoft.com/en-us/visualstudio/debugger/debug-dotnet-core-in-wsl-2?view=visualstudio)
- [VS Code: developing in WSL](https://code.visualstudio.com/docs/remote/wsl)
- [Testcontainers for .NET](https://dotnet.testcontainers.org/)
- [Docker Engine on Ubuntu](https://docs.docker.com/engine/install/ubuntu/)
- [LocalStack plans](https://docs.localstack.cloud/aws/licensing/)

