#!/usr/bin/env bash
# Fast WSL / Docker / SDK prerequisite check for the WSL Testcontainers sample.
#
# Emits one "PASS|WARN|FAIL <identifier> <message>" line per check and exits
# nonzero when any check fails. It never prints environment variable values,
# because proxy URLs and tokens may contain credentials.
#
# Usage: scripts/preflight.sh [--format text] [--require-localstack]
set -eu

sample_root="$(cd "$(dirname "$0")/.." && pwd)"
require_localstack=0
failures=0

usage() {
    echo "Usage: $0 [--format text] [--require-localstack]" >&2
    exit 2
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --format)
            [ "$#" -ge 2 ] && [ "$2" = "text" ] || usage
            shift 2
            ;;
        --require-localstack)
            require_localstack=1
            shift
            ;;
        *)
            usage
            ;;
    esac
done

report() {
    echo "$1 $2 $3"
    if [ "$1" = "FAIL" ]; then
        failures=$((failures + 1))
    fi
}

is_set() {
    eval "[ -n \"\${$1:-}\" ]"
}

check_os() {
    if [ "$(uname -s)" != "Linux" ]; then
        report FAIL os "Testhost OS is $(uname -s); run this sample from Linux inside WSL, not Windows or macOS."
    elif [ -n "${WSL_DISTRO_NAME:-}" ] || grep -qi microsoft /proc/version 2>/dev/null; then
        report PASS os "Linux inside WSL."
    else
        report PASS os "Linux (not WSL; expected for CI runners)."
    fi
}

check_ubuntu() {
    if [ -r /etc/os-release ] && grep -q '^ID=ubuntu$' /etc/os-release && grep -q '^VERSION_ID="24.04"$' /etc/os-release; then
        report PASS ubuntu "Ubuntu 24.04."
    else
        report FAIL ubuntu "Distribution is not Ubuntu 24.04; use the managed Ubuntu-24.04 WSL distribution."
    fi
}

check_dotnet() {
    local dotnet_path version required
    required="$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' "$sample_root/global.json" | head -n 1)"
    if ! dotnet_path="$(command -v dotnet)"; then
        report FAIL dotnet "dotnet is not on PATH; install .NET SDK $required inside WSL."
        return
    fi
    case "$dotnet_path" in
        /mnt/*|*.exe)
            report FAIL dotnet "dotnet resolves to a Windows binary ($dotnet_path); install the Linux SDK inside WSL."
            return
            ;;
    esac
    if ! version="$(cd "$sample_root" && dotnet --version 2>/dev/null)"; then
        report FAIL dotnet "No installed SDK satisfies global.json ($required, latestPatch)."
        return
    fi
    # latestPatch roll-forward: same major.minor.feature band, patch >= pinned.
    if [ "${version%??}" = "${required%??}" ] && [ "${version##*.}" -ge "${required##*.}" ] 2>/dev/null; then
        report PASS dotnet "SDK $version satisfies global.json $required."
    else
        report FAIL dotnet "SDK $version does not satisfy global.json $required."
    fi
}

check_systemd() {
    if [ "$(ps -p 1 -o comm= 2>/dev/null | tr -d ' ')" = "systemd" ]; then
        report PASS systemd "systemd is PID 1."
    else
        report FAIL systemd "systemd is not PID 1; enable it with [boot] systemd=true in /etc/wsl.conf, then run 'wsl --shutdown'."
    fi
}

check_docker_service() {
    if command -v systemctl >/dev/null 2>&1 && systemctl is-active --quiet docker 2>/dev/null; then
        report PASS docker-service "docker.service is active."
    else
        report FAIL docker-service "docker.service is not active; run 'sudo systemctl enable --now docker'."
    fi
}

check_docker_socket() {
    if [ ! -S /var/run/docker.sock ]; then
        report FAIL docker-socket "/var/run/docker.sock does not exist."
    elif [ ! -r /var/run/docker.sock ] || [ ! -w /var/run/docker.sock ]; then
        report FAIL docker-socket "No access to /var/run/docker.sock; join the docker group ('sudo usermod -aG docker \$USER') and start a new login shell."
    else
        report PASS docker-socket "/var/run/docker.sock is accessible."
    fi
}

check_docker_cli() {
    local name overrides="" server
    for name in DOCKER_HOST DOCKER_CONTEXT DOCKER_TLS DOCKER_TLS_VERIFY DOCKER_CERT_PATH \
        TESTCONTAINERS_HOST_OVERRIDE TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE TESTCONTAINERS_RYUK_DISABLED; do
        if is_set "$name"; then
            overrides="$overrides $name"
        fi
    done
    if [ -n "$overrides" ]; then
        report FAIL docker-cli "Unset Docker endpoint overrides:$overrides."
    elif ! command -v docker >/dev/null 2>&1; then
        report FAIL docker-cli "docker CLI is not on PATH."
    elif ! server="$(docker version --format '{{.Server.Version}} (API {{.Server.APIVersion}})' 2>/dev/null)"; then
        report FAIL docker-cli "docker CLI cannot reach the daemon."
    else
        report PASS docker-cli "Docker Engine $server."
    fi
}

check_docker_tcp() {
    if ! command -v ss >/dev/null 2>&1; then
        report WARN docker-tcp "ss is unavailable; cannot confirm that ports 2375/2376 are closed."
    elif ss -lnt 2>/dev/null | awk '{print $4}' | grep -Eq ':(2375|2376)$'; then
        report FAIL docker-tcp "A listener exists on port 2375 or 2376; remove the Docker TCP listener."
    else
        report PASS docker-tcp "No listener on ports 2375 or 2376."
    fi
}

check_proxy() {
    local shell_proxy=0 daemon_proxy=""
    if is_set HTTPS_PROXY || is_set https_proxy || is_set HTTP_PROXY || is_set http_proxy; then
        shell_proxy=1
    fi
    if command -v docker >/dev/null 2>&1; then
        daemon_proxy="$(docker info --format '{{.HTTPSProxy}}{{.HTTPProxy}}' 2>/dev/null || true)"
    fi
    if [ "$shell_proxy" -eq 1 ] && [ -z "$daemon_proxy" ]; then
        report WARN proxy "Shell proxy is set but the Docker daemon has no proxy; configure a docker.service drop-in if pulls fail."
    elif [ -n "$daemon_proxy" ]; then
        report PASS proxy "Docker daemon proxy is configured."
    else
        report PASS proxy "No proxy configured (direct connection)."
    fi
}

check_registry() {
    local image
    image="$(sed -n 's/.*Qualification = "\([^"]*\)".*/\1/p' "$sample_root/tests/EnvironmentQualification.Tests/ImageCatalog.cs")"
    if [ -z "$image" ]; then
        report FAIL registry "Could not read the qualification image from ImageCatalog.cs."
    elif command -v docker >/dev/null 2>&1 && docker pull --quiet "$image" >/dev/null 2>&1; then
        report PASS registry "Pulled $image."
    else
        report FAIL registry "Could not pull $image; check registry access, proxy, and corporate CA trust."
    fi
}

check_localstack_token() {
    if is_set LOCALSTACK_AUTH_TOKEN; then
        report PASS localstack-token "LOCALSTACK_AUTH_TOKEN is set."
    elif [ "$require_localstack" -eq 1 ]; then
        report FAIL localstack-token "LOCALSTACK_AUTH_TOKEN is not set; export it from your WSL profile or CI secret store."
    else
        report WARN localstack-token "LOCALSTACK_AUTH_TOKEN is not set; LocalStack tests will fail."
    fi
}

check_os
check_ubuntu
check_dotnet
check_systemd
check_docker_service
check_docker_socket
check_docker_cli
check_docker_tcp
check_proxy
check_registry
check_localstack_token

if [ "$failures" -gt 0 ]; then
    exit 1
fi
