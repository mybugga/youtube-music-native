# Builds a release: self-contained publish, the Inno Setup installer and a portable zip, into .\dist.
#   .\scripts\build-release.ps1 -Version 1.2.3
# Needs Inno Setup 6 (ISCC.exe) on PATH or in its default install folder. CI runs this same script.
param(
    [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$publish = Join-Path $root 'publish'
$dist = Join-Path $root 'dist'

& (Join-Path $PSScriptRoot 'fetch-deps.ps1')

Remove-Item $publish, $dist -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $dist | Out-Null

# Self-contained, so the installer works on a machine without the .NET runtime.
dotnet publish (Join-Path $root 'src\YouTubeMusicNative\YouTubeMusicNative.csproj') -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -p:DebugType=none -p:DebugSymbols=false -o $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

$iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    $iscc = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) not found. Install it from https://jrsoftware.org/isdl.php' }

& $iscc /Q "/DAppVersion=$Version" "/DSourceDir=$publish" "/O$dist" (Join-Path $root 'installer\YouTubeMusicNative.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed' }

Compress-Archive -Path (Join-Path $publish '*') -DestinationPath (Join-Path $dist "YouTubeMusicNative-$Version-win-x64-portable.zip") -CompressionLevel Optimal

Get-ChildItem $dist | ForEach-Object { '{0,-45} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB) }
