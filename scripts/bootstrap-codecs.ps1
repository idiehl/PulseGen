[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$lockPath = Join-Path $repositoryRoot 'dependencies.lock.json'
$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
$dependency = $lock.native.libsndfile
if ($dependency.version -ne '1.2.2' -or
    [string]::IsNullOrWhiteSpace($dependency.archiveUrl) -or
    [string]::IsNullOrWhiteSpace($dependency.archiveSha256) -or
    [string]::IsNullOrWhiteSpace($dependency.binarySha256) -or
    [string]::IsNullOrWhiteSpace($dependency.licenseUrl) -or
    [string]::IsNullOrWhiteSpace($dependency.licenseSha256)) {
    throw 'The libsndfile codec dependency lock is incomplete.'
}

$runtimeRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot ".runtime\codecs\libsndfile-$($dependency.version)"))
$runtimeBoundary = [IO.Path]::GetFullPath((Join-Path $repositoryRoot '.runtime\codecs')).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $runtimeRoot.StartsWith($runtimeBoundary, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to prepare a codec runtime outside $runtimeBoundary"
}
$binaryPath = Join-Path $runtimeRoot 'sndfile.dll'
$licensePath = Join-Path $runtimeRoot 'COPYING-libsndfile-LGPL-2.1.txt'

function Test-ExpectedHash {
    param([string]$Path, [string]$Expected)
    (Test-Path -LiteralPath $Path -PathType Leaf) -and
        ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $Expected)
}

if ((Test-ExpectedHash -Path $binaryPath -Expected $dependency.binarySha256) -and
    (Test-ExpectedHash -Path $licensePath -Expected $dependency.licenseSha256)) {
    Write-Output "PULSEGEN_CODECS_OK libsndfile=$($dependency.version) sha256=$($dependency.binarySha256)"
    return
}

New-Item -ItemType Directory -Force -Path $runtimeRoot | Out-Null
$downloadsRoot = Join-Path $repositoryRoot '.runtime\downloads'
New-Item -ItemType Directory -Force -Path $downloadsRoot | Out-Null
$archivePath = Join-Path $downloadsRoot "libsndfile-$($dependency.version)-win64.zip"
if (-not (Test-ExpectedHash -Path $archivePath -Expected $dependency.archiveSha256)) {
    Invoke-WebRequest -Uri $dependency.archiveUrl -OutFile $archivePath
}
if (-not (Test-ExpectedHash -Path $archivePath -Expected $dependency.archiveSha256)) {
    throw 'The downloaded libsndfile archive failed its SHA-256 integrity check.'
}

$staging = Join-Path $runtimeRoot ("staging-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging | Out-Null
try {
    Expand-Archive -LiteralPath $archivePath -DestinationPath $staging
    $sourceBinaries = @(Get-ChildItem -LiteralPath $staging -Filter 'sndfile.dll' -File -Recurse)
    if ($sourceBinaries.Count -ne 1 -or -not (Test-ExpectedHash -Path $sourceBinaries[0].FullName -Expected $dependency.binarySha256)) {
        throw 'The extracted libsndfile DLL failed its pinned SHA-256 integrity check.'
    }
    Copy-Item -LiteralPath $sourceBinaries[0].FullName -Destination $binaryPath -Force
    Invoke-WebRequest -Uri $dependency.licenseUrl -OutFile $licensePath
    if (-not (Test-ExpectedHash -Path $licensePath -Expected $dependency.licenseSha256)) {
        throw 'The downloaded libsndfile license failed its SHA-256 integrity check.'
    }
}
finally {
    if (Test-Path -LiteralPath $staging) {
        $resolvedStaging = [IO.Path]::GetFullPath($staging)
        if (-not $resolvedStaging.StartsWith($runtimeRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean codec staging outside $runtimeRoot"
        }
        Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
    }
}

if (-not (Test-ExpectedHash -Path $binaryPath -Expected $dependency.binarySha256)) {
    throw 'The installed libsndfile runtime failed its final SHA-256 integrity check.'
}
Write-Output "PULSEGEN_CODECS_OK libsndfile=$($dependency.version) sha256=$($dependency.binarySha256)"
