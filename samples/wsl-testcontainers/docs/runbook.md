# WSL Testcontainers runbook

Developer and support operations for the WSL Testcontainers sample on managed Windows 11 laptops. The supported path runs `dotnet`, the testhost, the debugger, and Docker Engine inside the managed `Ubuntu-24.04` WSL distribution. Windows only hosts the IDE UI.

Exact supported versions are in [`platform-manifest.md`](platform-manifest.md).

## Prohibited

These are never supported, even temporarily for troubleshooting:

- **A Docker TCP listener.** Do not expose the daemon on `tcp://…:2375` or `2376`, or add `-H tcp://` to `dockerd`. Preflight fails if either port is listening.
- **Disabling Ryuk.** Never set `TESTCONTAINERS_RYUK_DISABLED`. Ryuk removes containers, networks, and volumes after abnormal exits.
- **Committed secrets.** Never commit `LOCALSTACK_AUTH_TOKEN`, proxy credentials, AWS credentials, Docker client keys, or ECR tokens. That includes `.env` files, `launchSettings.json`, `.runsettings`, and VS Code settings.
- **Windows `dotnet`.** Never run this sample with Windows `dotnet.exe` or the Windows-local test host. `ProcessIdentityTests.Testhost_runs_on_linux` fails by design if you do.
- **Docker Desktop.** It is prohibited on managed laptops. Use native Docker Engine inside WSL.

## First-time setup

All commands run in a WSL `Ubuntu-24.04` shell unless stated otherwise. You do not need Windows administrator rights.

1. **Confirm the distribution name.** In Windows PowerShell:

   ```powershell
   wsl.exe --list --verbose
   ```

   The name must be exactly `Ubuntu-24.04`, matching `testEnvironments.json`. If it differs, escalate to Endpoint Management. Do not edit `testEnvironments.json` locally.

2. **Join the `docker` group.** This grants root-equivalent access to the daemon. The risk is accepted for the pilot.

   ```bash
   sudo usermod -aG docker "$USER"
   ```

   Then run `wsl.exe --shutdown` from Windows and reopen the shell.

3. **Start Docker.** Docker runs as a systemd service:

   ```bash
   sudo systemctl enable --now docker
   systemctl is-active docker
   ```

4. **Provision the LocalStack token.** Obtain it from the approved secret store. Add it to `~/.profile` (not to any repository file):

   ```bash
   printf '\nexport LOCALSTACK_AUTH_TOKEN=%s\n' '<token from secret store>' >> ~/.profile
   chmod 600 ~/.profile
   ```

   Open a new login shell. Never echo the variable, paste it into chat or tickets, or include it in screenshots.

5. **Check out on WSL ext4.** `/mnt/c` works but is slower and is not the supported baseline.

   ```bash
   mkdir -p ~/src && cd ~/src
   git clone <repository-url>
   cd <repository>/samples/wsl-testcontainers
   ```

6. **Run preflight.**

   ```bash
   ./scripts/preflight.sh --require-localstack
   ```

   Every line must be `PASS` or `WARN`. Fix each `FAIL` using its message before continuing.

## Daily workflow

### WSL CLI (reference result)

```bash
cd ~/src/<repository>/samples/wsl-testcontainers
./scripts/preflight.sh --require-localstack
dotnet restore WslTestcontainers.slnx --locked-mode
dotnet build WslTestcontainers.slnx --no-restore
dotnet test WslTestcontainers.slnx --no-build --logger "console;verbosity=normal"
```

To run one area:

```bash
dotnet test tests/Integration.Tests --filter "FullyQualifiedName~SqlServerTests"
dotnet test tests/Integration.Tests --filter "FullyQualifiedName~LocalStack"
```

### VS Code

1. From the WSL shell in the sample directory, run `code .`.
2. Confirm the status bar shows `WSL: Ubuntu-24.04`. If it does not, close the window and repeat step 1. A local Windows window is unsupported.
3. Install the recommended extensions when prompted. C# Dev Kit and C# must be installed **in WSL: Ubuntu-24.04**, not locally.
4. Use the Testing view to run and debug tests.

### Visual Studio 2026

1. Open `\\wsl.localhost\Ubuntu-24.04\home\<user>\src\<repository>\samples\wsl-testcontainers\WslTestcontainers.slnx`.
2. In Test Explorer, select the **WSL-Ubuntu-24.04** environment from the target-environment drop-down.
3. Run or debug tests. The testhost and debugger run inside WSL.

Visual Studio remote testing is an experimental preview. Use only the Visual Studio build recorded in the manifest.

## Proxy and CA validation

The Docker daemon does not read the shell's proxy variables. It uses its own systemd drop-in.

```bash
systemctl show docker --property=Environment | sed 's/=[^ ]*@/=***@/g'   # masks proxy credentials
docker info --format '{{if .HTTPSProxy}}daemon proxy configured{{else}}no daemon proxy{{end}}'
ls /usr/local/share/ca-certificates/
curl -sS -o /dev/null -w '%{http_code}\n' https://mcr.microsoft.com/v2/
docker pull alpine:3.23.3
```

A TLS error from `curl` or `docker pull` means the corporate CA is not trusted inside Ubuntu. A trusted Windows certificate is not enough. Escalate to Networking or Security with the error text (not the proxy URL).

## VPN reconnect

After connecting, disconnecting, or reconnecting the VPN:

1. Run `./scripts/preflight.sh`.
2. If `registry` fails or DNS resolution fails inside WSL, run `wsl.exe --shutdown` from Windows, reopen the shell, and repeat.
3. If it still fails, collect logs (see below) and escalate to Networking.

## WSL restart recovery

Use when Docker hangs, the socket disappears, or the IDE loses the WSL connection:

```powershell
wsl.exe --shutdown
```

Reopen the Ubuntu shell, then run:

```bash
systemctl is-active docker || sudo systemctl start docker
./scripts/preflight.sh
```

Restart Visual Studio or VS Code after `wsl --shutdown`; their WSL connections do not survive it.

## Stale resource inspection and cleanup

Ryuk normally removes everything a test run created, including after a crash, within about 10 seconds. Inspect before removing anything:

```bash
docker ps -a --filter label=org.testcontainers --format '{{.ID}} {{.Image}} {{.Status}} {{.Label "org.testcontainers.session-id"}}'
docker network ls --filter label=org.testcontainers
docker volume ls --filter label=org.testcontainers
```

Only remove resources labelled `org.testcontainers`, and only when no test run is active:

```bash
docker ps -aq --filter label=org.testcontainers | xargs -r docker rm -f
docker network ls -q --filter label=org.testcontainers | xargs -r docker network rm
docker volume ls -q --filter label=org.testcontainers | xargs -r docker volume rm
```

Do not run `docker system prune -a` or `docker volume prune` without a filter; they remove resources that belong to other work. Repeated leaks after normal runs are a Cleanup failure; escalate to Platform Engineering.

## Log collection

Collect only these. Review each file before attaching it to a ticket.

```bash
mkdir -p ~/wsl-testcontainers-diagnostics && cd ~/wsl-testcontainers-diagnostics
~/src/<repository>/samples/wsl-testcontainers/scripts/preflight.sh --require-localstack > preflight.txt 2>&1 || true
dotnet --info > dotnet-info.txt 2>&1
docker version > docker-version.txt 2>&1
docker info --format '{{.ServerVersion}} {{.OperatingSystem}} {{.KernelVersion}} {{.NCPU}} {{.MemTotal}}' > docker-info.txt 2>&1
cat /etc/os-release > os-release.txt
journalctl -u docker --since "1 hour ago" --no-pager > docker-journal.txt 2>&1
```

For failing tests, rerun with a TRX report and attach it:

```bash
dotnet test WslTestcontainers.slnx --logger trx --results-directory ~/wsl-testcontainers-diagnostics/TestResults
```

Never attach `env`, `printenv`, `set`, `~/.profile`, `/etc/docker/daemon.json` with credentials, or `systemctl show docker` output without masking.

## Escalation routing

| Symptom (preflight identifier or category) | First owner | Escalate to |
|---|---|---|
| `os`, `ubuntu`, wrong distribution name, WSL will not start | Platform Engineering | Endpoint Management |
| `dotnet`, `systemd`, `docker-service`, `docker-socket`, `docker-cli` | Platform Engineering | Endpoint Management (image defects) |
| `docker-tcp` (listener found) | Platform Engineering | Security (treat as a security incident) |
| `proxy`, `registry`, TLS or CA errors, VPN/DNS | Platform Engineering | Networking, Security |
| Image unavailable, digest mismatch, LocalStack entitlement | Platform Engineering | Security (supply chain), LocalStack licence owner |
| `localstack-token` | Engineer (provision token) | Platform Engineering |
| Readiness timeouts, resource pressure | Platform Engineering | Application team for workload-specific tuning |
| LocalStack behaves differently from AWS | Platform Engineering | AWS team; record in `localstack-fidelity.md` |
| Visual Studio / VS Code cannot discover or debug in WSL | Platform Engineering | Endpoint Management (IDE build), Microsoft support |
