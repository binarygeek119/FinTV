# FinTV-ready Jellyfin unstable: Docker CLI, yt-dlp, Playwright .NET driver, and sidecar control.
# Playwright Chromium runs in fintv-playwright-chromium (separate container), not in this image.
# The baked-in driver is only for Playwright.CreateAsync(); weather capture connects over CDP.
# Rebuilt automatically when jellyfin/jellyfin:unstable changes (see .github/workflows/build.yaml).

ARG JELLYFIN_TAG=unstable

FROM jellyfin/jellyfin:${JELLYFIN_TAG}

ARG TARGETARCH

USER root

COPY scripts/docker-cli-version.txt /tmp/docker-cli-version.txt
COPY scripts/yt-dlp-version.txt /tmp/yt-dlp-version.txt
COPY scripts/playwright-version.txt /tmp/playwright-version.txt

# Match host Docker Engine API (static CLI, not distro docker.io package).
# Install yt-dlp standalone binary for CommercialBrainz YouTube commercial streaming.
# Install fpcalc (libchromaprint-tools) for audio fingerprinting.
# Install Microsoft.Playwright .NET driver for FinTV weather capture (CDP sidecar control).
RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        libchromaprint-tools \
        unzip \
    && DOCKER_CLI_VERSION="$(tr -d '\r\n' < /tmp/docker-cli-version.txt)" \
    && YTDLP_VERSION="$(tr -d '\r\n' < /tmp/yt-dlp-version.txt)" \
    && PLAYWRIGHT_VERSION="$(tr -d '\r\n' < /tmp/playwright-version.txt)" \
    && case "${TARGETARCH:-amd64}" in \
        amd64) docker_arch="x86_64"; ytdlp_asset="yt-dlp_linux"; pw_node_dir="linux-x64" ;; \
        arm64) docker_arch="aarch64"; ytdlp_asset="yt-dlp_linux_aarch64"; pw_node_dir="linux-arm64" ;; \
        arm) docker_arch="armhf"; ytdlp_asset="yt-dlp_linux_armv7l.zip"; pw_node_dir="" ;; \
        *) echo "unsupported architecture: ${TARGETARCH}" >&2; exit 1 ;; \
       esac \
    && curl -fsSL "https://download.docker.com/linux/static/stable/${docker_arch}/docker-${DOCKER_CLI_VERSION}.tgz" -o /tmp/docker.tgz \
    && tar -xzf /tmp/docker.tgz -C /tmp \
    && install -m 0755 /tmp/docker/docker /usr/local/bin/docker \
    && rm -rf /tmp/docker /tmp/docker.tgz \
    && if [ "${ytdlp_asset}" = "yt-dlp_linux_armv7l.zip" ]; then \
         curl -fsSL "https://github.com/yt-dlp/yt-dlp/releases/download/${YTDLP_VERSION}/${ytdlp_asset}" -o /tmp/yt-dlp.zip \
         && unzip -q /tmp/yt-dlp.zip -d /tmp \
         && install -m 0755 /tmp/yt-dlp /usr/local/bin/yt-dlp \
         && rm -f /tmp/yt-dlp.zip /tmp/yt-dlp; \
       else \
         curl -fsSL "https://github.com/yt-dlp/yt-dlp/releases/download/${YTDLP_VERSION}/${ytdlp_asset}" -o /usr/local/bin/yt-dlp \
         && chmod 0755 /usr/local/bin/yt-dlp; \
       fi \
    && if [ -n "${pw_node_dir}" ]; then \
         mkdir -p /opt/fintv/playwright-driver/.playwright/node \
         && curl -fsSL "https://api.nuget.org/v3-flatcontainer/microsoft.playwright/${PLAYWRIGHT_VERSION}/microsoft.playwright.${PLAYWRIGHT_VERSION}.nupkg" -o /tmp/playwright.nupkg \
         && unzip -q /tmp/playwright.nupkg -d /tmp/playwright-nupkg \
         && cp -a /tmp/playwright-nupkg/.playwright/package /opt/fintv/playwright-driver/.playwright/ \
         && cp /tmp/playwright-nupkg/.playwright/node/LICENSE /opt/fintv/playwright-driver/.playwright/node/ \
         && cp -a /tmp/playwright-nupkg/.playwright/node/${pw_node_dir} /opt/fintv/playwright-driver/.playwright/node/ \
         && chmod 0755 /opt/fintv/playwright-driver/.playwright/node/${pw_node_dir}/node \
         && rm -rf /tmp/playwright.nupkg /tmp/playwright-nupkg; \
       fi \
    && rm -f /tmp/docker-cli-version.txt /tmp/yt-dlp-version.txt /tmp/playwright-version.txt \
    && rm -rf /var/lib/apt/lists/*

ENV FINTV_DOCKER_READY=1
ENV PLAYWRIGHT_DRIVER_SEARCH_PATH=/opt/fintv/playwright-driver

LABEL org.opencontainers.image.title="Jellyfin Unstable (FinTV-ready)" \
      org.opencontainers.image.description="Official jellyfin/jellyfin:unstable with Docker CLI, yt-dlp, fpcalc, and Playwright .NET driver for FinTV" \
      org.opencontainers.image.source="https://github.com/binarygeek119/jellyfin-unstable-fintv" \
      org.opencontainers.image.vendor="binarygeek119"
