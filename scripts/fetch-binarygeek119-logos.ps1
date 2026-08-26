# One-shot helper to vendor ChannelFlow artwork from FlowMeadow01/ChannelFlow-logo.
# Artwork is committed under src/ChannelFlow.Server/wwwroot/images, wwwroot/audio, and wwwroot/videos.
# Builds and runtime no longer fetch from GitHub.
param(
    [string]$OutputDir = (Join-Path $PSScriptRoot "..\src\ChannelFlow.Server\wwwroot\images\logos")
)

$ErrorActionPreference = "Stop"

$Repo = "FlowMeadow01/ChannelFlow-logo"
$GitRef = "main"
$TreeUrl = "https://api.github.com/repos/$Repo/git/trees/$GitRef`?recursive=1"
$RawBase = "https://raw.githubusercontent.com/$Repo/$GitRef/"
$LogoPrefixes = @(
    "EBS/",
    "OFFLINE/",
    "Movies/",
    "News/",
    "Shows/",
    "Music Videos Channels/",
    "The Holiday Channel/",
    "The_Holiday_Channel_Filler/",
    "Weather/"
)
$ImageSuffixes = @(".png", ".jpg", ".jpeg", ".webp")

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$headers = @{
    Accept       = "application/vnd.github+json"
    "User-Agent" = "ChannelFlow-Server"
}

$tree = Invoke-RestMethod -Uri $TreeUrl -Headers $headers
$files = $tree.tree | Where-Object {
    $path = $_.path
    $_.type -eq "blob" -and
    ($LogoPrefixes | Where-Object { $path.StartsWith($_) }) -and
    ($ImageSuffixes -contains [IO.Path]::GetExtension($path).ToLowerInvariant())
}

Write-Host "Bundling $($files.Count) logos from ${Repo}@${GitRef} into $OutputDir"

$client = New-Object System.Net.WebClient
$client.Headers.Add("User-Agent", "ChannelFlow-Server")

foreach ($file in $files) {
    $repoPath = $file.path
    $relative = $repoPath
    if ($repoPath.StartsWith("OFFLINE/")) {
        $relative = "EBS/" + [IO.Path]::GetFileName($repoPath)
    }
    $destination = Join-Path $OutputDir ($relative -replace '/', [IO.Path]::DirectorySeparatorChar)
    if (Test-Path -LiteralPath $destination) {
        continue
    }

    $encodedPath = ($file.path.Split('/') | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/'
    $url = "$RawBase$encodedPath"
    $parent = [System.IO.Path]::GetDirectoryName($destination)
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }

    $client.DownloadFile($url, $destination)
    Write-Host "  $repoPath -> $relative"
}

$client.Dispose()
