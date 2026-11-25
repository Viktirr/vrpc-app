# Serves a signed VRPC update manifest on localhost for updater rehearsal.
#
# It mimics the production feed contract closely enough to exercise a real client:
#   GET /products/vrpc/update-manifest  -> 200 application/json, ETag, Cache-Control
#   GET with If-None-Match     -> 304 with no body
#   anything else              -> 404 JSON
#
# The platform HTTP listener needs a URL reservation, so this uses a raw TCP
# listener bound to 127.0.0.1 only. The manifest is re-read on every request, so
# replacing the file takes effect without restarting the server.

<#
.SYNOPSIS
    Serves a signed VRPC update manifest from http://127.0.0.1:<port>/products/vrpc/update-manifest.

.DESCRIPTION
    A local rehearsal helper for the auto-updater. It implements the read-only
    subset of the API feed:

      - GET /products/vrpc/update-manifest returns the manifest file bytes with
        Content-Type: application/json, a strong ETag (SHA-256 of the bytes) and
        Cache-Control: public, max-age=300.
      - A matching If-None-Match header returns 304 with the ETag and no body.
      - HEAD returns the same headers without a body.
      - Unknown paths return 404 with a JSON error body; other methods return 405.

    The server exits when the process is stopped (Ctrl+C), after -MaxRequests
    requests when that is greater than zero, or after one request with -Once.
    No network access beyond the loopback interface.

.PARAMETER ManifestPath
    Signed envelope JSON to serve. Re-read on every request.

.PARAMETER Port
    TCP port to listen on. Default 8765. Use 0 to pick a free port; the URL is
    written to -ReadyFile and printed at startup.

.PARAMETER BindAddress
    IPv4 address to bind to. Default 127.0.0.1. Do not expose this to a network.

.PARAMETER ReadyFile
    Optional file to create once the listener is accepting connections. It
    contains the listen URL; useful for automated tests. Removed on exit.

.PARAMETER MaxRequests
    Stop after this many requests. Default 0 (serve until stopped).

.PARAMETER Once
    Stop after the first request.

.PARAMETER Quiet
    Do not log individual requests (startup and errors are still printed).

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File packaging\serve-feed.ps1 `
        -ManifestPath artifacts\VRPC-1.2.0-win-x64.update-manifest.json -Port 8765

.EXAMPLE
    Invoke-WebRequest http://127.0.0.1:8765/products/vrpc/update-manifest -UseBasicParsing
#>

[CmdletBinding()]
param(
    [string]$ManifestPath = "",
    [int]$Port = 8765,
    [string]$BindAddress = "127.0.0.1",
    [string]$ReadyFile = "",
    [int]$MaxRequests = 0,
    [switch]$Once,
    [switch]$Quiet
)

$ErrorActionPreference = "Stop"
$script:Cwd = (Get-Location).Path

function Fail {
    param([string]$Message)
    throw $Message
}

function Get-BytesSha256Hex {
    param([byte[]]$Bytes)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { $hash = $sha.ComputeHash($Bytes) } finally { $sha.Dispose() }
    return ([System.BitConverter]::ToString($hash) -replace "-", "").ToLowerInvariant()
}

function Get-Utf8JsonBytes {
    param([string]$Text)
    return (New-Object System.Text.UTF8Encoding($false)).GetBytes($Text)
}

function Send-Response {
    param(
        [System.IO.Stream]$Stream,
        [int]$Status,
        [string]$Reason,
        [System.Collections.Specialized.OrderedDictionary]$Headers,
        [byte[]]$Body = $null,
        [switch]$HeadOnly
    )
    $builder = New-Object System.Text.StringBuilder
    [void]$builder.Append(("HTTP/1.1 {0} {1}" -f $Status, $Reason) + "`r`n")
    foreach ($name in $Headers.Keys) {
        [void]$builder.Append(("{0}: {1}" -f $name, $Headers[$name]) + "`r`n")
    }
    if ($null -ne $Body) {
        [void]$builder.Append(("Content-Length: {0}" -f $Body.Length) + "`r`n")
    }
    [void]$builder.Append("Connection: close`r`n`r`n")
    $headerBytes = [System.Text.Encoding]::ASCII.GetBytes($builder.ToString())
    $Stream.Write($headerBytes, 0, $headerBytes.Length)
    if (-not $HeadOnly -and $null -ne $Body -and $Body.Length -gt 0) {
        $Stream.Write($Body, 0, $Body.Length)
    }
    $Stream.Flush()
}

function Get-IfNoneMatchMatches {
    param([string]$HeaderValue, [string]$ETag)
    if ([string]::IsNullOrWhiteSpace($HeaderValue)) { return $false }
    foreach ($token in $HeaderValue.Split(",")) {
        $candidate = $token.Trim()
        if ($candidate -eq "*") { return $true }
        if ($candidate.StartsWith("W/")) { $candidate = $candidate.Substring(2).Trim() }
        if ($candidate -eq $ETag) { return $true }
    }
    return $false
}

function Handle-Client {
    param([System.Net.Sockets.TcpClient]$Client)
    $stream = $Client.GetStream()
    $stream.ReadTimeout = 5000
    $stream.WriteTimeout = 10000
    $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::ASCII, $false, 1024, $true)
    try {
        $requestLine = $null
        try { $requestLine = $reader.ReadLine() } catch { return }
        if ([string]::IsNullOrWhiteSpace($requestLine)) { return }

        $parts = $requestLine.Split(" ")
        if ($parts.Length -lt 2) { return }
        $method = $parts[0].ToUpperInvariant()
        $target = $parts[1]
        $targetPath = $target.Split("?")[0]

        $requestHeaders = @{}
        while ($true) {
            $line = $null
            try { $line = $reader.ReadLine() } catch { return }
            if ($null -eq $line -or $line -eq "") { break }
            $separator = $line.IndexOf(":")
            if ($separator -gt 0) {
                $name = $line.Substring(0, $separator).Trim().ToLowerInvariant()
                $value = $line.Substring($separator + 1).Trim()
                $requestHeaders[$name] = $value
            }
        }

        $commonHeaders = [ordered]@{
            "Date"          = [DateTime]::UtcNow.ToString("r")
            "Cache-Control" = "public, max-age=300"
            "Server"        = "vrpc-serve-feed"
        }

        if ($targetPath -ne "/products/vrpc/update-manifest" -and $targetPath -ne "/vrpc/update-manifest") {
            $body = Get-Utf8JsonBytes '{"error":"not_found"}'
            $headers = [ordered]@{}
            foreach ($key in $commonHeaders.Keys) { $headers[$key] = $commonHeaders[$key] }
            $headers["Content-Type"] = "application/json"
            Send-Response -Stream $stream -Status 404 -Reason "Not Found" -Headers $headers -Body $body -HeadOnly:($method -eq "HEAD")
            if (-not $Quiet) { Write-Host ("[serve-feed] {0} {1} -> 404" -f $method, $targetPath) }
            return
        }

        if ($method -ne "GET" -and $method -ne "HEAD") {
            $body = Get-Utf8JsonBytes '{"error":"method_not_allowed"}'
            $headers = [ordered]@{}
            foreach ($key in $commonHeaders.Keys) { $headers[$key] = $commonHeaders[$key] }
            $headers["Content-Type"] = "application/json"
            $headers["Allow"] = "GET, HEAD"
            Send-Response -Stream $stream -Status 405 -Reason "Method Not Allowed" -Headers $headers -Body $body -HeadOnly:($method -eq "HEAD")
            if (-not $Quiet) { Write-Host ("[serve-feed] {0} {1} -> 405" -f $method, $targetPath) }
            return
        }

        $manifestBytes = $null
        try { $manifestBytes = [System.IO.File]::ReadAllBytes($manifestFullPath) }
        catch {
            $body = Get-Utf8JsonBytes '{"error":"manifest_unavailable"}'
            $headers = [ordered]@{}
            foreach ($key in $commonHeaders.Keys) { $headers[$key] = $commonHeaders[$key] }
            $headers["Content-Type"] = "application/json"
            Send-Response -Stream $stream -Status 500 -Reason "Internal Server Error" -Headers $headers -Body $body -HeadOnly:($method -eq "HEAD")
            if (-not $Quiet) { Write-Host ("[serve-feed] {0} {1} -> 500 (manifest unreadable: {2})" -f $method, $targetPath, $_.Exception.Message) }
            return
        }

        $etag = '"' + (Get-BytesSha256Hex -Bytes $manifestBytes) + '"'

        $ifNoneMatch = ""
        if ($requestHeaders.ContainsKey("if-none-match")) { $ifNoneMatch = [string]$requestHeaders["if-none-match"] }
        if (Get-IfNoneMatchMatches -HeaderValue $ifNoneMatch -ETag $etag) {
            $headers = [ordered]@{}
            foreach ($key in $commonHeaders.Keys) { $headers[$key] = $commonHeaders[$key] }
            $headers["ETag"] = $etag
            Send-Response -Stream $stream -Status 304 -Reason "Not Modified" -Headers $headers -HeadOnly:$true
            if (-not $Quiet) { Write-Host ("[serve-feed] {0} {1} -> 304 ({2})" -f $method, $targetPath, $etag) }
            return
        }

        $headers = [ordered]@{}
        foreach ($key in $commonHeaders.Keys) { $headers[$key] = $commonHeaders[$key] }
        $headers["Content-Type"] = "application/json"
        $headers["ETag"] = $etag
        Send-Response -Stream $stream -Status 200 -Reason "OK" -Headers $headers -Body $manifestBytes -HeadOnly:($method -eq "HEAD")
        if (-not $Quiet) {
            Write-Host ("[serve-feed] {0} {1} -> 200 application/json {2} bytes etag={3}" -f $method, $targetPath, $manifestBytes.Length, $etag)
        }
    }
    finally {
        $reader.Dispose()
    }
}

try {
    if ([string]::IsNullOrWhiteSpace($ManifestPath)) { Fail "-ManifestPath is required (the signed envelope to serve)." }
    if ([System.IO.Path]::IsPathRooted($ManifestPath)) {
        $manifestFullPath = [System.IO.Path]::GetFullPath($ManifestPath)
    }
    else {
        $manifestFullPath = [System.IO.Path]::GetFullPath((Join-Path $script:Cwd $ManifestPath))
    }
    if (-not (Test-Path -LiteralPath $manifestFullPath -PathType Leaf)) { Fail "The manifest was not found: $manifestFullPath" }
    if ($Port -lt 0 -or $Port -gt 65535) { Fail "Invalid -Port $Port (expected 0-65535)." }

    $address = [System.Net.IPAddress]::Parse($BindAddress)
    $listener = $null
    $served = 0
    try {
        $listener = New-Object System.Net.Sockets.TcpListener($address, $Port)
        $listener.Start()
        $actualPort = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
        $url = "http://${BindAddress}:${actualPort}/products/vrpc/update-manifest"

        if (-not [string]::IsNullOrWhiteSpace($ReadyFile)) {
            if ([System.IO.Path]::IsPathRooted($ReadyFile)) { $readyFullPath = [System.IO.Path]::GetFullPath($ReadyFile) }
            else { $readyFullPath = [System.IO.Path]::GetFullPath((Join-Path $script:Cwd $ReadyFile)) }
            $readyDirectory = Split-Path -Parent $readyFullPath
            if (-not (Test-Path -LiteralPath $readyDirectory -PathType Container)) {
                New-Item -ItemType Directory -Path $readyDirectory -Force | Out-Null
            }
            [System.IO.File]::WriteAllText($readyFullPath, $url, (New-Object System.Text.UTF8Encoding($false)))
        }

        Write-Host ("[serve-feed] Serving {0}" -f $manifestFullPath)
        Write-Host ("[serve-feed] URL: {0}" -f $url)
        Write-Host "[serve-feed] ETag is the SHA-256 of the served bytes; If-None-Match is answered with 304. Ctrl+C stops the server."

        while ($true) {
            $client = $listener.AcceptTcpClient()
            try { Handle-Client -Client $client }
            catch { if (-not $Quiet) { Write-Host ("[serve-feed] request failed: " + $_.Exception.Message) } }
            finally { $client.Close() }

            $served++
            if ($Once.IsPresent) { break }
            if ($MaxRequests -gt 0 -and $served -ge $MaxRequests) { break }
        }
        exit 0
    }
    finally {
        if ($null -ne $listener) { $listener.Stop() }
        if (-not [string]::IsNullOrWhiteSpace($ReadyFile)) {
            try {
                if ([System.IO.Path]::IsPathRooted($ReadyFile)) { $readyFullPath = [System.IO.Path]::GetFullPath($ReadyFile) }
                else { $readyFullPath = [System.IO.Path]::GetFullPath((Join-Path $script:Cwd $ReadyFile)) }
                if (Test-Path -LiteralPath $readyFullPath -PathType Leaf) { Remove-Item -LiteralPath $readyFullPath -Force -ErrorAction SilentlyContinue }
            }
            catch { }
        }
    }
}
catch {
    [Console]::Error.WriteLine("[serve-feed] ERROR: " + $_.Exception.Message)
    exit 1
}
