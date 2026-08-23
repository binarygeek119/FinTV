# Publish self-contained ChannelFlow-Server apps for Linux and Windows.
# Native apps are not the default distribution yet (Docker still is).
param(
    [ValidateSet("linux-x64", "win-x64", "all")]
    [string]$Rid = "all",
    [string]$Version = $(if ($env:CHANNELFLOW_VERSION) { $env:CHANNELFLOW_VERSION } else { "1.0.0" }),
    [string]$Revision = $(if ($env:CHANNELFLOW_REVISION) { $env:CHANNELFLOW_REVISION } else { "dev" })
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src/FinTv.Server/FinTv.Server.csproj"

function Publish-Rid([string]$Target) {
    $out = Join-Path $root "artifacts/native/$Target"
    Write-Host "Publishing $Target → $out"
    dotnet publish $project `
        -c Release `
        -r $Target `
        --self-contained true `
        -o $out `
        /p:SkipLogoFetch=true `
        /p:Version=$Version `
        /p:InformationalVersion="$Version+$Target.$Revision" `
        /p:PublishReadyToRun=false
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $Target"
    }
}

switch ($Rid) {
    "all" {
        Publish-Rid "linux-x64"
        Publish-Rid "win-x64"
    }
    default { Publish-Rid $Rid }
}
