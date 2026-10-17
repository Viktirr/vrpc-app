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

Get-ChildItem $publishDirectory -Recurse -File | Where-Object { $_.Extension -in ".pdb", ".xml" } | Remove-Item -Force
$reportedVersion = (& (Join-Path $publishDirectory "VRPC.exe") --version).Trim()
if ($reportedVersion -notmatch [regex]::Escape($Version)) {
    throw "Published executable reports '$reportedVersion', expected version '$Version'."
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
