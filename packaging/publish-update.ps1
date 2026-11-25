# Publishes a VRPC release: hashes the immutable ZIP, signs the update feed payload
# offline, optionally publishes the GitHub release, verifies the public asset, and
# installs the signed manifest into the website/API feed.
#
# This script never prints or writes the GitHub token. It fails loudly on any
# mismatch and -DryRun never touches GitHub, the feed file, or git.
#
# Usage examples are in the comment-based help below (Get-Help .\publish-update.ps1).

<#
.SYNOPSIS
    Publishes a signed VRPC release manifest and (optionally) the GitHub release.

.DESCRIPTION
    Implements the release procedure from AUTO_UPDATER.md section 10 and the feed
    manifest contract from AUTO_UPDATER_IMPL.md, using the offline signer in
    packaging/sign-manifest.mjs.

    Steps:
      1. Validate -Version against Directory.Build.props and the strict x.y.z pattern.
      2. Build the package with build-release.ps1 (unless -SkipBuild).
      3. Verify the ZIP: required files, payload.json schema/product/version/channel,
         and every payload.json file size + SHA-256 against the archive bytes.
      4. Compute the exact ZIP size and lowercase SHA-256.
      5. Require -KeyId to be embedded in Modules/Updates/TrustedUpdateKeys.cs and
         to match the -KeyPath private key, then sign the payload offline (ECDSA
         P-256 / SHA-256 over RFC 8785 JCS bytes).
      6. Verify the produced envelope with the trusted public key and cross-check
         all fields, including that the content is publishable (exact ZIP SHA-256,
         64-byte non-zero signature, trusted key id) rather than a placeholder.
      7. Write <OutDir>\release-plan-<version>.json (and stop with -DryRun).
      8. With -PublishGitHub: create/reuse a DRAFT GitHub release, verify the asset
         bytes BEFORE publishing, publish it, then download the public asset and
         verify its exact size and SHA-256.
      9. Copy the verified envelope into -FeedPath (atomic, UTF-8 without BOM). This
         requires -PublishGitHub, or -AllowUnpublishedFeed for a deliberate local
         manifest; with -DeployFeed commit and push it (push to main triggers the
         API deploy).

    Ordering matters: the feed is only written after the immutable asset is publicly
    downloadable and byte-verified, so no client ever sees an update it cannot fetch.

.PARAMETER Version
    Strict x.y.z version to publish. Must equal the version in Directory.Build.props.

.PARAMETER Runtime
    Release runtime. Only win-x64 is supported by the stable feed.

.PARAMETER KeyId
    Key id of the signing key; recorded in the envelope and embedded in the app.

.PARAMETER KeyPath
    PKCS#8 PEM private key (ECDSA P-256). Must live outside every repository.

.PARAMETER PublicKeyPath
    Optional base64 SPKI public key file. The trusted key is always read from
    Modules/Updates/TrustedUpdateKeys.cs (the updater's embedded key ring) and the
    envelope is verified with it; when this file is given (or the default
    <directory of -KeyPath>\<KeyId>.public.spki.b64 exists) it must match the
    trusted key. The public key derived from -KeyPath must match it as well.

.PARAMETER NotesUrl
    Informational release notes URL in the signed payload.
    Default: https://github.com/Viktirr/vrpc-app/releases/tag/v<version>

.PARAMETER AssetBase
    Base URL of the release asset, without trailing slash.
    Default: https://github.com/Viktirr/vrpc-app/releases/download
    Only that exact base is accepted: the updater rejects any other asset URL, so
    a value that would change the URL prefix is an error instead of a silent footgun.

.PARAMETER SkipBuild
    Reuse an existing <OutDir>\VRPC-<version>-<runtime>.zip instead of building.

.PARAMETER DryRun
    Do everything offline (build unless -SkipBuild, hash, sign, verify, plan) and stop
    before GitHub, the feed file, or git are touched. The trusted-key check and the
    publishable-content guard run in a dry run too, so an untrusted key cannot pass.

.PARAMETER PublishGitHub
    Create/reuse a draft GitHub release for tag v<version>, upload the ZIP, publish it,
    and verify the public download.

.PARAMETER AllowUnpublishedFeed
    Write the signed feed even though -PublishGitHub was not passed. This exists for a
    deliberate staging/local manifest only: the feed then points at an asset that is
    not publicly downloadable yet. Real releases use -PublishGitHub instead.

.PARAMETER GitHubToken
    GitHub token for the REST API. Falls back to the GH_TOKEN environment variable,
    then to -UseGhCli if gh is installed.

.PARAMETER UseGhCli
    Use the GitHub CLI for the release steps when gh is installed and authenticated.

.PARAMETER FeedPath
    Signed manifest destination in the API repository. Required unless -DryRun is
    set; the script has no default location outside this repository.

.PARAMETER DeployFeed
    Commit and push the feed manifest in its git repository. Requires -PublishGitHub.

.PARAMETER OutDir
    Package and output directory. Default: vrpc-app\artifacts. When -SkipBuild is
    set and the ZIP is missing here, the existing package in vrpc-app\artifacts is
    reused instead.

.PARAMETER EnvelopePath
    Signed manifest output path.
    Default: <OutDir>\VRPC-<version>-<runtime>.update-manifest.json

.PARAMETER PlanPath
    Release plan output path. Default: <OutDir>\release-plan-<version>.json

.PARAMETER TargetCommitish
    Commit SHA the GitHub release tag points at. Default: current HEAD of vrpc-app.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File packaging\publish-update.ps1 `
        -Version 1.1.0 -KeyId vrpc-updates-2026-11 `
        -KeyPath "$env:USERPROFILE\.vrpc-release-keys\vrpc-updates-2026-11.private.pem" -DryRun

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File packaging\publish-update.ps1 `
        -Version 1.2.0 -KeyId vrpc-updates-2026-11 `
        -KeyPath "$env:USERPROFILE\.vrpc-release-keys\vrpc-updates-2026-11.private.pem" `
        -PublishGitHub -DeployFeed `
        -FeedPath "C:\src\api\src\data\vrpc-update-manifest.json"
#>

[CmdletBinding()]
param(
    [string]$Version = "",
    [string]$Runtime = "win-x64",
    [string]$KeyId = "",
    [string]$KeyPath = "",
    [string]$PublicKeyPath = "",
    [string]$NotesUrl = "",
    [string]$AssetBase = "",
    [switch]$SkipBuild,
    [switch]$DryRun,
    [switch]$PublishGitHub,
    [switch]$AllowUnpublishedFeed,
    [string]$GitHubToken = "",
    [switch]$UseGhCli,
    [string]$FeedPath = "",
    [switch]$DeployFeed,
    [string]$OutDir = "",
    [string]$EnvelopePath = "",
    [string]$PlanPath = "",
    [string]$TargetCommitish = "",
    [switch]$SelfTest
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$script:Repo = "Viktirr/vrpc-app"
$script:ApiBase = "https://api.github.com"
$script:Channel = "stable"
$script:VersionPattern = '^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})$'
$script:ScriptRoot = $PSScriptRoot
$script:RepoRoot = Split-Path -Parent $PSScriptRoot
$script:Cwd = (Get-Location).Path

# ---------------------------------------------------------------------------
# Small helpers
# ---------------------------------------------------------------------------

function Write-Step {
    param([string]$Message)
    Write-Host ("[publish-update] " + $Message)
}

function Fail {
    param([string]$Message)
    throw $Message
}

function Resolve-UserPath {
    param([string]$Path, [string]$BaseDir)
    if ([System.IO.Path]::IsPathRooted($Path)) { return [System.IO.Path]::GetFullPath($Path) }
    return [System.IO.Path]::GetFullPath((Join-Path $BaseDir $Path))
}

function Get-Sha256Hex {
    param([string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Write-TextAtomic {
    param([string]$Path, [string]$Text)
    $directory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    $tempPath = Join-Path $directory (".tmp-" + [guid]::NewGuid().ToString("N") + ".json")
    $encoding = New-Object System.Text.UTF8Encoding($false)
    try {
        [System.IO.File]::WriteAllText($tempPath, $Text, $encoding)
        if (Test-Path -LiteralPath $Path) {
            # File.Replace with an explicit backup path: passing $null for the backup
            # is rejected by File.Replace under Windows PowerShell 5.1.
            $backupPath = Join-Path $directory (".bak-" + [guid]::NewGuid().ToString("N") + ".json")
            [System.IO.File]::Replace($tempPath, $Path, $backupPath)
            if (Test-Path -LiteralPath $backupPath) { Remove-Item -LiteralPath $backupPath -Force -ErrorAction SilentlyContinue }
        }
        else {
            [System.IO.File]::Move($tempPath, $Path)
        }
    }
    finally {
        if (Test-Path -LiteralPath $tempPath) { Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue }
    }
}

function Copy-FileAtomic {
    param([string]$Source, [string]$Destination)
    $directory = Split-Path -Parent $Destination
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    $tempPath = Join-Path $directory (".tmp-" + [guid]::NewGuid().ToString("N") + ".feed")
    try {
        $bytes = [System.IO.File]::ReadAllBytes($Source)
        [System.IO.File]::WriteAllBytes($tempPath, $bytes)
        if (Test-Path -LiteralPath $Destination) {
            $backupPath = Join-Path $directory (".bak-" + [guid]::NewGuid().ToString("N") + ".feed")
            [System.IO.File]::Replace($tempPath, $Destination, $backupPath)
            if (Test-Path -LiteralPath $backupPath) { Remove-Item -LiteralPath $backupPath -Force -ErrorAction SilentlyContinue }
        }
        else {
            [System.IO.File]::Move($tempPath, $Destination)
        }
    }
    finally {
        if (Test-Path -LiteralPath $tempPath) { Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue }
    }
}

function Invoke-Node {
    param([string[]]$Arguments)
    $node = Get-Command node.exe -ErrorAction SilentlyContinue
    if ($null -eq $node) { Fail "Node.js ('node') was not found on PATH; the offline signer needs it." }
    $raw = & $node.Source @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($item in @($raw)) {
        if ($null -ne $item) { $lines.Add($item.ToString()) }
    }
    return [pscustomobject]@{
        ExitCode = $exitCode
        Output   = ($lines -join [Environment]::NewLine).Trim()
    }
}

function Invoke-NodeSigner {
    param([string[]]$Arguments)
    $signer = Join-Path $script:ScriptRoot "sign-manifest.mjs"
    if (-not (Test-Path -LiteralPath $signer)) { Fail "The signer was not found at $signer." }
    $fullArguments = New-Object System.Collections.Generic.List[string]
    $fullArguments.Add($signer)
    foreach ($argument in $Arguments) { $fullArguments.Add($argument) }
    return Invoke-Node -Arguments $fullArguments.ToArray()
}

# Reads the public keys embedded in the shipped verifier. A release may only be
# signed with one of them: any other key id would make every installed client
# reject the feed, so this is checked before the build and signing steps.
function Get-TrustedUpdateKeys {
    $path = Join-Path $script:RepoRoot "Modules\Updates\TrustedUpdateKeys.cs"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Fail ("The app trust store was not found: {0}" -f $path)
    }
    $text = [System.IO.File]::ReadAllText($path)
    $keys = @{}
    $found = [System.Text.RegularExpressions.Regex]::Matches($text, '\[\s*"([A-Za-z0-9._-]+)"\s*\]\s*=\s*"([A-Za-z0-9+/=]+)"')
    foreach ($item in $found) {
        $keys[$item.Groups[1].Value] = $item.Groups[2].Value
    }
    if ($keys.Count -eq 0) {
        Fail ("No trusted update keys were found in {0}." -f $path)
    }
    return [pscustomobject]@{ Path = $path; Keys = $keys }
}

function ConvertFrom-Base64Key {
    param([string]$Value, [string]$Description)
    try { return [Convert]::FromBase64String(($Value -replace '\s', '')) }
    catch { Fail ("{0} is not valid base64." -f $Description) }
}

function Assert-SamePublicKey {
    param([string]$Actual, [string]$Expected, [string]$Context)
    $actualBytes = ConvertFrom-Base64Key -Value $Actual -Description "The signing public key"
    $expectedBytes = ConvertFrom-Base64Key -Value $Expected -Description "The trusted public key"
    if ([System.BitConverter]::ToString($actualBytes) -ne [System.BitConverter]::ToString($expectedBytes)) {
        Fail ("Public key mismatch: {0}. The signing key must match the public key embedded in Modules/Updates/TrustedUpdateKeys.cs." -f $Context)
    }
}

# Derives the SPKI base64 public key from the PKCS#8 PEM private key so a missing
# or stale *.public.spki.b64 helper file can never be trusted by accident.
function Get-PublicKeyFromPrivateKey {
    param([string]$PrivateKeyPath)
    $derivationScript = @'
const { createPrivateKey, createPublicKey } = require('node:crypto');
const { readFileSync } = require('node:fs');
const key = createPrivateKey(readFileSync(process.argv[1], 'utf8'));
process.stdout.write(createPublicKey(key).export({ type: 'spki', format: 'der' }).toString('base64'));
'@
    $derived = Invoke-Node -Arguments @('-e', $derivationScript, $PrivateKeyPath)
    if ($derived.ExitCode -ne 0) {
        Fail ("Could not derive the public key from the private key {0}: {1}" -f $PrivateKeyPath, $derived.Output)
    }
    $value = ($derived.Output -replace '\s', '')
    if ($value -notmatch '^[A-Za-z0-9+/=]{40,}$') {
        Fail ("Deriving the public key from {0} produced an unexpected value." -f $PrivateKeyPath)
    }
    return $value
}

# Rejects feed content that no client could accept (or that is a placeholder):
# an exact non-zero ZIP SHA-256, a trusted key id and a 64-byte non-zero signature.
function Assert-PublishableEnvelope {
    param($Envelope, $TrustedKeys, [string]$ExpectedKeyId, [string]$ExpectedSha256)

    $payload = $Envelope.payload
    $signature = $Envelope.signature

    $assetSha = [string]$payload.asset.sha256
    if ($assetSha -cnotmatch '^[0-9a-f]{64}$') {
        Fail ("The feed asset.sha256 '{0}' is not 64 lowercase hex characters; the updater would reject this feed." -f $assetSha)
    }
    if ($assetSha -eq ('0' * 64)) {
        Fail "The feed asset.sha256 is all zeros (a placeholder); refusing to write a feed no client can verify."
    }
    if ($assetSha -ne $ExpectedSha256.ToLowerInvariant()) {
        Fail ("The feed asset.sha256 '{0}' does not match the exact ZIP SHA-256 '{1}'." -f $assetSha, $ExpectedSha256)
    }

    $signatureKeyId = [string]$signature.keyId
    if (-not $TrustedKeys.Keys.ContainsKey($signatureKeyId)) {
        Fail ("The feed signature keyId '{0}' is not in Modules/Updates/TrustedUpdateKeys.cs." -f $signatureKeyId)
    }
    if ($signatureKeyId -ne $ExpectedKeyId) {
        Fail ("The feed signature keyId '{0}' does not match -KeyId '{1}'." -f $signatureKeyId, $ExpectedKeyId)
    }

    $signatureBytes = ConvertFrom-Base64Key -Value ([string]$signature.value) -Description "The feed signature"
    if ($signatureBytes.Length -ne 64) {
        Fail ("The feed signature is {0} bytes, expected 64 (IEEE-P1363 P-256)." -f $signatureBytes.Length)
    }
    $nonZero = $false
    foreach ($byte in $signatureBytes) {
        if ($byte -ne 0) { $nonZero = $true; break }
    }
    if (-not $nonZero) {
        Fail "The feed signature is all zero bytes (a placeholder); refusing to write a feed no client can verify."
    }
}

# Verify the immutable ZIP against its own payload.json allow-list and the
# required release files. Returns the number of payload files.
function Test-ReleaseZip {
    param([string]$ZipPath, [string]$ExpectedVersion)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $entries = @{}
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            if ([string]::IsNullOrEmpty($name)) { continue }
            if ($name.EndsWith('/')) { continue }
            if ($name.Contains('..')) { Fail "ZIP entry looks unsafe: $name" }
            $key = $name.ToLowerInvariant()
            if ($entries.ContainsKey($key)) { Fail "ZIP contains a duplicate entry: $name" }
            $entries[$key] = $entry
        }

        foreach ($required in @("VRPC.exe", "payload.json", "vrpc-installer-ui.exe")) {
            $requiredKey = $required.ToLowerInvariant()
            if (-not $entries.ContainsKey($requiredKey)) { Fail "The ZIP is missing required file '$required'." }
        }

        $payloadEntry = $entries["payload.json"]
        $reader = New-Object System.IO.StreamReader($payloadEntry.Open(), (New-Object System.Text.UTF8Encoding($false)))
        try { $payloadText = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ([string]::IsNullOrWhiteSpace($payloadText)) { Fail "The ZIP's payload.json is empty." }
        $payload = $payloadText | ConvertFrom-Json

        if ($null -eq $payload) { Fail "The ZIP's payload.json could not be parsed." }
        if ($payload.schema -ne 1) { Fail ("The ZIP's payload.json has schema '$($payload.schema)', expected 1.") }
        if ($payload.product -ne "VRPC") { Fail ("The ZIP's payload.json has product '$($payload.product)', expected VRPC.") }
        if ($payload.version -ne $ExpectedVersion) { Fail ("The ZIP's payload.json version is '$($payload.version)', expected '$ExpectedVersion'.") }
        if ($payload.channel -ne $script:Channel) { Fail ("The ZIP's payload.json channel is '$($payload.channel)', expected '$($script:Channel)'.") }
        if ($payload.entryPoint -ne "VRPC.exe") { Fail ("The ZIP's payload.json entryPoint is '$($payload.entryPoint)', expected VRPC.exe.") }

        $manifestFiles = @($payload.files)
        if ($manifestFiles.Count -le 0) { Fail "The ZIP's payload.json lists no files." }

        $sha = [System.Security.Cryptography.SHA256]::Create()
        try {
            foreach ($file in $manifestFiles) {
                $relativePath = [string]$file.path
                if ([string]::IsNullOrWhiteSpace($relativePath)) { Fail "payload.json contains a file entry without a path." }
                $key = $relativePath.Replace('\', '/').ToLowerInvariant()
                if (-not $entries.ContainsKey($key)) { Fail "payload.json lists '$relativePath', but the ZIP does not contain it." }
                $entry = $entries[$key]
                if ([long]$entry.Length -ne [long]$file.size) {
                    Fail ("ZIP entry '{0}' is {1} bytes, payload.json says {2}." -f $relativePath, $entry.Length, $file.size)
                }
                $stream = $entry.Open()
                try { $hashBytes = $sha.ComputeHash($stream) } finally { $stream.Dispose() }
                $hashHex = ([System.BitConverter]::ToString($hashBytes) -replace "-", "").ToLowerInvariant()
                $expectedHash = ([string]$file.sha256).ToLowerInvariant()
                if ($hashHex -ne $expectedHash) {
                    Fail ("ZIP entry '{0}' has SHA-256 {1}, payload.json says {2}." -f $relativePath, $hashHex, $expectedHash)
                }
            }
        }
        finally { $sha.Dispose() }

        $expectedEntryCount = $manifestFiles.Count + 1
        if ($entries.Count -ne $expectedEntryCount) {
            $extra = @()
            foreach ($key in $entries.Keys) {
                if ($key -eq "payload.json") { continue }
                $listed = $false
                foreach ($file in $manifestFiles) {
                    if (([string]$file.path).Replace('\', '/').ToLowerInvariant() -eq $key) { $listed = $true; break }
                }
                if (-not $listed) { $extra += $entries[$key].FullName }
            }
            Fail ("The ZIP has {0} file entries, payload.json lists {1} ({2} unexpected: {3})." -f $entries.Count, ($expectedEntryCount - 1), $extra.Count, (($extra | Select-Object -First 5) -join ", "))
        }

        return [pscustomobject]@{
            FileCount = $manifestFiles.Count
            Payload   = $payload
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Get-HttpErrorDetail {
    param($ErrorRecord)
    $detail = ""
    $response = $null
    if ($null -ne $ErrorRecord.Exception -and $ErrorRecord.Exception -is [System.Net.WebException]) {
        $response = $ErrorRecord.Exception.Response
    }
    if ($null -ne $response) {
        $status = 0
        try { $status = [int]$response.StatusCode } catch { $status = 0 }
        $body = ""
        try {
            $stream = $response.GetResponseStream()
            if ($null -ne $stream) {
                $reader = New-Object System.IO.StreamReader($stream)
                try { $body = $reader.ReadToEnd() } finally { $reader.Dispose() }
            }
        }
        catch { }
        if ($body.Length -gt 600) { $body = $body.Substring(0, 600) }
        $detail = ("HTTP {0} {1}" -f $status, $body).Trim()
    }
    if ([string]::IsNullOrWhiteSpace($detail)) { $detail = $ErrorRecord.Exception.Message }
    return $detail
}

# GitHub REST request returning a result object instead of throwing, so callers can
# treat 404 (release does not exist yet) as a normal branch.
function Invoke-GitHubJson {
    param([string]$Method, [string]$Uri, [hashtable]$Headers, [object]$Body = $null)
    try {
        if ($null -eq $Body) {
            $data = Invoke-RestMethod -Method $Method -Uri $Uri -Headers $Headers -TimeoutSec 120
        }
        else {
            $json = $Body | ConvertTo-Json -Depth 6
            $data = Invoke-RestMethod -Method $Method -Uri $Uri -Headers $Headers -Body $json -ContentType "application/json; charset=utf-8" -TimeoutSec 120
        }
        return [pscustomobject]@{ Ok = $true; Status = 200; Data = $data; Detail = "" }
    }
    catch {
        $status = 0
        if ($null -ne $_.Exception.Response) {
            try { $status = [int]$_.Exception.Response.StatusCode } catch { $status = 0 }
        }
        return [pscustomobject]@{ Ok = $false; Status = $status; Data = $null; Detail = (Get-HttpErrorDetail $_) }
    }
}

function Invoke-GitHubUpload {
    param([string]$Uri, [hashtable]$Headers, [string]$Path)
    try {
        $data = Invoke-RestMethod -Method Post -Uri $Uri -Headers $Headers -ContentType "application/zip" -InFile $Path -TimeoutSec 1800
        return [pscustomobject]@{ Ok = $true; Data = $data; Detail = "" }
    }
    catch {
        return [pscustomobject]@{ Ok = $false; Data = $null; Detail = (Get-HttpErrorDetail $_) }
    }
}

function Invoke-DownloadWithRetry {
    param([string]$Uri, [string]$OutFile, [hashtable]$Headers = @{}, [int]$MaxAttempts = 8, [int]$DelaySeconds = 10)
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            $params = @{ Uri = $Uri; OutFile = $OutFile; UseBasicParsing = $true; TimeoutSec = 600 }
            if ($null -ne $Headers -and $Headers.Count -gt 0) { $params["Headers"] = $Headers }
            Invoke-WebRequest @params | Out-Null
            return $true
        }
        catch {
            if ($attempt -lt $MaxAttempts) { Start-Sleep -Seconds $DelaySeconds }
        }
    }
    return $false
}

function Assert-LocalAssetBytes {
    param([string]$Path, [string]$ExpectedSha, [long]$ExpectedSize)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { Fail ("The release asset file was not found: {0}" -f $Path) }
    $size = (Get-Item -LiteralPath $Path).Length
    $sha = Get-Sha256Hex $Path
    if ($size -ne $ExpectedSize) { Fail ("The release asset {0} is {1} bytes, expected {2}." -f $Path, $size, $ExpectedSize) }
    if ($sha -ne $ExpectedSha.ToLowerInvariant()) { Fail ("The release asset {0} SHA-256 is {1}, expected {2}." -f $Path, $sha, $ExpectedSha) }
}

function Assert-AssetBytes {
    param([string]$Uri, [hashtable]$Headers, [string]$ExpectedSha, [long]$ExpectedSize, [int]$MaxAttempts = 1)
    $tempPath = Join-Path ([System.IO.Path]::GetTempPath()) ("vrpc-asset-" + [guid]::NewGuid().ToString("N") + ".bin")
    try {
        $downloadHeaders = $Headers
        if ($null -eq $downloadHeaders) { $downloadHeaders = @{} }
        $ok = Invoke-DownloadWithRetry -Uri $Uri -OutFile $tempPath -Headers $downloadHeaders -MaxAttempts $MaxAttempts -DelaySeconds 10
        if (-not $ok) { Fail "Could not download the release asset from $Uri." }
        Assert-LocalAssetBytes -Path $tempPath -ExpectedSha $ExpectedSha -ExpectedSize $ExpectedSize
    }
    finally {
        if (Test-Path -LiteralPath $tempPath) { Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue }
    }
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

function Invoke-Main {

try {
    if ([string]::IsNullOrWhiteSpace($Version)) { Fail "-Version is required (strict x.y.z)." }
    if ($Version -notmatch $script:VersionPattern) { Fail "-Version '$Version' is not a strict numeric dotted version (x.y.z)." }
    if ([string]::IsNullOrWhiteSpace($KeyId)) { Fail "-KeyId is required." }
    if ($KeyId -notmatch '^[A-Za-z0-9._-]{3,64}$') { Fail "-KeyId '$KeyId' must be 3-64 characters of [A-Za-z0-9._-]." }
    if ([string]::IsNullOrWhiteSpace($KeyPath)) { Fail "-KeyPath is required." }
    if ($Runtime -ne "win-x64") { Fail "Only -Runtime win-x64 is supported by the stable feed (got '$Runtime')." }
    if ($DeployFeed -and -not $PublishGitHub) {
        Fail "-DeployFeed requires -PublishGitHub: the feed must never point at an asset that is not publicly downloadable."
    }
    if (-not $DryRun -and [string]::IsNullOrWhiteSpace($FeedPath)) {
        Fail "-FeedPath is required to write the signed feed: point it at the API repository's vrpc-update-manifest.json, or use -DryRun to only plan the release."
    }

    # The updater verifier only accepts assets under the official release URL, so
    # a custom -AssetBase is rejected unless it is that exact base.
    $expectedAssetBase = "https://github.com/$($script:Repo)/releases/download"
    if (-not [string]::IsNullOrWhiteSpace($AssetBase)) {
        $trimmedAssetBase = $AssetBase.TrimEnd('/')
        if ($trimmedAssetBase -cne $expectedAssetBase) {
            Fail ("-AssetBase '{0}' is not allowed. The updater only accepts asset URLs under '{1}', so a custom base would produce a feed the client rejects." -f $AssetBase, $expectedAssetBase)
        }
        $AssetBase = $trimmedAssetBase
    }

    $KeyPath = Resolve-UserPath -Path $KeyPath -BaseDir $script:Cwd
    if (-not (Test-Path -LiteralPath $KeyPath -PathType Leaf)) { Fail "The signing key was not found: $KeyPath" }

    # The chosen key must be one the shipped verifier trusts; otherwise every
    # installed client would reject the feed. Check before building or signing.
    $trustedKeys = Get-TrustedUpdateKeys
    if (-not $trustedKeys.Keys.ContainsKey($KeyId)) {
        $trustedKeyIds = (@($trustedKeys.Keys.Keys) | Sort-Object) -join ", "
        Fail ("-KeyId '{0}' is not in the app trust store (Modules/Updates/TrustedUpdateKeys.cs). Trusted key ids: {1}. Ship the new public key in a manually installed release before signing with it." -f $KeyId, $trustedKeyIds)
    }
    $trustedPublicKey = [string]$trustedKeys.Keys[$KeyId]
    Write-Step ("Trusted update key: '{0}' is listed in Modules/Updates/TrustedUpdateKeys.cs." -f $KeyId)

    # Derive the public key from the private key so a stale helper file cannot
    # hide a key/trust-store mismatch.
    $derivedPublicKey = Get-PublicKeyFromPrivateKey -PrivateKeyPath $KeyPath
    Assert-SamePublicKey -Actual $derivedPublicKey -Expected $trustedPublicKey -Context ("the -KeyPath private key does not match the trusted entry for '{0}'" -f $KeyId)

    $publicKeyPathExplicit = -not [string]::IsNullOrWhiteSpace($PublicKeyPath)
    if ($publicKeyPathExplicit) {
        $PublicKeyPath = Resolve-UserPath -Path $PublicKeyPath -BaseDir $script:Cwd
        if (-not (Test-Path -LiteralPath $PublicKeyPath -PathType Leaf)) { Fail "The public key was not found: $PublicKeyPath" }
        $filePublicKey = [System.IO.File]::ReadAllText($PublicKeyPath).Trim()
        if ([string]::IsNullOrWhiteSpace($filePublicKey)) { Fail "The public key file is empty: $PublicKeyPath" }
        Assert-SamePublicKey -Actual $filePublicKey -Expected $trustedPublicKey -Context ("the -PublicKeyPath file {0} does not match the trusted entry for '{1}'" -f $PublicKeyPath, $KeyId)
    }
    else {
        $defaultPublicKeyPath = Join-Path (Split-Path -Parent $KeyPath) ("$KeyId.public.spki.b64")
        if (Test-Path -LiteralPath $defaultPublicKeyPath -PathType Leaf) {
            $filePublicKey = [System.IO.File]::ReadAllText($defaultPublicKeyPath).Trim()
            if (-not [string]::IsNullOrWhiteSpace($filePublicKey)) {
                Assert-SamePublicKey -Actual $filePublicKey -Expected $trustedPublicKey -Context ("the public key file {0} does not match the trusted entry for '{1}'" -f $defaultPublicKeyPath, $KeyId)
            }
        }
    }
    $publicKeyBase64 = $trustedPublicKey
    Write-Step "The signing key derives the trusted public key."

    if ([string]::IsNullOrWhiteSpace($OutDir)) { $OutDir = Join-Path $script:RepoRoot "artifacts" }
    else { $OutDir = Resolve-UserPath -Path $OutDir -BaseDir $script:RepoRoot }
    if (-not (Test-Path -LiteralPath $OutDir -PathType Container)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

    $assetName = "VRPC-$Version-$Runtime.zip"
    $zipPath = Join-Path $OutDir $assetName
    if ([string]::IsNullOrWhiteSpace($EnvelopePath)) {
        $EnvelopePath = Join-Path $OutDir "VRPC-$Version-$Runtime.update-manifest.json"
    }
    else { $EnvelopePath = Resolve-UserPath -Path $EnvelopePath -BaseDir $script:Cwd }
    $canonicalPath = Join-Path $OutDir "VRPC-$Version-$Runtime.canonical.json"
    if ([string]::IsNullOrWhiteSpace($PlanPath)) {
        $PlanPath = Join-Path $OutDir "release-plan-$Version.json"
    }
    else { $PlanPath = Resolve-UserPath -Path $PlanPath -BaseDir $script:Cwd }
    if (-not [string]::IsNullOrWhiteSpace($FeedPath)) {
        $FeedPath = Resolve-UserPath -Path $FeedPath -BaseDir $script:Cwd
    }

    # 1. Version must already be the one the source tree reports.
    $propsPath = Join-Path $script:RepoRoot "Directory.Build.props"
    if (-not (Test-Path -LiteralPath $propsPath -PathType Leaf)) { Fail "Directory.Build.props was not found at $propsPath." }
    [xml]$props = Get-Content -LiteralPath $propsPath
    $versionNode = $props.SelectSingleNode("//Version")
    if ($null -eq $versionNode) { Fail "Directory.Build.props does not contain a Version element." }
    $propsVersion = $versionNode.InnerText.Trim()
    if ($propsVersion -ne $Version) {
        Fail ("Version mismatch: -Version $Version but Directory.Build.props says $propsVersion. Bump (and commit) Directory.Build.props before publishing the release.")
    }

    Write-Step "VRPC release publication"
    Write-Host ("  version : {0} ({1}, {2})" -f $Version, $Runtime, $script:Channel)
    Write-Host ("  key     : {0} ({1})" -f $KeyId, $KeyPath)
    Write-Host ("  out dir : {0}" -f $OutDir)

    # 2. Build (or reuse) the immutable ZIP.
    if (-not $SkipBuild) {
        $buildScript = Join-Path $script:ScriptRoot "build-release.ps1"
        if (-not (Test-Path -LiteralPath $buildScript -PathType Leaf)) { Fail "build-release.ps1 was not found at $buildScript." }
        Write-Step "Building the release package..."
        & $buildScript -Version $Version -Runtime $Runtime -OutputDirectory $OutDir
        if ($LASTEXITCODE -ne 0) { Fail ("build-release.ps1 failed with exit code {0}." -f $LASTEXITCODE) }
    }
    else {
        Write-Step "Skipping the build (-SkipBuild)."
    }

    if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf)) {
        # -SkipBuild means "reuse the package that already exists". A custom -OutDir
        # is usually a scratch directory for the envelope/plan, so fall back to the
        # standard artifacts location used by build-release.ps1.
        $fallbackZip = Join-Path (Join-Path $script:RepoRoot "artifacts") $assetName
        if ($SkipBuild -and (Test-Path -LiteralPath $fallbackZip -PathType Leaf)) {
            Write-Step ("Using the existing package from {0} (-SkipBuild)." -f $fallbackZip)
            $zipPath = $fallbackZip
        }
        else {
            Fail ("The release ZIP was not found: {0} (also checked {1}). Build it with build-release.ps1 or check -Version / -OutDir / -SkipBuild." -f $zipPath, $fallbackZip)
        }
    }
    $zipSize = (Get-Item -LiteralPath $zipPath).Length
    if ($zipSize -le 0) { Fail "The release ZIP is empty: $zipPath" }
    Write-Step "Hashing the exact ZIP bytes..."
    $zipSha = Get-Sha256Hex $zipPath

    # 3. The ZIP must match its own internal payload manifest.
    Write-Step "Verifying the ZIP contents against payload.json..."
    $zipInfo = Test-ReleaseZip -ZipPath $zipPath -ExpectedVersion $Version
    Write-Step ("ZIP OK: {0} payload files plus payload.json; VRPC.exe and vrpc-installer-ui.exe present." -f $zipInfo.FileCount)

    # 4. Sign the feed payload offline. The temporary envelope is only moved into
    #    place after it verifies, so a failed run leaves no half-written manifest.
    $tempEnvelope = Join-Path $OutDir (".tmp-" + [guid]::NewGuid().ToString("N") + ".envelope.json")
    try {
        $signArguments = New-Object System.Collections.Generic.List[string]
        foreach ($pair in @(
                @("--zip", $zipPath),
                @("--version", $Version),
                @("--channel", $script:Channel),
                @("--runtime", $Runtime),
                @("--key", $KeyPath),
                @("--key-id", $KeyId),
                @("--release-tag", ("v" + $Version)),
                @("--out", $tempEnvelope))) {
            $signArguments.Add($pair[0])
            $signArguments.Add($pair[1])
        }
        if (-not [string]::IsNullOrWhiteSpace($NotesUrl)) { $signArguments.Add("--notes-url"); $signArguments.Add($NotesUrl) }
        if (-not [string]::IsNullOrWhiteSpace($AssetBase)) { $signArguments.Add("--asset-base"); $signArguments.Add($AssetBase) }

        Write-Step "Signing the manifest payload (ECDSA P-256 / SHA-256)..."
        $signed = Invoke-NodeSigner -Arguments $signArguments.ToArray()
        if ($signed.ExitCode -ne 0) { Fail ("The signer failed (exit {0}):{1}{2}" -f $signed.ExitCode, [Environment]::NewLine, $signed.Output) }
        if (-not (Test-Path -LiteralPath $tempEnvelope -PathType Leaf)) { Fail "The signer did not produce an envelope." }

        # 5. Independently verify the envelope with the public key.
        Write-Step "Verifying the produced envelope with the public key..."
        $verify = Invoke-NodeSigner -Arguments @("--verify", $tempEnvelope, "--public-key", $publicKeyBase64, "--canonical-out", $canonicalPath)
        if ($verify.ExitCode -ne 0) { Fail ("The produced envelope did not verify:{0}{1}" -f [Environment]::NewLine, $verify.Output) }
        $canonicalSha = ""
        if ($verify.Output -match "sha256=([0-9a-f]{64})") { $canonicalSha = $Matches[1] }
        if ([string]::IsNullOrWhiteSpace($canonicalSha)) { Fail "Could not read the canonical SHA-256 from the verifier output." }
        if ((Get-Sha256Hex $canonicalPath) -ne $canonicalSha) { Fail "The canonical bytes written by the verifier do not match the verified SHA-256." }

        $envelope = [System.IO.File]::ReadAllText($tempEnvelope) | ConvertFrom-Json
        if ($null -eq $envelope -or $null -eq $envelope.payload -or $null -eq $envelope.signature) {
            Fail "The produced envelope is missing 'payload' or 'signature'."
        }
        $payload = $envelope.payload
        $signature = $envelope.signature

        if ($envelope.schema -ne 1) { Fail ("Envelope schema is '$($envelope.schema)', expected 1.") }
        if ($payload.product -ne "VRPC") { Fail "Envelope payload product is not VRPC." }
        if ($payload.version -ne $Version) { Fail ("Envelope payload version is '{0}', expected '{1}'." -f $payload.version, $Version) }
        if ($payload.channel -ne $script:Channel) { Fail "Envelope payload channel is not stable." }
        if ($payload.runtime -ne $Runtime) { Fail "Envelope payload runtime is not win-x64." }
        if ($payload.releaseTag -ne ("v" + $Version)) { Fail ("Envelope payload releaseTag is '{0}', expected 'v{1}'." -f $payload.releaseTag, $Version) }
        if ($payload.asset.name -ne $assetName) { Fail ("Envelope asset name is '{0}', expected '{1}'." -f $payload.asset.name, $assetName) }
        if ([long]$payload.asset.size -ne $zipSize) { Fail ("Envelope asset size is {0}, expected {1}." -f $payload.asset.size, $zipSize) }
        if (([string]$payload.asset.sha256).ToLowerInvariant() -ne $zipSha) { Fail ("Envelope asset sha256 is '{0}', expected '{1}'." -f $payload.asset.sha256, $zipSha) }
        $expectedAssetUrl = "$expectedAssetBase/v$Version/$assetName"
        if ($payload.asset.url -ne $expectedAssetUrl) { Fail ("Envelope asset URL is '{0}', expected '{1}'." -f $payload.asset.url, $expectedAssetUrl) }
        if ($payload.notesUrl -notlike "https://*") { Fail ("Envelope notesUrl '{0}' is not HTTPS." -f $payload.notesUrl) }
        if ($payload.publishedAtUtc -notmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$') { Fail ("Envelope publishedAtUtc '{0}' is not a UTC yyyy-MM-ddTHH:mm:ssZ timestamp." -f $payload.publishedAtUtc) }
        if ($signature.algorithm -ne "ECDSA-P256-SHA256") { Fail ("Envelope signature algorithm is '{0}', expected ECDSA-P256-SHA256." -f $signature.algorithm) }
        if ($signature.keyId -ne $KeyId) { Fail ("Envelope signature keyId is '{0}', expected '{1}'." -f $signature.keyId, $KeyId) }
        if ([string]::IsNullOrWhiteSpace($signature.value)) { Fail "Envelope signature value is empty." }
        $signatureBytes = $null
        try { $signatureBytes = [Convert]::FromBase64String($signature.value) } catch { Fail "Envelope signature value is not valid base64." }
        if ($signatureBytes.Length -ne 64) { Fail ("Envelope signature is {0} bytes, expected 64 (IEEE-P1363 P-256)." -f $signatureBytes.Length) }

        # A placeholder (zero hash / zero signature / unknown key) must never reach
        # the API repository, in a dry run or a real release.
        Assert-PublishableEnvelope -Envelope $envelope -TrustedKeys $trustedKeys -ExpectedKeyId $KeyId -ExpectedSha256 $zipSha
        Write-Step ("Feed content guard passed: exact ZIP SHA-256, trusted keyId '{0}', 64-byte non-zero signature." -f $KeyId)

        if (Test-Path -LiteralPath $EnvelopePath) {
            $envelopeBackup = Join-Path $OutDir (".bak-" + [guid]::NewGuid().ToString("N") + ".json")
            [System.IO.File]::Replace($tempEnvelope, $EnvelopePath, $envelopeBackup)
            if (Test-Path -LiteralPath $envelopeBackup) { Remove-Item -LiteralPath $envelopeBackup -Force -ErrorAction SilentlyContinue }
        }
        else { [System.IO.File]::Move($tempEnvelope, $EnvelopePath) }
        $tempEnvelope = $null
        Write-Step "Signed envelope: $EnvelopePath"
    }
    finally {
        if (-not [string]::IsNullOrEmpty($tempEnvelope) -and (Test-Path -LiteralPath $tempEnvelope)) {
            Remove-Item -LiteralPath $tempEnvelope -Force -ErrorAction SilentlyContinue
        }
    }

    # 6. Release plan (written before any external side effect, so a dry run has it too).
    $targetCommit = $TargetCommitish
    if ([string]::IsNullOrWhiteSpace($targetCommit)) {
        $git = Get-Command git.exe -ErrorAction SilentlyContinue
        if ($null -ne $git) {
            $head = & $git.Source -C $script:RepoRoot rev-parse HEAD 2>&1
            if ($LASTEXITCODE -eq 0) { $targetCommit = (($head | Out-String).Trim().Split("`n")[0]).Trim() }
        }
    }

    $githubAction = "skipped"
    if ($PublishGitHub) { $githubAction = "planned" }
    $feedAction = "write"
    if ($DeployFeed) { $feedAction = "deploy" }
    if ($DryRun) { $feedAction = "planned" }

    $plan = [ordered]@{
        schema        = 1
        createdAtUtc  = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
        dryRun        = $DryRun.IsPresent
        version       = $Version
        runtime       = $Runtime
        channel       = $script:Channel
        releaseTag    = "v$Version"
        keyId         = $KeyId
        targetCommit  = $targetCommit
        asset         = [ordered]@{
            name    = $assetName
            url     = $payload.asset.url
            size    = $zipSize
            sha256  = $zipSha
            zipPath = $zipPath
        }
        notesUrl      = $payload.notesUrl
        publishedAtUtc = $payload.publishedAtUtc
        payloadFiles  = $zipInfo.FileCount
        envelope      = [ordered]@{
            path            = $EnvelopePath
            sha256          = (Get-Sha256Hex $EnvelopePath)
            canonicalPath   = $canonicalPath
            canonicalSha256 = $canonicalSha
            signatureBytes  = $signatureBytes.Length
        }
        github        = [ordered]@{
            repo    = $script:Repo
            action  = $githubAction
            draft   = $true
            releaseUrl = "https://github.com/$($script:Repo)/releases/tag/v$Version"
        }
        feed          = [ordered]@{
            path                   = $FeedPath
            action                 = $feedAction
            allowUnpublishedFeed   = $AllowUnpublishedFeed.IsPresent
        }
    }
    Write-TextAtomic -Path $PlanPath -Text ($plan | ConvertTo-Json -Depth 10)
    Write-Step "Release plan: $PlanPath"

    if ($DryRun) {
        Write-Host ""
        Write-Step "Dry run complete: nothing was uploaded, published, committed, or deployed."
        Write-Host ("  version   : {0} ({1}, {2})" -f $Version, $Runtime, $script:Channel)
        Write-Host ("  zip       : {0}" -f $zipPath)
        Write-Host ("  size      : {0} bytes" -f $zipSize)
        Write-Host ("  sha256    : {0}" -f $zipSha)
        Write-Host ("  asset url : {0}" -f $payload.asset.url)
        Write-Host ("  notes url : {0}" -f $payload.notesUrl)
        Write-Host ("  published : {0}" -f $payload.publishedAtUtc)
        Write-Host ("  signature : {0}, keyId={1}, {2} bytes" -f $signature.algorithm, $signature.keyId, $signatureBytes.Length)
        Write-Host ("  key trust : keyId {0} is embedded in Modules/Updates/TrustedUpdateKeys.cs" -f $KeyId)
        Write-Host ("  canonical : sha256={0}" -f $canonicalSha)
        Write-Host ("  envelope  : {0}" -f $EnvelopePath)
        $feedDisplay = if ([string]::IsNullOrWhiteSpace($FeedPath)) { "<not specified; pass -FeedPath to write>" } else { $FeedPath }
        Write-Host ("  feed      : {0} (planned)" -f $feedDisplay)
        if ($PublishGitHub) {
            Write-Host ("  github    : would create/reuse draft release v{0} at {1}, publish it, and verify the public asset" -f $Version, $targetCommit)
        }
        if ($DeployFeed) { Write-Host "  feed git  : would commit and push the manifest on main after a successful publish" }
        exit 0
    }

    # 7. GitHub release publication (only after everything above verified).
    $releaseUrl = "https://github.com/$($script:Repo)/releases/tag/v$Version"
    $publicAssetUrl = $payload.asset.url
    if ($PublishGitHub) {
        if ([string]::IsNullOrWhiteSpace($targetCommit)) {
            Fail "Could not determine the commit for the release; pass -TargetCommitish explicitly."
        }
        if ($targetCommit -notmatch '^[0-9a-fA-F]{7,40}$') {
            Fail "-TargetCommitish '$targetCommit' is not a git commit SHA."
        }
        [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12

        $token = $GitHubToken
        if ([string]::IsNullOrWhiteSpace($token)) { $token = $env:GH_TOKEN }
        if ([string]::IsNullOrWhiteSpace($token) -and -not $UseGhCli.IsPresent) {
            Fail "GitHub publication needs -GitHubToken, the GH_TOKEN environment variable, or -UseGhCli with an authenticated gh CLI."
        }
        if (-not [string]::IsNullOrWhiteSpace($token) -and $UseGhCli.IsPresent) {
            Write-Step "A GitHub token was supplied, so the REST path is used and -UseGhCli is ignored."
        }

        if (-not [string]::IsNullOrWhiteSpace($token)) {
            $githubResult = Publish-GitHubRelease -ZipPath $zipPath -AssetName $assetName -Commitish $targetCommit -Token $token
        }
        else {
            $githubResult = Publish-GitHubReleaseWithCli -ZipPath $zipPath -AssetName $assetName -Commitish $targetCommit
        }

        $releaseUrl = $githubResult.ReleaseUrl
        $publicAssetUrl = $githubResult.AssetUrl
        $plan.github.action = "published"
        $plan.github.draft = $false
        $plan.github.releaseUrl = $releaseUrl
        $plan.github.assetUrl = $publicAssetUrl
        $plan.github.assetVerified = $true
        if ($githubResult.AlreadyPublished) { $plan.github.alreadyPublished = $true }
        Write-Step ("GitHub release published and verified: {0}" -f $releaseUrl)
    }

    # 8. Feed manifest: only after the asset is public and byte-verified.
    if (-not $PublishGitHub -and -not $AllowUnpublishedFeed) {
        Fail "Refusing to write the feed without -PublishGitHub: the manifest would point at an asset that is not publicly downloadable. Pass -PublishGitHub for a real release, or -AllowUnpublishedFeed for a deliberate staging/local manifest."
    }
    if (-not $PublishGitHub) {
        Write-Host ("[publish-update] WARNING: -AllowUnpublishedFeed is set, so the feed points at an asset")
        Write-Host "[publish-update]          that is not publicly downloadable. Do not deploy this feed."
    }
    $feedDirectory = Split-Path -Parent $FeedPath
    if (-not (Test-Path -LiteralPath $feedDirectory -PathType Container)) {
        Fail ("The feed directory does not exist: {0}. Check -FeedPath or check out the website/API repository." -f $feedDirectory)
    }
    Copy-FileAtomic -Source $EnvelopePath -Destination $FeedPath
    if ((Get-Sha256Hex $FeedPath) -ne (Get-Sha256Hex $EnvelopePath)) { Fail "The written feed file does not match the verified envelope." }
    $feedBytes = [System.IO.File]::ReadAllBytes($FeedPath)
    if ($feedBytes.Length -ge 3 -and $feedBytes[0] -eq 0xEF -and $feedBytes[1] -eq 0xBB -and $feedBytes[2] -eq 0xBF) {
        Fail "The feed file was written with a UTF-8 BOM; it must be BOM-less JSON."
    }
    $feedVerify = Invoke-NodeSigner -Arguments @("--verify", $FeedPath, "--public-key", $publicKeyBase64)
    if ($feedVerify.ExitCode -ne 0) { Fail ("The feed file failed signature verification after being written:{0}{1}" -f [Environment]::NewLine, $feedVerify.Output) }
    Write-Step "Feed manifest written: $FeedPath"
    $plan.feed.action = "written"
    $plan.feed.sha256 = (Get-Sha256Hex $FeedPath)

    if ($DeployFeed) {
        Deploy-Feed -FeedFilePath $FeedPath
        $plan.feed.action = "deployed"
        $plan.feed.deployed = $true
    }

    $plan.result = [ordered]@{
        success        = $true
        completedAtUtc = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
    }
    Write-TextAtomic -Path $PlanPath -Text ($plan | ConvertTo-Json -Depth 10)

    Write-Host ""
    Write-Step "Release complete."
    Write-Host ("  version : {0}" -f $Version)
    Write-Host ("  asset   : {0} ({1} bytes, sha256={2})" -f $assetName, $zipSize, $zipSha)
    Write-Host ("  envelope: {0}" -f $EnvelopePath)
    Write-Host ("  feed    : {0}" -f $FeedPath)
    Write-Host ("  plan    : {0}" -f $PlanPath)
    if ($PublishGitHub) { Write-Host ("  github  : {0}" -f $releaseUrl) }
    exit 0
}
catch {
    [Console]::Error.WriteLine("[publish-update] ERROR: " + $_.Exception.Message)
    exit 1
}

}

# ---------------------------------------------------------------------------
# GitHub release helpers (defined after main for readability; PowerShell resolves
# them at call time).
# ---------------------------------------------------------------------------

function Publish-GitHubRelease {
    param([string]$ZipPath, [string]$AssetName, [string]$Commitish, [string]$Token)

    $headers = @{
        Authorization          = "Bearer $Token"
        Accept                 = "application/vnd.github+json"
        "X-GitHub-Api-Version" = "2022-11-28"
        "User-Agent"           = "VRPC-publish-update"
    }
    $tag = "v$Version"
    $releaseUri = "$($script:ApiBase)/repos/$($script:Repo)/releases/tags/$tag"
    $expectedSize = [long](Get-Item -LiteralPath $ZipPath).Length
    $expectedSha = Get-Sha256Hex $ZipPath

    $lookup = Invoke-GitHubJson -Method GET -Uri $releaseUri -Headers $headers
    $release = $null
    if ($lookup.Ok) {
        $release = $lookup.Data
        Write-Step ("Using the existing GitHub release {0} (draft={1})." -f $tag, $release.draft)
    }
    elseif ($lookup.Status -eq 404) {
        Write-Step ("Creating the draft GitHub release {0} at commit {1}..." -f $tag, $Commitish)
        $created = Invoke-GitHubJson -Method POST -Uri "$($script:ApiBase)/repos/$($script:Repo)/releases" -Headers $headers -Body @{
            tag_name         = $tag
            target_commitish = $Commitish
            name             = "VRPC $Version"
            body             = "VRPC $Version"
            draft            = $true
            prerelease       = $false
        }
        if (-not $created.Ok) { Fail ("Could not create the GitHub release: " + $created.Detail) }
        $release = $created.Data
    }
    else {
        Fail ("Could not look up the GitHub release {0}: {1}" -f $tag, $lookup.Detail)
    }

    if ($release.tag_name -ne $tag) { Fail ("The GitHub release returned tag_name '{0}', expected '{1}'." -f $release.tag_name, $tag) }
    $releaseId = $release.id
    $wasDraft = [bool]$release.draft

    $asset = $null
    foreach ($item in @($release.assets)) {
        if ($null -ne $item -and $item.name -eq $AssetName) { $asset = $item; break }
    }

    if ($null -ne $asset) {
        if ($wasDraft) {
            Write-Step ("Verifying the bytes of the existing draft asset {0}..." -f $AssetName)
            $assetHeaders = @{
                Authorization = "Bearer $Token"
                Accept        = "application/octet-stream"
                "User-Agent"  = "VRPC-publish-update"
            }
            Assert-AssetBytes -Uri $asset.url -Headers $assetHeaders -ExpectedSha $expectedSha -ExpectedSize $expectedSize
        }
        else {
            # Re-run after the release was already published: the shared public
            # verification below decides whether the published bytes still match.
            Write-Step ("The release {0} is already published; checking the public asset below." -f $tag)
        }
    }
    else {
        if (-not $wasDraft) {
            Fail ("The published release {0} does not contain the asset '{1}'; refusing to modify a published release. Bump the version." -f $tag, $AssetName)
        }
        $uploadBase = ([string]$release.upload_url) -replace '\{.*$', ''
        if ([string]::IsNullOrWhiteSpace($uploadBase)) { Fail "The GitHub release did not include an upload_url." }
        $uploadUri = $uploadBase + "?name=" + [System.Uri]::EscapeDataString($AssetName)
        Write-Step ("Uploading {0} ({1:N0} bytes)..." -f $AssetName, $expectedSize)
        $uploaded = Invoke-GitHubUpload -Uri $uploadUri -Headers $headers -Path $ZipPath
        if (-not $uploaded.Ok) { Fail ("The GitHub asset upload failed: " + $uploaded.Detail) }
        if ([string]$uploaded.Data.name -ne $AssetName) { Fail ("GitHub stored the asset as '{0}', expected '{1}'." -f $uploaded.Data.name, $AssetName) }
        if ([long]$uploaded.Data.size -ne $expectedSize) { Fail ("GitHub reports the uploaded asset as {0} bytes, expected {1}." -f $uploaded.Data.size, $expectedSize) }
        $asset = $uploaded.Data

        Write-Step "Verifying the uploaded draft asset bytes before publishing..."
        $assetHeaders = @{
            Authorization = "Bearer $Token"
            Accept        = "application/octet-stream"
            "User-Agent"  = "VRPC-publish-update"
        }
        Assert-AssetBytes -Uri $asset.url -Headers $assetHeaders -ExpectedSha $expectedSha -ExpectedSize $expectedSize -MaxAttempts 7
    }

    if ($wasDraft) {
        Write-Step "Publishing the GitHub release..."
        $published = Invoke-GitHubJson -Method PATCH -Uri "$($script:ApiBase)/repos/$($script:Repo)/releases/$releaseId" -Headers $headers -Body @{ draft = $false }
        if (-not $published.Ok) { Fail ("Could not publish the GitHub release: " + $published.Detail) }
        Write-Step "Release published; verifying the public download..."
    }

    $final = Invoke-GitHubJson -Method GET -Uri $releaseUri -Headers $headers
    if (-not $final.Ok) { Fail ("Could not read back the published GitHub release: " + $final.Detail) }
    if ([bool]$final.Data.draft) { Fail "The GitHub release is still a draft after publishing." }
    $publicAsset = $null
    foreach ($item in @($final.Data.assets)) {
        if ($null -ne $item -and $item.name -eq $AssetName) { $publicAsset = $item; break }
    }
    if ($null -eq $publicAsset) { Fail ("The published GitHub release does not list the asset '{0}'." -f $AssetName) }
    $publicUrl = [string]$publicAsset.browser_download_url
    if ($publicUrl -notlike "https://*") { Fail ("The public asset URL '{0}' is not HTTPS." -f $publicUrl) }
    Assert-AssetBytes -Uri $publicUrl -Headers @{} -ExpectedSha $expectedSha -ExpectedSize $expectedSize -MaxAttempts 10

    return [pscustomobject]@{
        ReleaseUrl       = [string]$final.Data.html_url
        AssetUrl         = $publicUrl
        AlreadyPublished = (-not $wasDraft)
    }
}

function Publish-GitHubReleaseWithCli {
    param([string]$ZipPath, [string]$AssetName, [string]$Commitish)

    $gh = Get-Command gh.exe -ErrorAction SilentlyContinue
    if ($null -eq $gh) {
        Fail "gh was not found on PATH. Install it (winget install GitHub.cli) and run 'gh auth login', or pass -GitHubToken / set GH_TOKEN."
    }
    $tag = "v$Version"
    $expectedSize = [long](Get-Item -LiteralPath $ZipPath).Length
    $expectedSha = Get-Sha256Hex $ZipPath

    $viewJson = & $gh.Source release view $tag --repo $script:Repo --json isDraft,assets,url 2>&1
    $viewExit = $LASTEXITCODE
    $view = $null
    if ($viewExit -eq 0) {
        $viewText = ($viewJson | Out-String).Trim()
        if (-not [string]::IsNullOrWhiteSpace($viewText)) { $view = $viewText | ConvertFrom-Json }
        Write-Step ("Using the existing GitHub release {0} (draft={1})." -f $tag, $view.isDraft)
    }
    else {
        Write-Step ("Creating the draft GitHub release {0} at commit {1}..." -f $tag, $Commitish)
        & $gh.Source release create $tag $ZipPath --repo $script:Repo --title "VRPC $Version" --notes "VRPC $Version" --draft --target $Commitish
        if ($LASTEXITCODE -ne 0) { Fail "gh release create failed." }
    }

    $wasDraft = $true
    if ($null -ne $view) { $wasDraft = [bool]$view.isDraft }

    $hasAsset = $false
    if ($null -ne $view) {
        foreach ($item in @($view.assets)) {
            if ($null -ne $item -and $item.name -eq $AssetName) { $hasAsset = $true; break }
        }
    }

    if ($hasAsset) {
        if ($wasDraft) {
            $tempDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("vrpc-gh-" + [guid]::NewGuid().ToString("N"))
            New-Item -ItemType Directory -Path $tempDirectory -Force | Out-Null
            try {
                & $gh.Source release download $tag --repo $script:Repo --pattern $AssetName --dir $tempDirectory
                if ($LASTEXITCODE -ne 0) { Fail "gh release download failed for the existing draft asset." }
                $downloadPath = Join-Path $tempDirectory $AssetName
                if (-not (Test-Path -LiteralPath $downloadPath -PathType Leaf)) { Fail "gh did not download the draft asset." }
                Assert-LocalAssetBytes -Path $downloadPath -ExpectedSha $expectedSha -ExpectedSize $expectedSize
            }
            finally {
                if (Test-Path -LiteralPath $tempDirectory) { Remove-Item -LiteralPath $tempDirectory -Recurse -Force -ErrorAction SilentlyContinue }
            }
        }
        else {
            # Re-run after the release was already published: the public verification
            # below decides whether the published bytes still match.
            Write-Step ("The release {0} is already published; checking the public asset below." -f $tag)
        }
    }
    elseif (-not $wasDraft) {
        Fail ("The published release {0} does not contain the asset '{1}'; refusing to modify a published release. Bump the version." -f $tag, $AssetName)
    }
    else {
        Write-Step ("Uploading {0} with gh..." -f $AssetName)
        & $gh.Source release upload $tag $ZipPath --repo $script:Repo
        if ($LASTEXITCODE -ne 0) { Fail "gh release upload failed." }

        # The REST path verifies the hosted draft bytes after upload; the gh path
        # must enforce the same gate before the release becomes public.
        Write-Step "Verifying the uploaded draft asset bytes before publishing..."
        $tempDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("vrpc-gh-" + [guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Path $tempDirectory -Force | Out-Null
        try {
            & $gh.Source release download $tag --repo $script:Repo --pattern $AssetName --dir $tempDirectory
            if ($LASTEXITCODE -ne 0) { Fail "gh release download failed for the uploaded draft asset." }
            $downloadPath = Join-Path $tempDirectory $AssetName
            if (-not (Test-Path -LiteralPath $downloadPath -PathType Leaf)) { Fail "gh did not download the uploaded draft asset." }
            Assert-LocalAssetBytes -Path $downloadPath -ExpectedSha $expectedSha -ExpectedSize $expectedSize
        }
        finally {
            if (Test-Path -LiteralPath $tempDirectory) { Remove-Item -LiteralPath $tempDirectory -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }

    if ($wasDraft) {
        Write-Step "Publishing the GitHub release..."
        & $gh.Source release edit $tag --repo $script:Repo --draft=false
        if ($LASTEXITCODE -ne 0) { Fail "gh release edit --draft=false failed." }
    }

    $publicUrl = "https://github.com/$($script:Repo)/releases/download/$tag/$AssetName"
    Assert-AssetBytes -Uri $publicUrl -Headers @{} -ExpectedSha $expectedSha -ExpectedSize $expectedSize -MaxAttempts 10
    return [pscustomobject]@{
        ReleaseUrl       = "https://github.com/$($script:Repo)/releases/tag/$tag"
        AssetUrl         = $publicUrl
        AlreadyPublished = (-not $wasDraft)
    }
}

function Deploy-Feed {
    param([string]$FeedFilePath)

    $directory = Split-Path -Parent $FeedFilePath
    $gitRepo = $directory
    while (-not (Test-Path -LiteralPath (Join-Path $gitRepo ".git") -PathType Container)) {
        $parent = Split-Path -Parent $gitRepo
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent -eq $gitRepo) {
            Fail ("Could not find the git repository that contains {0}." -f $FeedFilePath)
        }
        $gitRepo = $parent
    }
    $gitRepo = [System.IO.Path]::GetFullPath($gitRepo)
    if ($gitRepo -eq [System.IO.Path]::GetFullPath($script:RepoRoot)) {
        Fail "-DeployFeed resolved to the VRPC app repository; the feed belongs to the website/API repository."
    }

    $relative = [System.IO.Path]::GetFullPath($FeedFilePath).Substring($gitRepo.Length).TrimStart('\', '/').Replace('\', '/')
    $branch = (& git -C $gitRepo branch --show-current 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { Fail "git branch failed in $gitRepo." }
    if ($branch -ne "main") {
        Fail ("The feed repository is on branch '{0}'; deployment expects 'main' because a push to main triggers the API deploy." -f $branch)
    }

    $status = (& git -C $gitRepo status --porcelain -- $relative 2>&1 | Out-String).Trim()
    if ([string]::IsNullOrWhiteSpace($status)) {
        Write-Step "The feed manifest is unchanged in git; nothing to commit."
    }
    else {
        & git -C $gitRepo add -- $relative
        if ($LASTEXITCODE -ne 0) { Fail "git add failed in $gitRepo." }
        & git -C $gitRepo commit -m "Publish VRPC $Version update manifest" -- $relative
        if ($LASTEXITCODE -ne 0) { Fail "git commit failed in $gitRepo." }
        Write-Step ("Committed the feed manifest in {0}." -f $gitRepo)
    }

    & git -C $gitRepo push origin main
    if ($LASTEXITCODE -ne 0) { Fail "git push failed; the manifest is committed locally but not deployed." }
    Write-Step "Pushed to origin/main; the API deploy workflow publishes the feed (cache TTL is about 5 minutes)."
}

if ($SelfTest) {
    $tempPath = Join-Path ([System.IO.Path]::GetTempPath()) ("vrpc-selftest-" + [guid]::NewGuid().ToString("N") + ".bin")
    try {
        [System.IO.File]::WriteAllBytes($tempPath, [byte[]](1, 2, 3, 4))
        $sha = Get-Sha256Hex $tempPath
        Assert-LocalAssetBytes -Path $tempPath -ExpectedSha $sha -ExpectedSize 4

        $failed = $false
        try { Assert-LocalAssetBytes -Path $tempPath -ExpectedSha ("0" * 64) -ExpectedSize 4 } catch { $failed = $true }
        if (-not $failed) { throw "The asset SHA-256 mismatch was accepted." }

        $failed = $false
        try { Assert-LocalAssetBytes -Path $tempPath -ExpectedSha $sha -ExpectedSize 5 } catch { $failed = $true }
        if (-not $failed) { throw "The asset size mismatch was accepted." }

        Write-Step "Asset byte verification self-test passed (hash and size mismatches rejected)."
        exit 0
    }
    finally {
        if (Test-Path -LiteralPath $tempPath) { Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue }
    }
}

Invoke-Main


# Build tools write progress to stderr; success is decided above, so return a deliberate code.
exit 0
