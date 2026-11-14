# Builds a self-contained Debug package, then runs its setup in update mode.
param(
    [string]$Version = "1.1.0-dev",
    [string]$Runtime = "win-x64",
    # The unpacked dev copy gets an id derived from its folder, not the store id the app allows by
    # default, so register it here or the extension cannot reach the app.
    [string]$ExtensionId = "ajhfgjkcmjjngfjiobckkhilpbfafilm"
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

$setupArgs = @("--install", "--payload", $packageDirectory, "--force")
if ($ExtensionId) { $setupArgs += @("--extension-id", $ExtensionId) }

& $setup @setupArgs
exit $LASTEXITCODE
