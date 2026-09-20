# Windows IDE, WSL test host, and native Docker Engine

_Research snapshot: 20 September 2026. This addendum narrows the workstation design to Visual Studio or VS Code running on Windows, while the actual .NET 10 test process and Docker Engine run inside the managed Ubuntu 24.04 WSL 2 distribution. Docker Desktop is out of scope and prohibited._

## Decision

The supported design is **a Windows IDE front end with a Linux test host in WSL**, not a Windows `vstest` process controlling a Docker daemon across the WSL network boundary:

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
native Docker Engine in the same Ubuntu-24.04 WSL distro
                |
                +-- Ryuk
                +-- SQL Server / PostgreSQL / LocalStack
```

This meets the corrected requirement: engineers run, discover, and debug tests from the Windows IDE, but the process loading the tests is Linux `dotnet` inside WSL. It preserves the ordinary Docker Unix socket, Linux paths, container-port reachability, and Linux CI behaviour without publishing a root-equivalent Docker API endpoint.

No daemon TCP listener, TLS client key, WSL IP discovery, named-pipe bridge, `DOCKER_HOST`, `TESTCONTAINERS_HOST_OVERRIDE`, or `TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE` is required in this baseline.

## First-class IDE paths

### Visual Studio Test Explorer

Put `testenvironments.json` at the solution root and commit it:

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

The `wslDistribution` value must exactly match `wsl.exe --list --verbose` on the managed image; do not assume the unversioned `Ubuntu` alias. In Test Explorer, select `WSL-Ubuntu-24.04` as the active environment. Visual Studio then discovers, runs, and debugs the tests in that environment. Microsoft also states specifically that WSL unit-test debugging requires `testEnvironments.json`. ([Visual Studio remote testing](https://learn.microsoft.com/en-us/visualstudio/test/remote-testing?view=visualstudio), [Visual Studio: debug .NET in WSL](https://learn.microsoft.com/en-us/visualstudio/debugger/debug-dotnet-core-in-wsl-2?view=visualstudio#remote-debug-unit-tests))

Do **not** use a `type: "docker"` test environment. Microsoft's local-container test-environment path requires Docker Desktop; the WSL environment is the applicable integration for native Docker Engine inside the distro. ([Visual Studio remote testing: local container and WSL connections](https://learn.microsoft.com/en-us/visualstudio/test/remote-testing?view=visualstudio#local-wsl2-connections))

There is an important product-support caveat: Microsoft's current page still labels Visual Studio remote testing an **experimental preview**, says that most target provisioning is the user's responsibility, and advises monitoring `Output > Tests` for lost connections. First-class organizational support is therefore achievable only as a **qualified and version-pinned enterprise workflow**, not by claiming that Microsoft has promoted the feature to a stable support tier. The qualification matrix must include the organization's exact Visual Studio 2026 build, .NET test platform, adapter versions, and Ubuntu image. ([Visual Studio remote testing requirements](https://learn.microsoft.com/en-us/visualstudio/test/remote-testing?view=visualstudio#requirements))

The WSL distro needs the pinned .NET 10 SDK and test runtime, Docker service, and any debugger prerequisites delivered by the platform baseline. The reference command remains `dotnet test` inside WSL; Test Explorer must produce the same discovered test count and result set.

### VS Code

Install VS Code on Windows, connect using the Microsoft WSL extension, and open the repository in a WSL window—for example, start from `~/src/<repo>` with `code .`, or use **WSL: Reopen Folder in WSL**. VS Code installs its server and workspace extensions inside WSL; commands, debugging, terminals, and workspace extensions then execute in the Linux environment. ([VS Code: developing in WSL](https://code.visualstudio.com/docs/remote/wsl))

Install C# Dev Kit in the **WSL: Ubuntu-24.04** extension host. Its Test Explorer supports test discovery plus run/debug operations. Combined with the WSL extension's execution model, the .NET test host is the WSL process and reaches `/var/run/docker.sock` locally. ([VS Code C# testing](https://code.visualstudio.com/docs/csharp/testing), [VS Code WSL extension placement and debugging](https://code.visualstudio.com/docs/remote/wsl#_managing-extensions))

Do not open the folder in a normal local VS Code window and expect the Windows C# extension host to discover WSL Docker. The status bar must show the named WSL distro, and a diagnostic test must assert Linux at runtime.

## Testcontainers configuration in the supported lane

Testcontainers 4.15.0 is the current release at this snapshot and directly targets `net10.0`. Pin it through central package management rather than using a floating range. ([NuGet: Testcontainers 4.15.0](https://www.nuget.org/packages/Testcontainers/4.15.0))

Use the defaults first:

```text
DOCKER_HOST                                  unset
DOCKER_CONTEXT                               unset
DOCKER_TLS                                   unset
DOCKER_TLS_VERIFY                            unset
DOCKER_CERT_PATH                             unset
TESTCONTAINERS_HOST_OVERRIDE                 unset
TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE        unset
TESTCONTAINERS_RYUK_DISABLED                 unset (therefore false)
TESTCONTAINERS_RYUK_CONTAINER_PRIVILEGED     unset (therefore true)
```

On Linux, Testcontainers' normal endpoint is `unix:///var/run/docker.sock`. `TESTCONTAINERS_HOST_OVERRIDE` means the host that exposes published container ports; it is unnecessary when testhost and Docker are in the same WSL environment. `TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE` changes the daemon-socket path mounted into Ryuk and defaults to `/var/run/docker.sock`; set it only if Platform Engineering deliberately moves the rootful daemon socket. ([Testcontainers custom configuration](https://dotnet.testcontainers.org/custom_configuration/))

Leave Ryuk enabled. Testcontainers starts it with a random published port, mounts the daemon socket into it, and maintains a TCP connection so leaked resources are removed when the test process disappears. The released implementation falls back to `/var/run/docker.sock` for Ryuk unless a Unix endpoint or explicit socket override says otherwise. ([Testcontainers resource reaper](https://dotnet.testcontainers.org/api/resource_reaper/), [4.15.0 `ResourceReaper`](https://github.com/testcontainers/testcontainers-dotnet/blob/4.15.0/src/Testcontainers/Containers/ResourceReaper.cs), [4.15.0 `UnixSocketMount`](https://github.com/testcontainers/testcontainers-dotnet/blob/4.15.0/src/Testcontainers/Configurations/Volumes/UnixSocketMount.cs))

Use random host-port bindings and construct endpoints from `container.Hostname` plus `container.GetMappedPublicPort(...)`. Do not hard-code `localhost` or fixed ports in test code. ([Testcontainers best practices](https://dotnet.testcontainers.org/api/best_practices/))

## Source and bind-mount semantics

The recommended checkout is `~/src/<repo>` in WSL ext4. VS Code operates there directly. Visual Studio may present the solution through Windows/WSL integration, but the WSL test host must resolve files as Linux paths.

Avoid host bind mounts in portable integration-test fixtures. Docker evaluates a bind-mount source on the **daemon host**, not on the client; a Windows path such as `C:\src\...` or `\\wsl$\Ubuntu-24.04\...` is not a valid native-Docker source path. Docker explicitly states that a remote daemon cannot bind-mount client files. Prefer Testcontainers resource-copy APIs, generated content, named volumes, or a Linux daemon-host path. ([Docker bind-mount constraints](https://docs.docker.com/engine/storage/bind-mounts/#considerations-and-constraints), [Testcontainers resource mapping](https://dotnet.testcontainers.org/api/create_docker_container/#copying-directories-or-files-to-the-container))

If a fixture genuinely requires a bind mount, prove separately that the path observed by the Visual Studio WSL testhost is the same Linux path visible to `dockerd`. Do not implement a generic string translation from `C:\...` or UNC paths to `/mnt/c/...`; that mapping does not cover WSL ext4, Visual Studio's projected test root, permissions, symlinks, or CI.

## Networking, firewall, proxy, and certificates

For the supported lane, Docker control traffic stays on the Unix socket, and test-to-container traffic stays inside WSL through Docker's published random ports. Default WSL NAT is sufficient; mirrored networking is not required.

WSL NAT supports Windows access to Linux services through Windows `localhost`, while the WSL VM address can change after restart. Mirrored mode also supports bidirectional IPv4 localhost and may improve VPN compatibility, but it changes the network architecture and brings Hyper-V firewall policy into scope. Keep NAT plus the default `localhostForwarding=true` unless the enterprise VPN pilot demonstrates a concrete reason to adopt mirrored mode. ([Microsoft WSL networking](https://learn.microsoft.com/en-us/windows/wsl/networking), [Microsoft WSL configuration](https://learn.microsoft.com/en-us/windows/wsl/wsl-config#configuration-settings-for-wslconfig))

Windows 11 22H2+ Hyper-V firewall can filter WSL traffic. Its WSL `LoopbackEnabled` setting is enabled by default to allow host/WSL loopback, while inbound rules can be centrally managed. Qualification must prove both IDE debugger/test communication and every Testcontainers published-port connection under the applied Intune/GPO/CSP policy; do not solve failures by allowing all WSL inbound traffic. ([Microsoft Hyper-V firewall](https://learn.microsoft.com/en-us/windows/security/operating-system-security/network-security/windows-firewall/hyper-v-firewall))

The corporate proxy already permits the required vendor image registries. That does not remove the need to configure the **Docker daemon** proxy in WSL, because daemon image pulls are separate from the Windows IDE and test process. Include registry and localhost entries in `NO_PROXY`, and place the corporate interception CA in Ubuntu's trust store plus Docker's registry-specific trust where required. Containers that call inspected internal HTTPS endpoints need the CA in their own trust store. ([Docker daemon proxy](https://docs.docker.com/engine/daemon/proxy/), [Docker CA certificates](https://docs.docker.com/engine/network/ca-certs/), [Docker registry certificates](https://docs.docker.com/engine/security/certificates/))

## Rejected nonbaseline: Windows testhost to WSL Docker

A Windows `dotnet`/`vstest` process controlling native Docker in WSL is technically possible through a network listener, but it is not the supported workstation architecture.

- Current Docker.DotNet.Enhanced 4.3.3, used by Testcontainers 4.15.0, supports `npipe`, `unix`, `tcp`, `http`, and `https`, and explicitly throws for `ssh`. Consequently, a Docker CLI SSH context is **not** a Testcontainers transport even though Docker CLI supports it. ([Docker.DotNet 4.3.3 transport selection](https://github.com/testcontainers/Docker.DotNet/blob/1e4015a84fa48cbcfe9002ecc4e2cf14177edc2d/src/Docker.DotNet/DockerClientBuilder.cs#L287-L303), [Docker SSH contexts](https://docs.docker.com/engine/security/protect-access/#use-ssh-to-protect-the-docker-daemon-socket))
- A Windows named pipe is Docker's default for a Windows daemon, not for native Linux `dockerd` in WSL. No Microsoft, Docker, or Testcontainers primary documentation defines a supported named-pipe relay from Windows to WSL's Unix socket. A third-party relay would become a privileged, separately supported security component and is rejected.
- `tcp://localhost:2375` is unauthenticated, root-equivalent Docker API access. Docker warns that unsecured remote access can grant root access and says remote access without TLS is not recommended. It is rejected even if bound to loopback. ([Docker remote-access warning](https://docs.docker.com/engine/daemon/remote-access/), [Docker daemon socket security](https://docs.docker.com/engine/security/protect-access/))
- If an exceptional Windows-testhost proof is ever authorized, the only candidate in scope is mutual TLS on WSL loopback port 2376, retaining the Unix socket for Ryuk. The Windows test process would need `DOCKER_HOST=tcp://localhost:2376`, `DOCKER_TLS_VERIFY=1`, and `DOCKER_CERT_PATH=<Windows directory containing ca.pem, cert.pem, key.pem>`. The server certificate must cover `localhost`/`127.0.0.1`, the client key must be guarded like a root password, and localhost must bypass the corporate proxy. Testcontainers' current mTLS provider reads those PEM files and handles the Windows certificate conversion. ([Docker mutual TLS](https://docs.docker.com/engine/security/protect-access/#use-tls-https-to-protect-the-docker-daemon-socket), [Testcontainers 4.15.0 mTLS provider](https://github.com/testcontainers/testcontainers-dotnet/blob/4.15.0/src/Testcontainers/Builders/MTlsEndpointAuthenticationProvider.cs))
- In that exceptional lane, `TESTCONTAINERS_HOST_OVERRIDE` is normally unnecessary because a TCP endpoint's host becomes the published-port hostname; using `localhost` avoids the changing WSL NAT address. `TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE` remains unnecessary while the daemon socket is `/var/run/docker.sock`. Bind mounts from Windows remain invalid because their source is evaluated on the Linux daemon host.

Mutual TLS reduces network exposure but does not restore Linux process or filesystem parity. It should therefore be a diagnostic experiment, not the route used to satisfy in-IDE test debugging.

## CI parity

Run the authoritative workflow on a standard Ubuntu GitHub-hosted runner or an ephemeral self-hosted Linux runner with local Docker. Testcontainers documents that GitHub-hosted runners require no extra Docker endpoint configuration, while Windows agents use the Windows engine and cannot run the Linux-container lane. ([Testcontainers CI guidance](https://dotnet.testcontainers.org/cicd/#github-actions))

CI should use the same `global.json`, centrally pinned Testcontainers/module versions, image references, test filters, fixture parallelism, and Ryuk policy. It should **not** copy workstation-specific WSL settings, Visual Studio projection details, or a TLS `DOCKER_HOST`; Linux CI should use its local Unix socket.

## Required proof-of-concept and qualification tests

The architecture is not enterprise-supported until all of these pass on the managed image:

1. **Identity:** from Visual Studio Test Explorer and VS Code Test Explorer, a test records `OperatingSystem.IsLinux() == true`, the expected Ubuntu release, the pinned .NET 10 SDK/runtime, and a path inside the selected WSL distro.
2. **IDE discovery:** Visual Studio WSL, VS Code WSL, `dotnet test` in WSL, and GitHub Actions discover the same tests. Repeat after IDE restart, `wsl --shutdown`, Windows restart, and repository clean/restore.
3. **IDE debugging:** set breakpoints before and after a container starts; step through fixture initialization, a database request, and fixture disposal in both IDEs. Verify cancellation and test-abort behaviour.
4. **Docker boundary:** confirm the WSL test process connects to `unix:///var/run/docker.sock`; confirm `ss -lntp` shows no Docker TCP listener and Windows has no Docker named-pipe relay.
5. **Dependencies:** concurrently start SQL Server, PostgreSQL, and the approved LocalStack image with random ports; execute a representative operation against each; run at the agreed bounded parallelism.
6. **Ryuk:** verify its image pull, privileged start, socket mount, testhost-to-Ryuk connection, normal cleanup, and cleanup after force-killing the testhost/IDE. Repeat after `wsl --shutdown` and confirm the operational stale-resource cleanup procedure.
7. **Paths:** prove resource-copy APIs from both IDEs. If any bind mount remains, log the testhost source path, validate the same path from WSL and the daemon, and repeat from `~/src`, `/mnt/c`, and CI; document unsupported combinations rather than silently translating them.
8. **Network/security:** test while off VPN, on VPN, and after VPN reconnect; validate registry pulls through the daemon proxy, corporate CA trust in Ubuntu and representative containers, DNS, random published ports, debugger connectivity, and applied Hyper-V firewall policy.
9. **No insecure fallback:** fail qualification if port 2375 is listening, `TESTCONTAINERS_RYUK_DISABLED=true` is set on a persistent workstation, or tests silently switch to Windows `dotnet`/a Windows container engine.
10. **CI parity:** execute the same smoke-test assembly and assert Linux, Docker Engine/API compatibility, images, readiness behaviour, concurrent fixture isolation, and forced-termination cleanup on the chosen GitHub Actions runner.

## Residual risk

The Docker/Testcontainers side is conventional once testhost runs inside WSL. The primary remaining risk is Visual Studio's remote-test feature still being documented as experimental preview. Platform Engineering should pin and regression-test the exact Visual Studio build and keep VS Code WSL plus WSL `dotnet test` as fully qualified alternate entry points. If Visual Studio remote Test Explorer fails an upgrade gate, that release cannot be promoted merely by exposing Docker to a Windows testhost.
