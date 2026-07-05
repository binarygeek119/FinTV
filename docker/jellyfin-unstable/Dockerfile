# FinTV-ready Jellyfin unstable: Docker CLI, yt-dlp, and sidecar control for WeatherStar/Playwright.
# Playwright Chromium runs in fintv-playwright-chromium (separate container), not in this image.
# Rebuilt automatically when jellyfin/jellyfin:unstable changes (see .github/workflows).

ARG JELLYFIN_TAG=unstable
ARG DOCKER_CLI_VERSION=29.5.2

FROM jellyfin/jellyfin:${JELLYFIN_TAG}

ARG DOCKER_CLI_VERSION
ARG TARGETARCH

USER root

COPY scripts/yt-dlp-version.txt /tmp/yt-dlp-version.txt

# Match host Docker Engine API (static CLI, not distro docker.io package).
# Install yt-dlp standalone binary for CommercialBrainz YouTube commercial streaming.
# Install fpcalc (chromaprint-tools) for audio fingerprinting.
RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
        ca-certificates \
        chromaprint-tools \
        curl \
    && YTDLP_VERSION="$(tr -d '\r\n' < /tmp/yt-dlp-version.txt)" \
    && case "${TARGETARCH:-amd64}" in \
        amd64) docker_arch="x86_64"; ytdlp_asset="yt-dlp_linux" ;; \
        arm64) docker_arch="aarch64"; ytdlp_asset="yt-dlp_linux_aarch64" ;; \
        arm) docker_arch="armhf"; ytdlp_asset="yt-dlp_linux_armv7l.zip" ;; \
        *) echo "unsupported architecture: ${TARGETARCH}" >&2; exit 1 ;; \
       esac \
    && curl -fsSL "https://download.docker.com/linux/static/stable/${docker_arch}/docker-${DOCKER_CLI_VERSION}.tgz" -o /tmp/docker.tgz \
    && tar -xzf /tmp/docker.tgz -C /tmp \
    && install -m 0755 /tmp/docker/docker /usr/local/bin/docker \
    && rm -rf /tmp/docker /tmp/docker.tgz \
    && if [ "${ytdlp_asset}" = "yt-dlp_linux_armv7l.zip" ]; then \
         DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends unzip \
         && curl -fsSL "https://github.com/yt-dlp/yt-dlp/releases/download/${YTDLP_VERSION}/${ytdlp_asset}" -o /tmp/yt-dlp.zip \
         && unzip -q /tmp/yt-dlp.zip -d /tmp \
         && install -m 0755 /tmp/yt-dlp /usr/local/bin/yt-dlp \
         && rm -f /tmp/yt-dlp.zip /tmp/yt-dlp; \
       else \
         curl -fsSL "https://github.com/yt-dlp/yt-dlp/releases/download/${YTDLP_VERSION}/${ytdlp_asset}" -o /usr/local/bin/yt-dlp \
         && chmod 0755 /usr/local/bin/yt-dlp; \
       fi \
    && rm -f /tmp/yt-dlp-version.txt \
    && rm -rf /var/lib/apt/lists/*

ENV FINTV_DOCKER_READY=1

LABEL org.opencontainers.image.title="Jellyfin Unstable (FinTV-ready)" \
      org.opencontainers.image.description="Official jellyfin/jellyfin:unstable with Docker CLI 29.5.2, yt-dlp, and fpcalc for FinTV" \
      org.opencontainers.image.source="https://github.com/binarygeek119/FinTV" \
      org.opencontainers.image.vendor="binarygeek119" \
      fintv.docker-cli.version="${DOCKER_CLI_VERSION}"
