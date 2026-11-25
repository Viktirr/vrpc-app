# Builds the installable package: self-contained publish plus payload.json.
param(
    [string]$Version,
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$repoRoot = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$props = Get-Content (Join-Path $repoRoot "Directory.Build.props")
    $versionNode = $props.SelectSingleNode("//Version")
    if ($null -eq $versionNode) { throw "Directory.Build.props does not contain a Version element." }
    $Version = $versionNode.InnerText.Trim()
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "artifacts"
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot $OutputDirectory
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)

$packageName = "VRPC-$Version-$Runtime"
$packageDirectory = Join-Path $OutputDirectory $packageName
$publishDirectory = Join-Path $OutputDirectory "publish-$Runtime"
$zipPath = Join-Path $OutputDirectory "$packageName.zip"

if (Test-Path $packageDirectory) { Remove-Item -LiteralPath $packageDirectory -Recurse -Force }
if (Test-Path $publishDirectory) { Remove-Item -LiteralPath $publishDirectory -Recurse -Force }
if (Test-Path $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null

dotnet publish (Join-Path $repoRoot "VRPC.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:Version=$Version `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publishDirectory

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE); no package, signature or release may be produced." }

Get-ChildItem $publishDirectory -Recurse -File | Where-Object { $_.Extension -in ".pdb", ".xml" } | Remove-Item -Force

# The setup panel: a separate Rust process that VRPC.exe spawns. It is Windows
# only, so a linux-x64 publish simply ships without it and setup falls back to
# the console. A release must never lose it silently, so a failed build stops
# the package here instead of producing one that can only use the console.
if ($Runtime -like "win-*") {
    $installerUiDirectory = Join-Path $repoRoot "installer-ui"
    $installerUiExe = Join-Path $installerUiDirectory "target\release\vrpc-installer-ui.exe"
    Write-Host "Building the setup panel (cargo build --release --locked --no-default-features --features verify)..."

    Push-Location $installerUiDirectory
    try {
        # --no-default-features drops the inherited VRPC_UI_* automation hooks
        # from the panel that ships to users; `verify` stays for --probe/--dump.
        cargo build --release --locked --no-default-features --features verify
        if ($LASTEXITCODE -ne 0) { throw "cargo build failed for installer-ui (exit $LASTEXITCODE)." }
    }
    finally {
        Pop-Location
    }

    if (-not (Test-Path $installerUiExe)) { throw "The setup panel was not produced at $installerUiExe." }
    Copy-Item -LiteralPath $installerUiExe -Destination (Join-Path $publishDirectory "vrpc-installer-ui.exe") -Force
    Write-Host "Setup panel: $([System.IO.Path]::GetFileName($installerUiExe)) $((Get-Item $installerUiExe).Length) bytes"
}

$reportedRaw = (& (Join-Path $publishDirectory "VRPC.exe") --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw "The published VRPC.exe --version check failed (exit $LASTEXITCODE)." }

$reportedMatch = [regex]::Match($reportedRaw, '^VRPC\s+(?<version>\S+)$')
if (-not $reportedMatch.Success) {
    throw "The published executable reported '$reportedRaw', which is not 'VRPC <version>'."
}
$reportedVersion = $reportedMatch.Groups["version"].Value
if (-not [string]::Equals($reportedVersion, $Version, [System.StringComparison]::Ordinal)) {
    throw "Published executable reports '$reportedVersion', expected exactly '$Version'."
}
Copy-Item -Path (Join-Path $publishDirectory "*") -Destination $packageDirectory -Recurse -Force

# The manifest is the installer's allow-list, with a hash per file.
$files = Get-ChildItem $packageDirectory -Recurse -File | Sort-Object FullName | ForEach-Object {
    $relative = $_.FullName.Substring($packageDirectory.Length).TrimStart('\', '/').Replace('\', '/')
    [ordered]@{
        path = $relative
        size = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

$payload = [ordered]@{
    schema = 1
    product = "VRPC"
    version = $Version
    channel = "stable"
    entryPoint = "VRPC.exe"
    files = $files
}

$payloadJson = $payload | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText((Join-Path $packageDirectory "payload.json"), $payloadJson, (New-Object System.Text.UTF8Encoding($false)))

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $packageDirectory,
    $zipPath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false)

Write-Host "Package: $packageDirectory"
Write-Host "Zip:     $zipPath"
Write-Host "Files:   $($files.Count)"
