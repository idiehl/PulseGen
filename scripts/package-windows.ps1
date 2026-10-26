[CmdletBinding()]
param(
    [ValidateSet('Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('win-x64')]
    [string]$RuntimeIdentifier = 'win-x64',
    [switch]$NoBuild,
    [switch]$AllowDirty,
    [switch]$SkipVerification,
    [switch]$PublicDistribution,
    [string]$OutputDirectory = 'out/packages',
    [ValidatePattern('^[A-Fa-f0-9]{40}$')]
    [string]$SigningCertificateThumbprint,
    [uri]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$packagesRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
$stagingRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'out\package-staging'))
$activeStaging = $null
$activeArchive = $null

function Assert-ChildPath {
    param([string]$Path, [string]$Parent)
    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullParent = [IO.Path]::GetFullPath($Parent).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($fullParent, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing generated-file operation outside $fullParent"
    }
    return $fullPath
}

function Remove-GeneratedPath {
    param([string]$Path, [string]$Parent)
    $resolved = Assert-ChildPath -Path $Path -Parent $Parent
    if (Test-Path -LiteralPath $resolved) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

$packagesRoot = Assert-ChildPath -Path $packagesRoot -Parent (Join-Path $repositoryRoot 'out')

function Get-SignTool {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) { return $command.Source }
    $kits = 'C:\Program Files (x86)\Windows Kits\10\bin'
    if (Test-Path -LiteralPath $kits) {
        return Get-ChildItem -LiteralPath $kits -Filter signtool.exe -File -Recurse |
            Where-Object { $_.DirectoryName.EndsWith('\x64', [StringComparison]::OrdinalIgnoreCase) } |
            Sort-Object FullName -Descending |
            Select-Object -First 1 -ExpandProperty FullName
    }
    return $null
}

$originalLocation = Get-Location
try {
    Set-Location -LiteralPath $repositoryRoot
    $dirtyLines = @(git status --porcelain=v1)
    if ($LASTEXITCODE -ne 0) { throw 'Git worktree state could not be read.' }
    $sourceDirty = $dirtyLines.Count -gt 0
    if ($sourceDirty -and -not $AllowDirty) {
        throw 'Refusing to create a release candidate from a dirty worktree. Commit the intended source or pass -AllowDirty for an explicitly marked engineering package.'
    }

    [xml]$buildProperties = Get-Content -LiteralPath 'Directory.Build.props' -Raw
    $versionGroup = @($buildProperties.Project.PropertyGroup | Where-Object { -not [string]::IsNullOrWhiteSpace($_.Version) }) | Select-Object -First 1
    $version = [string]$versionGroup.Version
    if ($version -notmatch '^\d+\.\d+\.\d+-[0-9A-Za-z.-]+$') {
        throw "Directory.Build.props does not contain a valid prerelease product version: $version"
    }
    $nativeSource = Get-Content -LiteralPath 'native\src\pulsegen.cpp' -Raw
    if ($nativeSource -notmatch 'engine_version\s*=\s*"([^"]+)"' -or $Matches[1] -ne $version) {
        throw "Managed product version $version does not match the native engine version."
    }

    $commit = (git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') { throw 'The source commit could not be resolved.' }
    $packageName = "PulseGen-$version-$RuntimeIdentifier"
    $staging = Assert-ChildPath -Path (Join-Path $stagingRoot "$packageName-$([Guid]::NewGuid().ToString('N'))") -Parent $stagingRoot
    $activeStaging = $staging
    $packageDirectory = Assert-ChildPath -Path (Join-Path $packagesRoot $packageName) -Parent $packagesRoot
    $archivePath = Assert-ChildPath -Path (Join-Path $packagesRoot "$packageName.zip") -Parent $packagesRoot
    $archiveHashPath = Assert-ChildPath -Path "$archivePath.sha256" -Parent $packagesRoot
    New-Item -ItemType Directory -Path $staging -Force | Out-Null

    & (Join-Path $PSScriptRoot 'bootstrap-codecs.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Codec runtime bootstrap failed.' }
    & (Join-Path $PSScriptRoot 'bootstrap-modelhost.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'ModelHost runtime bootstrap failed.' }
    if (-not $NoBuild) {
        & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration
        if ($LASTEXITCODE -ne 0) { throw 'Release prerequisite build failed.' }
    }

    $nativeBuildRoot = Join-Path $repositoryRoot 'out\native\release'
    $nativeDll = Join-Path $nativeBuildRoot 'native\PulseGen.Engine.dll'
    foreach ($required in @(
        $nativeDll,
        (Join-Path $nativeBuildRoot '_deps\assimp-src\LICENSE'),
        (Join-Path $nativeBuildRoot '_deps\miniaudio-src\LICENSE'),
        (Join-Path $repositoryRoot '.runtime\codecs\libsndfile-1.2.2\sndfile.dll'),
        (Join-Path $repositoryRoot '.runtime\modelhost\python311\python.exe')
    )) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "A required package input is missing: $required" }
    }

    $publishArguments = @(
        'publish', 'managed\PulseGen.Editor\PulseGen.Editor.csproj',
        '-c', $Configuration,
        '-r', $RuntimeIdentifier,
        '--self-contained', 'true',
        '--output', $staging,
        '-p:PublishSingleFile=false',
        '-p:PublishTrimmed=false',
        '-p:DebugSymbols=false',
        '-p:DebugType=None'
    )
    dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) { throw 'The self-contained editor publish failed.' }
    Copy-Item -LiteralPath $nativeDll -Destination (Join-Path $staging 'PulseGen.Engine.dll') -Force
    $managedInventory = dotnet list managed\PulseGen.Editor\PulseGen.Editor.csproj package --include-transitive --format json --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'The resolved managed dependency inventory could not be generated.' }
    $managedInventoryDocument = ($managedInventory -join [Environment]::NewLine) | ConvertFrom-Json
    foreach ($project in @($managedInventoryDocument.projects)) {
        $project.path = [IO.Path]::GetRelativePath($repositoryRoot, [IO.Path]::GetFullPath([string]$project.path)).Replace([IO.Path]::DirectorySeparatorChar, '/')
    }
    [IO.File]::WriteAllText(
        (Join-Path $staging 'managed-dependencies.json'),
        ($managedInventoryDocument | ConvertTo-Json -Depth 10),
        [Text.UTF8Encoding]::new($false))

    New-Item -ItemType Directory -Path (Join-Path $staging '.runtime\modelhost') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repositoryRoot '.runtime\modelhost\python311') -Destination (Join-Path $staging '.runtime\modelhost\python311') -Recurse -Force
    New-Item -ItemType Directory -Path (Join-Path $staging 'modelhost\pulsegen_model_host') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $staging 'modelhost\packs') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'modelhost\runtime.manifest.json') -Destination (Join-Path $staging 'modelhost\runtime.manifest.json')
    Copy-Item -Path (Join-Path $repositoryRoot 'modelhost\pulsegen_model_host\*.py') -Destination (Join-Path $staging 'modelhost\pulsegen_model_host')
    Copy-Item -Path (Join-Path $repositoryRoot 'modelhost\packs\*.json') -Destination (Join-Path $staging 'modelhost\packs')
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'samples') -Destination (Join-Path $staging 'Samples') -Recurse -Force

    $licenseRoot = Join-Path $staging 'licenses'
    New-Item -ItemType Directory -Path $licenseRoot -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'THIRD_PARTY_NOTICES.md') -Destination (Join-Path $staging 'THIRD_PARTY_NOTICES.md')
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'dependencies.lock.json') -Destination (Join-Path $staging 'dependencies.lock.json')
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'packaging\START_HERE.txt') -Destination (Join-Path $staging 'START_HERE.txt')
    Copy-Item -LiteralPath (Join-Path $nativeBuildRoot '_deps\assimp-src\LICENSE') -Destination (Join-Path $licenseRoot 'Assimp-BSD-3-Clause.txt')
    Copy-Item -LiteralPath (Join-Path $nativeBuildRoot '_deps\miniaudio-src\LICENSE') -Destination (Join-Path $licenseRoot 'miniaudio-MIT-0.txt')
    Copy-Item -LiteralPath (Join-Path $repositoryRoot '.runtime\codecs\libsndfile-1.2.2\COPYING-libsndfile-LGPL-2.1.txt') -Destination (Join-Path $licenseRoot 'libsndfile-LGPL-2.1.txt')
    Copy-Item -LiteralPath (Join-Path $repositoryRoot '.runtime\modelhost\python311\LICENSE.txt') -Destination (Join-Path $licenseRoot 'CPython-PSF.txt')
    Copy-Item -LiteralPath (Join-Path $repositoryRoot '.runtime\modelhost\python311\Lib\site-packages\numpy-2.0.2.dist-info\LICENSE.txt') -Destination (Join-Path $licenseRoot 'NumPy-and-bundled-libraries.txt')

    if ($PublicDistribution) {
        & (Join-Path $PSScriptRoot 'add-publication-notices.ps1') -StagingPath $staging
        if ($LASTEXITCODE -ne 0) { throw 'Public distribution notice assembly failed.' }
    }

    $signatureState = if ($PublicDistribution) { 'unsigned-experimental-alpha' } else { 'unsigned-private-alpha' }
    if (-not [string]::IsNullOrWhiteSpace($SigningCertificateThumbprint)) {
        $signTool = Get-SignTool
        if ([string]::IsNullOrWhiteSpace($signTool)) { throw 'signtool.exe is unavailable; install the Windows SDK signing tools.' }
        $signTargets = @(Get-ChildItem -LiteralPath $staging -File | Where-Object {
            $_.Name -eq 'PulseGen.exe' -or $_.Name -eq 'PulseGen.dll' -or $_.Name -eq 'PulseGen.Engine.dll' -or
            ($_.Name.StartsWith('PulseGen.', [StringComparison]::Ordinal) -and $_.Extension -eq '.dll')
        } | Sort-Object FullName -Unique)
        foreach ($target in $signTargets) {
            & $signTool sign /sha1 $SigningCertificateThumbprint /fd SHA256 /tr $TimestampUrl.AbsoluteUri /td SHA256 $target.FullName
            if ($LASTEXITCODE -ne 0) { throw "Authenticode signing failed: $($target.Name)" }
            $signature = Get-AuthenticodeSignature -LiteralPath $target.FullName
            if ($signature.Status -ne 'Valid') { throw "Authenticode verification failed: $($target.Name) ($($signature.Status))" }
        }
        $signatureState = 'authenticode-sha256-timestamped'
    }

    $payloadFiles = @(Get-ChildItem -LiteralPath $staging -File -Recurse | Sort-Object FullName | ForEach-Object {
        [ordered]@{
            path = [IO.Path]::GetRelativePath($staging, $_.FullName).Replace([IO.Path]::DirectorySeparatorChar, '/')
            bytes = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
    $manifest = [ordered]@{
        schemaVersion = 1
        product = 'PulseGen'
        version = $version
        channel = if ($PublicDistribution) { 'experimental-alpha' } else { 'private-alpha' }
        packageType = 'portable-self-contained'
        runtimeIdentifier = $RuntimeIdentifier
        entryPoint = 'PulseGen.exe'
        sourceCommit = $commit
        sourceTreeState = if ($sourceDirty) { 'dirty-explicitly-allowed' } else { 'clean' }
        builtAtUtc = [DateTimeOffset]::UtcNow
        signature = $signatureState
        modelWeightsBundled = $false
        dependencyInventory = 'managed-dependencies.json'
        files = $payloadFiles
    }
    if ($PublicDistribution) {
        $publicationNotices = Get-Content -LiteralPath (Join-Path $staging 'licenses/public/notices-index.json') -Raw | ConvertFrom-Json
        $manifest.publicDistribution = [ordered]@{
            notices = 'licenses/public/notices-index.json'
            sourceIndex = 'licenses/public/sources-index.json'
            companionSourceArchive = $publicationNotices.companionSourceArchive
            companionSourceArchiveSha256 = $publicationNotices.companionSourceArchiveSha256
        }
    }
    $manifestPath = Join-Path $staging 'package-manifest.json'
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))

    $sumLines = @(Get-ChildItem -LiteralPath $staging -File -Recurse | Sort-Object FullName | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($staging, $_.FullName).Replace([IO.Path]::DirectorySeparatorChar, '/')
        "$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $relative"
    })
    [IO.File]::WriteAllLines((Join-Path $staging 'SHA256SUMS.txt'), $sumLines, [Text.UTF8Encoding]::new($false))

    New-Item -ItemType Directory -Path $packagesRoot -Force | Out-Null
    Remove-GeneratedPath -Path $packageDirectory -Parent $packagesRoot
    Move-Item -LiteralPath $staging -Destination $packageDirectory
    $activeStaging = $null

    if (-not $SkipVerification) {
        & (Join-Path $PSScriptRoot 'verify-package.ps1') -PackagePath $packageDirectory
        if ($LASTEXITCODE -ne 0) { throw 'Packaged runtime verification failed.' }
    }

    Remove-GeneratedPath -Path $archivePath -Parent $packagesRoot
    Remove-GeneratedPath -Path $archiveHashPath -Parent $packagesRoot
    Add-Type -AssemblyName System.IO.Compression
    $temporaryArchive = Assert-ChildPath -Path "$archivePath.$([Guid]::NewGuid().ToString('N')).tmp" -Parent $packagesRoot
    $activeArchive = $temporaryArchive
    $zip = [IO.Compression.ZipFile]::Open($temporaryArchive, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $packageDirectory -File -Recurse | Sort-Object FullName) {
            $relative = [IO.Path]::GetRelativePath($packageDirectory, $file.FullName).Replace([IO.Path]::DirectorySeparatorChar, '/')
            $entry = $zip.CreateEntry("$packageName/$relative", [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $input = [IO.File]::OpenRead($file.FullName)
            try {
                $output = $entry.Open()
                try { $input.CopyTo($output) } finally { $output.Dispose() }
            }
            finally { $input.Dispose() }
        }
    }
    finally { $zip.Dispose() }
    Move-Item -LiteralPath $temporaryArchive -Destination $archivePath
    $activeArchive = $null
    $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($archiveHashPath, "$archiveHash  $([IO.Path]::GetFileName($archivePath))`n", [Text.UTF8Encoding]::new($false))
    $archiveItem = Get-Item -LiteralPath $archivePath
    Write-Output "PULSEGEN_PACKAGE_OK version=$version source=$commit state=$($manifest.sourceTreeState) signature=$signatureState bytes=$($archiveItem.Length) sha256=$archiveHash path=$archivePath"
}
finally {
    if ($null -ne $activeStaging -and (Test-Path -LiteralPath $activeStaging)) {
        Remove-Item -LiteralPath (Assert-ChildPath -Path $activeStaging -Parent $stagingRoot) -Recurse -Force
    }
    if ($null -ne $activeArchive -and (Test-Path -LiteralPath $activeArchive)) {
        Remove-Item -LiteralPath (Assert-ChildPath -Path $activeArchive -Parent $packagesRoot) -Force
    }
    Set-Location -LiteralPath $originalLocation
}
