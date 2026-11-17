[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'

$root = Join-Path (Split-Path $PSScriptRoot -Parent) 'third_party'
New-Item -ItemType Directory -Force -Path $root | Out-Null

function Get-PinnedFile([string]$File, [string]$Url, [string]$Sha256) {
    $target = Join-Path $root $File
    if (-not (Test-Path -LiteralPath $target)) {
        $partial = "$target.download-$PID"
        try {
            Invoke-WebRequest -Uri $Url -OutFile $partial -UseBasicParsing
            if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $Sha256) {
                throw "SHA-256 mismatch for $File."
            }
            Move-Item -LiteralPath $partial -Destination $target
        } finally {
            if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
        }
    }
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $Sha256) {
        throw "SHA-256 mismatch for $target. Remove that file and rerun."
    }
    return $target
}

function Expand-PinnedTar([string]$Directory, [string]$File, [string]$Url, [string]$Hash) {
    $ready = Join-Path $root "$Directory/.verified-$Hash"
    if (Test-Path -LiteralPath $ready) { return }
    $archive = Get-PinnedFile $File $Url $Hash
    if ($Directory -eq 'zstd-1.5.7') {
        # Windows tar cannot create these upstream test-only symbolic links.
        & tar.exe -xf $archive -C $root --exclude=zstd-1.5.7/tests/cli-tests/bin/unzstd --exclude=zstd-1.5.7/tests/cli-tests/bin/zstdcat
    } else {
        & tar.exe -xf $archive -C $root
    }
    if ($LASTEXITCODE -ne 0) { throw "Could not extract $File." }
    New-Item -ItemType File -Path $ready | Out-Null
}

$sdkHash = '1d9da340870b12712e9068be178a4436fbad785486b62aed6e2aa88ea1e3bb65'
$sdkPath = Join-Path $root 'lzma-26.06'
$sdkReady = Join-Path $sdkPath ".verified-$sdkHash"
if (-not (Test-Path -LiteralPath $sdkReady)) {
    $sdk = Get-PinnedFile 'lzma2606.7z' 'https://github.com/ip7z/7zip/releases/download/26.06/lzma2606.7z' $sdkHash
    $installed = Get-Command 7z.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    $unpacker = if ($installed) { $installed.Source } else {
        Get-PinnedFile '7zr.exe' 'https://www.7-zip.org/a/7zr.exe' '256feca8e274e5da655e2a284fabafd9f554365eb164862089dacd4e8276d282'
    }
    & $unpacker x $sdk "-o$sdkPath" -y
    if ($LASTEXITCODE -ne 0) { throw 'Could not extract the official LZMA SDK.' }
    New-Item -ItemType File -Path $sdkReady | Out-Null
}

Expand-PinnedTar 'minizip-ng-4.2.2' 'minizip-ng-4.2.2.tar.gz' 'https://codeload.github.com/zlib-ng/minizip-ng/tar.gz/refs/tags/4.2.2' '71af7b9799856d8b03619df3949e9c1be9703f8de0795af71399ba283cb27aac'
Expand-PinnedTar 'zlib-1.4' 'zlib-1.4.tar.gz' 'https://zlib.net/fossils/zlib-1.4.tar.gz' '02dd159783677a749c04adbdd02b55b8a2bcc7e054f51b76b549199a916ad584'
Expand-PinnedTar 'zstd-1.5.7' 'zstd-1.5.7.tar.gz' 'https://github.com/facebook/zstd/releases/download/v1.5.7/zstd-1.5.7.tar.gz' 'eb33e51f49a15e023950cd7825ca74a4a2b43db8354825ac24fc1b7ee09e6fa3'
