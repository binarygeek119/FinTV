param(
    [string]$OutputDir = (Join-Path $PSScriptRoot "..\Jellyfin.Plugin.FinTV\Assets\logos\binarygeek119")
)

$ErrorActionPreference = "Stop"

$Repo = "binarygeek119/open-channel-logos"
$GitRef = "fintv2"
$TreeUrl = "https://api.github.com/repos/$Repo/git/trees/$GitRef`?recursive=1"
$RawBase = "https://raw.githubusercontent.com/$Repo/$GitRef/"
$LogoPrefixes = @(
    "EBS/",
    "Movies/",
    "Shows/",
    "Music Videos Channels/",
    "The Holiday Channel/"
)
$ImageSuffixes = @(".png", ".jpg", ".jpeg", ".webp")

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$headers = @{
    Accept       = "application/vnd.github+json"
    "User-Agent" = "FinTV-Jellyfin-Plugin"
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
$client.Headers.Add("User-Agent", "FinTV-Jellyfin-Plugin")

foreach ($file in $files) {
    $relative = $file.path
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
    Write-Host "  $relative"
}

$client.Dispose()
