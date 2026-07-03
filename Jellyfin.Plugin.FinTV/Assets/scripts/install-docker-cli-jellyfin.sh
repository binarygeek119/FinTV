#!/usr/bin/env bash
set -euo pipefail

# Bundled with FinTV at: plugins/FinTV_<version>/scripts/install-docker-cli-jellyfin.sh
# Run from the Jellyfin HOST (where Docker Engine runs), not inside Jellyfin:
#   bash /config/plugins/FinTV_<version>/scripts/install-docker-cli-jellyfin.sh jellyfin
#   JELLYFIN_CONTAINER=jellyfin bash .../install-docker-cli-jellyfin.sh
#
# Before running, mount the host Docker socket into Jellyfin, for example:
#   -v /var/run/docker.sock:/var/run/docker.sock

CONTAINER="${1:-${JELLYFIN_CONTAINER:-jellyfin}}"
DOCKER_CLI_VERSION="${DOCKER_CLI_VERSION:-27.4.1}"
INSTALL_DIR="${DOCKER_INSTALL_DIR:-/usr/local/bin}"

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

Example docker run flag:
  -v /var/run/docker.sock:/var/run/docker.sock
EOF
  exit 1
fi

ARCH="$(docker exec "$CONTAINER" uname -m)"
case "$ARCH" in
  x86_64|amd64) DOCKER_ARCH="x86_64" ;;
  aarch64|arm64) DOCKER_ARCH="aarch64" ;;
  armv7l|armv6l) DOCKER_ARCH="armhf" ;;
  *)
    echo "ERROR: unsupported container architecture: $ARCH" >&2
    exit 1
    ;;
esac

TARBALL="docker-${DOCKER_CLI_VERSION}.tgz"
URL="https://download.docker.com/linux/static/stable/${DOCKER_ARCH}/${TARBALL}"

echo "Installing Docker CLI ${DOCKER_CLI_VERSION} (${DOCKER_ARCH}) into '${CONTAINER}:${INSTALL_DIR}/docker'..."

docker exec -u root "$CONTAINER" sh -eu -c "
  if ! command -v curl >/dev/null 2>&1; then
    if command -v apt-get >/dev/null 2>&1; then
      apt-get update -qq
      apt-get install -y -qq curl ca-certificates
    elif command -v apk >/dev/null 2>&1; then
      apk add --no-cache curl ca-certificates
    else
      echo 'ERROR: curl is required inside the container but could not be installed.' >&2
      exit 1
    fi
  fi

  tmp=\$(mktemp -d)
  trap 'rm -rf \"\$tmp\"' EXIT
  curl -fsSL '${URL}' -o \"\$tmp/${TARBALL}\"
  tar -xzf \"\$tmp/${TARBALL}\" -C \"\$tmp\"
  install -m 0755 \"\$tmp/docker/docker\" '${INSTALL_DIR}/docker'
"

echo
echo "Verifying Docker CLI from inside '${CONTAINER}'..."
docker exec "$CONTAINER" docker version --format 'Client: {{.Client.Version}} · Server: {{.Server.Version}}'

cat <<EOF

Done.

FinTV Weather Docker buttons should work after a Jellyfin restart if needed.
If Jellyfin runs as a non-root user, ensure it can access /var/run/docker.sock
(for example, match the socket group inside the container or run Jellyfin as root).
EOF
