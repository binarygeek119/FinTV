FROM mcr.microsoft.com/dotnet/sdk:10.0-noble-amd64 AS build
WORKDIR /src
COPY global.json ./
COPY src/ChannelFlow.Server/ src/ChannelFlow.Server/
COPY logo.png logo-plane.png ./
COPY logo.png src/ChannelFlow.Server/wwwroot/logo.png
COPY logo-plane.png src/ChannelFlow.Server/wwwroot/logo-plane.png
COPY vendor/ws4kp/server/fonts vendor/ws4kp/server/fonts
COPY vendor/ws4kp/server/images/backgrounds vendor/ws4kp/server/images/backgrounds
COPY vendor/ws4kp/server/images/icons/current-conditions vendor/ws4kp/server/images/icons/current-conditions
COPY vendor/ws4kp/server/images/maps/radar vendor/ws4kp/server/images/maps/radar
COPY vendor/ws4kp/server/music/default vendor/ws4kp/server/music/default
COPY vendor/ws3kp/server/fonts vendor/ws3kp/server/fonts
COPY vendor/ws3kp/server/images/backgrounds vendor/ws3kp/server/images/backgrounds
ARG CHANNELFLOW_VERSION=1.0.0
ARG CHANNELFLOW_REVISION=dev
RUN dotnet publish src/ChannelFlow.Server/ChannelFlow.Server.csproj -c Release -o /app/publish \
    /p:Version=1.0.0 \
    /p:InformationalVersion=${CHANNELFLOW_VERSION}+${CHANNELFLOW_REVISION}

# Same layout as https://github.com/ErsatzTV/legacy/blob/main/docker/Dockerfile:
# .NET runtime on top of ersatztv-ffmpeg (VAAPI/QSV, libva 2.23, Intel iHD).
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-amd64 AS dotnet-runtime

FROM --platform=linux/amd64 ghcr.io/ersatztv/ersatztv-ffmpeg:8.1.2
ARG CHANNELFLOW_VERSION=1.0.0
ARG CHANNELFLOW_REVISION=dev
COPY --from=dotnet-runtime /usr/share/dotnet /usr/share/dotnet
ENV TZ=America/Chicago \
    FONTCONFIG_PATH=/etc/fonts \
    DOTNET_ROOT=/usr/share/dotnet \
    PATH="/usr/share/dotnet:${PATH}"
RUN apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
        tzdata \
        python3 \
        ca-certificates \
        wget \
    && ln -snf "/usr/share/zoneinfo/${TZ}" /etc/localtime \
    && echo "${TZ}" > /etc/timezone \
    && fc-cache -f \
    && wget -qO /usr/local/bin/yt-dlp https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp \
    && chmod +x /usr/local/bin/yt-dlp \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

ENV CHANNELFLOW_CONFIG=/config \
    FINTV_CONFIG=/config \
    FFMPEG_PATH=/usr/local/bin/ffmpeg \
    CHANNELFLOW_YTDLP_PATH=/usr/local/bin/yt-dlp \
    FINTV_YTDLP_PATH=/usr/local/bin/yt-dlp \
    FFMPEG_HWACCEL=vaapi \
    FFMPEG_VAAPI_DEVICE=/dev/dri/renderD128 \
    CHANNELFLOW_VERSION=${CHANNELFLOW_VERSION} \
    CHANNELFLOW_REVISION=${CHANNELFLOW_REVISION} \
    CHANNELFLOW_PACKAGING=docker \
    PORT=8097 \
    TZ=America/Chicago

EXPOSE 8097
VOLUME ["/config"]
HEALTHCHECK --interval=30s --timeout=5s --start-period=40s --retries=3 \
    CMD wget -qO- http://127.0.0.1:8097/health >/dev/null || exit 1
ENTRYPOINT ["/usr/share/dotnet/dotnet", "ChannelFlow.Server.dll"]
