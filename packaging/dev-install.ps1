# Builds a self-contained Debug package, then runs its setup in update mode.
param(
    [string]$Version = "1.10.0-dev",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$packageDirectory = Join-Path $repoRoot "artifacts\VRPC-$Version-$Runtime"

& (Join-Path $PSScriptRoot "build-release.ps1") `
    -Version $Version `
    -Configuration Debug `
    -Runtime $Runtime `
    -OutputDirectory (Join-Path $repoRoot "artifacts")

$setup = Join-Path $packageDirectory "VRPC.exe"
if (-not (Test-Path $setup)) { throw "The dev package was not created at $packageDirectory." }

& $setup --install --payload $packageDirectory --force
exit $LASTEXITCODE
