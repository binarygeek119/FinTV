param(
    [string]$OutputDir = (Join-Path $PSScriptRoot "..\Jellyfin.Plugin.FinTV\Assets\tools\yt-dlp")
)

$ErrorActionPreference = "Stop"

$VersionFile = Join-Path $PSScriptRoot "yt-dlp-version.txt"
if (-not (Test-Path $VersionFile)) {
    throw "Missing yt-dlp version file: $VersionFile"
}

$Version = (Get-Content -Path $VersionFile -Raw).Trim()
if ([string]::IsNullOrWhiteSpace($Version)) {
    throw "yt-dlp version file is empty: $VersionFile"
}

$Platforms = @{
    "win-x64"   = @{ Asset = "yt-dlp.exe"; Output = "yt-dlp.exe" }
    "linux-x64" = @{ Asset = "yt-dlp_linux"; Output = "yt-dlp" }
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

foreach ($platform in $Platforms.Keys) {
    $asset = $Platforms[$platform].Asset
    $outputName = $Platforms[$platform].Output
    $platformDir = Join-Path $OutputDir $platform
    New-Item -ItemType Directory -Force -Path $platformDir | Out-Null
    $destination = Join-Path $platformDir $outputName
    $url = "https://github.com/yt-dlp/yt-dlp/releases/download/$Version/$asset"

    Write-Host "Downloading $url -> $destination"
    Invoke-WebRequest -Uri $url -OutFile $destination -UseBasicParsing
}

Set-Content -Path (Join-Path $OutputDir "VERSION.txt") -Value $Version -NoNewline
Add-Content -Path (Join-Path $OutputDir "VERSION.txt") -Value ""
Write-Host "Bundled yt-dlp $Version into $OutputDir"
