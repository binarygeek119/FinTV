#!/usr/bin/env bash
set -euo pipefail

# Bundled with FinTV at: plugins/FinTV_<version>/scripts/install-docker-cli-jellyfin.sh
#
# Preferred: run from the Docker HOST (where Jellyfin runs):
#   bash /path/to/config/plugins/FinTV_<version>/scripts/install-docker-cli-jellyfin.sh jellyfin
#
# If you are already inside the Jellyfin container as root:
#   bash /config/plugins/FinTV_<version>/scripts/install-docker-cli-jellyfin.sh --inside-container
#
# Before either path, mount the host Docker socket into Jellyfin:
#   -v /var/run/docker.sock:/var/run/docker.sock

CONTAINER="${JELLYFIN_CONTAINER:-jellyfin}"
INSTALL_DIR="${DOCKER_INSTALL_DIR:-/usr/local/bin}"
DOCKER_CLI_VERSION="${DOCKER_CLI_VERSION:-29.5.2}"
INSIDE_CONTAINER=0

usage() {
  cat <<EOF
Usage:
  $(basename "$0") [container-name]          Install from Docker host via docker exec
  $(basename "$0") --inside-container       Install inside the current container (run as root)

Environment:
  JELLYFIN_CONTAINER   Jellyfin container name (default: jellyfin)
  DOCKER_INSTALL_DIR   Where to place static CLI (default: /usr/local/bin)
  DOCKER_CLI_VERSION   Static CLI version (default: 29.5.2)
EOF
}

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
  usage
  exit 0
fi

if [[ "${1:-}" == "--inside-container" ]]; then
  INSIDE_CONTAINER=1
elif [[ -n "${1:-}" ]]; then
  CONTAINER="$1"
fi

require_root() {
  if [[ "$(id -u)" -ne 0 ]]; then
    echo "ERROR: run as root (inside container: docker exec -u root -it <name> bash)" >&2
    exit 1
  fi
}

require_docker_sock() {
  if ! test -S /var/run/docker.sock; then
    cat >&2 <<EOF
ERROR: /var/run/docker.sock is not mounted.

Add the Docker socket to Jellyfin, then restart:
  volumes:
    - /var/run/docker.sock:/var/run/docker.sock
EOF
    exit 1
  fi
}

install_with_apt() {
  if ! command -v apt-get >/dev/null 2>&1; then
    return 1
  fi

  echo "Installing Docker CLI with apt (docker.io)..."
  export DEBIAN_FRONTEND=noninteractive
  apt-get update -qq
  apt-get install -y -qq --no-install-recommends ca-certificates docker.io
  return 0
}

install_with_apk() {
  if ! command -v apk >/dev/null 2>&1; then
    return 1
  fi

  echo "Installing Docker CLI with apk (docker-cli)..."
  apk add --no-cache docker-cli ca-certificates
  return 0
}

install_static_binary() {
  local arch="$1"
  local docker_arch

  case "$arch" in
    x86_64|amd64) docker_arch="x86_64" ;;
    aarch64|arm64) docker_arch="aarch64" ;;
    armv7l|armv6l) docker_arch="armhf" ;;
    *)
      echo "ERROR: unsupported architecture: $arch" >&2
      return 1
      ;;
  esac

  local tarball="docker-${DOCKER_CLI_VERSION}.tgz"
  local url="https://download.docker.com/linux/static/stable/${docker_arch}/${tarball}"

  if ! command -v curl >/dev/null 2>&1; then
    if command -v apt-get >/dev/null 2>&1; then
      export DEBIAN_FRONTEND=noninteractive
      apt-get update -qq
      apt-get install -y -qq --no-install-recommends curl ca-certificates
    elif command -v apk >/dev/null 2>&1; then
      apk add --no-cache curl ca-certificates
    else
      echo "ERROR: curl is required for static Docker CLI install." >&2
      return 1
    fi
  fi

  echo "Installing Docker CLI ${DOCKER_CLI_VERSION} (${docker_arch}) to ${INSTALL_DIR}/docker..."
  local tmp
  tmp="$(mktemp -d)"
  if ! curl -fsSL "$url" -o "$tmp/$tarball"; then
    rm -rf "$tmp"
    echo "Static Docker CLI ${DOCKER_CLI_VERSION} is not available for ${docker_arch}." >&2
    return 1
  fi

  tar -xzf "$tmp/$tarball" -C "$tmp"
  mkdir -p "$INSTALL_DIR"
  if command -v install >/dev/null 2>&1; then
    install -m 0755 "$tmp/docker/docker" "${INSTALL_DIR}/docker"
  else
    cp "$tmp/docker/docker" "${INSTALL_DIR}/docker"
    chmod 0755 "${INSTALL_DIR}/docker"
  fi
  rm -rf "$tmp"
  return 0
}

install_docker_cli() {
  if command -v docker >/dev/null 2>&1; then
    if docker version >/dev/null 2>&1; then
      echo "Docker CLI already works."
      return 0
    fi
    echo "Docker binary exists but cannot reach the daemon; checking socket access..."
  fi

  if install_static_binary "$(uname -m)"; then
    return 0
  fi

  if install_with_apt; then
    :
  elif install_with_apk; then
    :
  else
    echo "ERROR: could not install Docker CLI." >&2
    exit 1
  fi
}

print_socket_group_hint() {
  local sock_gid="${1:-}"
  if [[ -z "$sock_gid" ]]; then
    sock_gid="$(stat -c '%g' /var/run/docker.sock 2>/dev/null || stat -f '%g' /var/run/docker.sock)"
  fi

  cat <<EOF

Docker CLI is installed but the Jellyfin user cannot access /var/run/docker.sock.

Add the host docker group GID to your Jellyfin compose/service and restart Jellyfin:
  group_add:
    - ${sock_gid}

Or run Jellyfin as root (not recommended).

To find the GID on the host:
  stat -c '%g' /var/run/docker.sock
EOF
}

verify_docker_cli() {
  if docker version >/dev/null 2>&1; then
    docker version --format 'Client: {{.Client.Version}} · Server: {{.Server.Version}}'
    echo
    echo "Done. FinTV Weather Docker buttons should work after restarting Jellyfin if needed."
    return 0
  fi

  print_socket_group_hint
  return 1
}

run_inside_container() {
  require_root
  require_docker_sock
  install_docker_cli
  verify_docker_cli
}

run_from_host() {
  if ! command -v docker >/dev/null 2>&1; then
    echo "ERROR: docker is not available on the host. Install Docker Engine first." >&2
    exit 1
  fi

  if ! docker ps --format '{{.Names}}' | grep -Fxq "$CONTAINER"; then
    echo "ERROR: container '$CONTAINER' is not running." >&2
    echo "Set JELLYFIN_CONTAINER or pass the container name as the first argument." >&2
    echo "Running containers:" >&2
    docker ps --format '  {{.Names}}' >&2 || true
    exit 1
  fi

  if ! docker exec "$CONTAINER" test -S /var/run/docker.sock 2>/dev/null; then
    cat >&2 <<EOF
ERROR: /var/run/docker.sock is not mounted in '$CONTAINER'.

Add the Docker socket to your Jellyfin compose/service, then restart Jellyfin:
  volumes:
    - /var/run/docker.sock:/var/run/docker.sock
EOF
    exit 1
  fi

  echo "Installing Docker CLI inside '${CONTAINER}'..."
  docker cp "$0" "${CONTAINER}:/tmp/install-docker-cli-jellyfin.sh"
  docker exec -u root "$CONTAINER" bash /tmp/install-docker-cli-jellyfin.sh --inside-container
  docker exec "$CONTAINER" rm -f /tmp/install-docker-cli-jellyfin.sh

  echo
  echo "Verifying from host..."
  if ! docker exec "$CONTAINER" docker version --format 'Client: {{.Client.Version}} · Server: {{.Server.Version}}'; then
    sock_gid="$(docker exec "$CONTAINER" stat -c '%g' /var/run/docker.sock)"
    print_socket_group_hint "$sock_gid" >&2
    exit 1
  fi

  echo
  echo "Done. FinTV Weather Docker buttons should work after restarting Jellyfin if needed."
}

if [[ "$INSIDE_CONTAINER" -eq 1 ]]; then
  run_inside_container
else
  run_from_host
fi
