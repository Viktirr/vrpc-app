# Failure-injection checks for the release scripts. These are deliberately
# separate from `dotnet test`: they exercise native-process exit handling and
# asset byte verification without contacting the network, signing, publishing or
# touching the feed repository.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File packaging\test-release-scripts.ps1

$ErrorActionPreference = "Stop"
$scriptRoot = $PSScriptRoot
$repoRoot = Split-Path -Parent $scriptRoot
$buildScript = Join-Path $scriptRoot "build-release.ps1"
$publishScript = Join-Path $scriptRoot "publish-update.ps1"

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

[xml]$props = Get-Content (Join-Path $repoRoot "Directory.Build.props")
$version = $props.SelectSingleNode("//Version").InnerText.Trim()

# 1. Asset mismatch self-test in publish-update.ps1.
& powershell -NoProfile -ExecutionPolicy Bypass -File $publishScript -SelfTest
if ($LASTEXITCODE -ne 0) { throw "publish-update.ps1 -SelfTest failed (exit $LASTEXITCODE)." }
Write-Host "[release-scripts] PASS: asset hash/size mismatches are rejected."

# 2. A failed native `dotnet publish` must stop before anything is packaged.
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("vrpc-release-script-test-" + [guid]::NewGuid().ToString("N"))
$fakeBin = Join-Path $tempRoot "bin"
$outputDirectory = Join-Path $tempRoot "out"
$marker = Join-Path $tempRoot "dotnet-was-called"
New-Item -ItemType Directory -Path $fakeBin -Force | Out-Null
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

$oldPath = $env:PATH
try {
    $fakeDotnet = Join-Path $fakeBin "dotnet.cmd"
    $fakeBody = "@echo off`r`necho called>`"$marker`"`r`nexit /b 7`r`n"
    [System.IO.File]::WriteAllText($fakeDotnet, $fakeBody, (New-Object System.Text.ASCIIEncoding))
    $env:PATH = $fakeBin + ";" + $oldPath

    $failed = $false
    try {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $buildScript `
            -Version $version -Runtime "linux-x64" -OutputDirectory $outputDirectory
    }
    catch {
        $failed = $true
    }
    if (-not $failed -and $LASTEXITCODE -eq 0) { throw "build-release.ps1 succeeded with a failing dotnet." }

    Assert-True (Test-Path -LiteralPath $marker) "The fake dotnet was never invoked."
    $zipPath = Join-Path $outputDirectory "VRPC-$version-linux-x64.zip"
    Assert-True (-not (Test-Path -LiteralPath $zipPath)) "A failed dotnet publish produced a ZIP at $zipPath."
    Write-Host "[release-scripts] PASS: a nonzero dotnet publish stops the build before packaging."
}
finally {
    $env:PATH = $oldPath
    try { Remove-Item -LiteralPath $tempRoot -Recurse -Force } catch { }
}

Write-Host "[release-scripts] All release-script failure-injection checks passed."
exit 0
