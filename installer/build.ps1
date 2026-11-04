#Requires -Version 7.0
<#
.SYNOPSIS
  Builds the UPS Tray Monitor MSI installer.

.DESCRIPTION
  Publishes the app as a framework-dependent win-x64 build and packages it
  into a small per-user MSI with WiX v5. Requires the WiX command-line tool:

    dotnet tool install --global wix
#>
param(
    # Override the version baked into the MSI (default: csproj <Version>).
    [string]$Version
)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $root 'src\UpsTrayMonitor\UpsTrayMonitor.csproj'
$publishDir = Join-Path $PSScriptRoot 'publish'
$distDir = Join-Path $PSScriptRoot 'dist'

if (-not $Version) {
    $Version = (dotnet msbuild $csproj -getProperty:Version).Trim()
    if (-not $Version) { throw 'Could not read <Version> from the csproj.' }
}

# The MSI uses a two-part Major.Minor version (e.g. 1.0), even though the
# assembly version has three parts (1.0.0).
$Version = ($Version -split '\.')[0..1] -join '.'

Write-Output "Publishing framework-dependent build (v$Version)..."
dotnet publish $csproj -c Release -r win-x64 --self-contained false -o $publishDir --nologo -v q
if ($LASTEXITCODE) { exit $LASTEXITCODE }

New-Item -ItemType Directory -Path $distDir -Force | Out-Null
$msi = Join-Path $distDir "ups-tray-monitor-$Version.msi"

Write-Output "Building $msi ..."
wix build (Join-Path $PSScriptRoot 'Product.wxs') `
    -d "Version=$Version" `
    -d "PublishDir=$publishDir" `
    -d "AssetsDir=$(Join-Path $root 'assets')" `
    -o $msi
if ($LASTEXITCODE) { exit $LASTEXITCODE }

Write-Output ("Done: {0} ({1:N1} MB)" -f $msi, ((Get-Item $msi).Length / 1MB))
