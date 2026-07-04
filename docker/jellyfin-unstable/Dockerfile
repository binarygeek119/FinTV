# FinTV-ready Jellyfin unstable: Docker CLI + Playwright Chromium baked in.
# Rebuilt automatically when jellyfin/jellyfin:unstable changes (see .github/workflows).

ARG PLAYWRIGHT_VERSION=1.49.0
ARG JELLYFIN_TAG=unstable
ARG DOCKER_CLI_VERSION=29.5.2

FROM mcr.microsoft.com/playwright:v${PLAYWRIGHT_VERSION}-jammy AS playwright

FROM jellyfin/jellyfin:${JELLYFIN_TAG}

ARG DOCKER_CLI_VERSION
ARG PLAYWRIGHT_VERSION=1.49.0
ARG TARGETARCH

USER root

# Match host Docker Engine API (static CLI, not distro docker.io package).
RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
    && case "${TARGETARCH:-amd64}" in \
        amd64) docker_arch="x86_64" ;; \
        arm64) docker_arch="aarch64" ;; \
        arm) docker_arch="armhf" ;; \
        *) echo "unsupported architecture: ${TARGETARCH}" >&2; exit 1 ;; \
       esac \
    && curl -fsSL "https://download.docker.com/linux/static/stable/${docker_arch}/docker-${DOCKER_CLI_VERSION}.tgz" -o /tmp/docker.tgz \
    && tar -xzf /tmp/docker.tgz -C /tmp \
    && install -m 0755 /tmp/docker/docker /usr/local/bin/docker \
    && rm -rf /tmp/docker /tmp/docker.tgz \
    && rm -rf /var/lib/apt/lists/*

COPY --from=playwright /ms-playwright /ms-playwright

# .NET Playwright driver (node + package). FinTV plugin no longer bundles linux-x64 node.
RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
        unzip \
    && curl -fsSL "https://www.nuget.org/api/v2/package/Microsoft.Playwright/${PLAYWRIGHT_VERSION}" -o /tmp/playwright.nupkg \
    && unzip -q /tmp/playwright.nupkg -d /tmp/playwright-nupkg \
    && mkdir -p /opt/playwright-driver \
    && cp -a /tmp/playwright-nupkg/.playwright /opt/playwright-driver/ \
    && find /opt/playwright-driver/.playwright/node -name node -type f -exec chmod 0755 {} + \
    && rm -rf /tmp/playwright.nupkg /tmp/playwright-nupkg \
    && rm -rf /var/lib/apt/lists/*

# Chromium shared libraries for Debian/Ubuntu (compatible with jellyfin/jellyfin base).
RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
        fonts-liberation \
        fonts-noto-color-emoji \
        libasound2 \
        libatk-bridge2.0-0 \
        libatk1.0-0 \
        libatspi2.0-0 \
        libcairo2 \
        libcups2 \
        libdbus-1-3 \
        libdrm2 \
        libgbm1 \
        libglib2.0-0 \
        libnspr4 \
        libnss3 \
        libpango-1.0-0 \
        libx11-6 \
        libxcb1 \
        libxcomposite1 \
        libxdamage1 \
        libxext6 \
        libxfixes3 \
        libxkbcommon0 \
        libxrandr2 \
    && rm -rf /var/lib/apt/lists/*

ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright \
    PLAYWRIGHT_DRIVER_SEARCH_PATH=/opt/playwright-driver \
    FINTV_DOCKER_READY=1

RUN chmod -R a+rX /ms-playwright /opt/playwright-driver

LABEL org.opencontainers.image.title="Jellyfin Unstable (FinTV-ready)" \
      org.opencontainers.image.description="Official jellyfin/jellyfin:unstable with Docker CLI 29.5.2, Playwright Chromium, and .NET Playwright driver for FinTV" \
      org.opencontainers.image.source="https://github.com/binarygeek119/FinTV" \
      org.opencontainers.image.vendor="binarygeek119" \
      fintv.docker-cli.version="${DOCKER_CLI_VERSION}"
