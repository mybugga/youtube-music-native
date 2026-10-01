# Downloads native runtime dependencies into src/YouTubeMusicNative/deps:
#   libmpv-2.dll  (shinchiro mpv-winbuild-cmake, x86_64 dev build)
#   yt-dlp.exe    (latest yt-dlp release)
#   deno.exe      (JavaScript runtime yt-dlp needs for YouTube; bundled so playback works without Node/Deno installed)
param([switch]$Force)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$deps = Join-Path $PSScriptRoot '..\src\YouTubeMusicNative\deps'
New-Item -ItemType Directory -Force $deps | Out-Null
$headers = @{ 'User-Agent' = 'YouTubeMusicNative-fetch-deps' }
if ($env:GITHUB_TOKEN) { $headers.Authorization = "Bearer $env:GITHUB_TOKEN" }  # CI: avoid the anonymous API rate limit

$mpvDll = Join-Path $deps 'libmpv-2.dll'
if ($Force -or -not (Test-Path $mpvDll)) {
    $release = Invoke-RestMethod 'https://api.github.com/repos/shinchiro/mpv-winbuild-cmake/releases/latest' -Headers $headers
    $asset = $release.assets | Where-Object { $_.name -match '^mpv-dev-x86_64-\d{8}-git-.*\.7z$' } | Select-Object -First 1
    if (-not $asset) { throw 'Could not find mpv-dev-x86_64 asset in latest release.' }

    $archive = Join-Path $env:TEMP $asset.name
    Write-Host "Downloading $($asset.name)..."
    Invoke-WebRequest $asset.browser_download_url -OutFile $archive -Headers $headers

    # Windows' bundled bsdtar (libarchive) can read 7z.
    & "$env:SystemRoot\System32\tar.exe" -xf $archive -C $deps 'libmpv-2.dll'
    if ($LASTEXITCODE -ne 0) { throw 'Failed to extract libmpv-2.dll' }
    Remove-Item $archive
}

$ytdlp = Join-Path $deps 'yt-dlp.exe'
if ($Force -or -not (Test-Path $ytdlp)) {
    Write-Host 'Downloading yt-dlp.exe...'
    Invoke-WebRequest 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe' -OutFile $ytdlp -Headers $headers
}

$deno = Join-Path $deps 'deno.exe'
if ($Force -or -not (Test-Path $deno)) {
    Write-Host 'Downloading deno.exe...'
    $zip = Join-Path $env:TEMP 'deno-x86_64-pc-windows-msvc.zip'
    Invoke-WebRequest 'https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip' -OutFile $zip -Headers $headers
    Expand-Archive $zip -DestinationPath $deps -Force
    Remove-Item $zip
}

Write-Host "Dependencies ready in $((Resolve-Path $deps).Path)"
