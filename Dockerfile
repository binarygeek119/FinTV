FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
RUN apt-get update && apt-get install -y --no-install-recommends python3 ca-certificates \
    && rm -rf /var/lib/apt/lists/*
COPY global.json ./
COPY src/ChannelFlow.Server/ src/ChannelFlow.Server/
COPY scripts/ scripts/
COPY logo.png logo-plane.png ./
COPY favicon_io/ favicon_io/
COPY logo.png src/ChannelFlow.Server/wwwroot/logo.png
COPY logo-plane.png src/ChannelFlow.Server/wwwroot/logo-plane.png
COPY favicon_io/favicon.ico favicon_io/favicon-16x16.png favicon_io/favicon-32x32.png favicon_io/apple-touch-icon.png favicon_io/android-chrome-192x192.png favicon_io/android-chrome-512x512.png src/ChannelFlow.Server/wwwroot/
COPY vendor/ws4kp/server/fonts vendor/ws4kp/server/fonts
COPY vendor/ws4kp/server/images/backgrounds vendor/ws4kp/server/images/backgrounds
COPY vendor/ws4kp/server/images/icons/current-conditions vendor/ws4kp/server/images/icons/current-conditions
COPY vendor/ws4kp/server/images/maps/radar vendor/ws4kp/server/images/maps/radar
COPY vendor/ws4kp/server/music/default vendor/ws4kp/server/music/default
COPY vendor/ws3kp/server/fonts vendor/ws3kp/server/fonts
COPY vendor/ws3kp/server/images/backgrounds vendor/ws3kp/server/images/backgrounds
RUN python3 scripts/fetch-binarygeek119-logos.py src/ChannelFlow.Server/wwwroot/logos/binarygeek119 \
    || echo "Logo fetch skipped (offline or rate-limited)"
ARG CHANNELFLOW_VERSION=1.0.0
ARG CHANNELFLOW_REVISION=dev
RUN dotnet publish src/ChannelFlow.Server/ChannelFlow.Server.csproj -c Release -o /app/publish /p:SkipLogoFetch=true \
    /p:Version=1.0.0 \
    /p:InformationalVersion=${CHANNELFLOW_VERSION}+${CHANNELFLOW_REVISION}

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG CHANNELFLOW_VERSION=1.0.0
ARG CHANNELFLOW_REVISION=dev
RUN apt-get update && apt-get install -y --no-install-recommends \
        ffmpeg \
        ca-certificates \
        python3 \
        wget \
        fonts-liberation \
        fonts-dejavu-core \
        intel-media-va-driver \
        i965-va-driver \
        mesa-va-drivers \
        libva2 \
        vainfo \
        libfontconfig1 \
    && rm -rf /var/lib/apt/lists/* \
    && wget -qO /usr/local/bin/yt-dlp https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp \
    && chmod +x /usr/local/bin/yt-dlp

WORKDIR /app
COPY --from=build /app/publish .

ENV CHANNELFLOW_CONFIG=/config \
    FINTV_CONFIG=/config \
    FFMPEG_PATH=/usr/bin/ffmpeg \
    CHANNELFLOW_YTDLP_PATH=/usr/local/bin/yt-dlp \
    FINTV_YTDLP_PATH=/usr/local/bin/yt-dlp \
    FFMPEG_HWACCEL=vaapi \
    FFMPEG_VAAPI_DEVICE=/dev/dri/renderD128 \
    CHANNELFLOW_VERSION=${CHANNELFLOW_VERSION} \
    CHANNELFLOW_REVISION=${CHANNELFLOW_REVISION} \
    CHANNELFLOW_PACKAGING=docker \
    PORT=8097

EXPOSE 8097
VOLUME ["/config"]
HEALTHCHECK --interval=30s --timeout=5s --start-period=40s --retries=3 \
    CMD wget -qO- http://127.0.0.1:8097/health >/dev/null || exit 1
ENTRYPOINT ["dotnet", "ChannelFlow.Server.dll"]
