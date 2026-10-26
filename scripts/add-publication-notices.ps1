[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$StagingPath,
    [string]$SourceOutputDirectory = 'out/publication/sources'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$stage = [IO.Path]::GetFullPath($StagingPath)
$outRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'out'))
$outPrefix = $outRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $stage.StartsWith($outPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath $stage -PathType Container)) {
    throw 'Publication notices may only be added to a package staged below repository out/.'
}
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $SourceOutputDirectory))
if (-not $sourceRoot.StartsWith($outPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Companion source output must be below repository out/.'
}
$cacheRoot = Join-Path $repositoryRoot 'out/publication-inputs'
$noticeRoot = Join-Path $stage 'licenses/public'
if (Test-Path -LiteralPath $noticeRoot) { throw 'Publication notices already exist in this staging directory.' }
New-Item -ItemType Directory -Path $cacheRoot, $noticeRoot, $sourceRoot -Force | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
$documents = [Collections.Generic.List[object]]::new()
$components = [Collections.Generic.List[object]]::new()

function Add-Notice {
    param([string]$RelativePath, [byte[]]$Bytes, [string]$Origin)
    if ($Bytes.Length -eq 0) { throw "Empty upstream notice: $Origin" }
    $target = [IO.Path]::GetFullPath((Join-Path $noticeRoot $RelativePath))
    $prefix = $noticeRoot + [IO.Path]::DirectorySeparatorChar
    if (-not $target.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe notice path.' }
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    [IO.File]::WriteAllBytes($target, $Bytes)
    $documents.Add([ordered]@{
        path = ('licenses/public/' + $RelativePath.Replace('\', '/'))
        source = $Origin
        sha256 = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($Bytes))
    })
}

function Add-LocalNotice {
    param([string]$Path, [string]$RelativePath, [string]$Origin)
    Add-Notice -RelativePath $RelativePath -Bytes ([IO.File]::ReadAllBytes($Path)) -Origin $Origin
}

function Add-UpstreamNotice {
    param([string]$Url, [string]$RelativePath)
    $response = Invoke-WebRequest -Uri $Url -TimeoutSec 45
    $bytes = if ($response.Content -is [byte[]]) { $response.Content } else { $utf8.GetBytes([string]$response.Content) }
    Add-Notice -RelativePath $RelativePath -Bytes $bytes -Origin $Url
}

$runtimeConfig = Get-Content -LiteralPath (Join-Path $stage 'PulseGen.runtimeconfig.json') -Raw | ConvertFrom-Json
$runtimeVersion = @($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App')[0].version
$nugetRoot = if (-not [string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
    [IO.Path]::GetFullPath($env:NUGET_PACKAGES)
} else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages' }
$runtimePackage = Join-Path $nugetRoot "microsoft.netcore.app.runtime.win-x64/$runtimeVersion"
$mitLicense = [IO.File]::ReadAllText((Join-Path $runtimePackage 'LICENSE.TXT'))
$mitPermissionOffset = $mitLicense.IndexOf('Permission is hereby granted', [StringComparison]::Ordinal)
if ($mitPermissionOffset -lt 0) { throw 'The runtime MIT permission text was not found.' }
$mitPermissionText = $mitLicense.Substring($mitPermissionOffset)
foreach ($name in @('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')) {
    Add-LocalNotice -Path (Join-Path $runtimePackage $name) -RelativePath "dotnet/$name" `
        -Origin "https://api.nuget.org/v3-flatcontainer/microsoft.netcore.app.runtime.win-x64/$runtimeVersion/microsoft.netcore.app.runtime.win-x64.$runtimeVersion.nupkg"
}
$components.Add([ordered]@{ id = 'Microsoft.NETCore.App.Runtime.win-x64'; version = $runtimeVersion; license = 'MIT and bundled third-party notices' })

$inventory = Get-Content -LiteralPath (Join-Path $stage 'managed-dependencies.json') -Raw | ConvertFrom-Json
$packages = @($inventory.projects[0].frameworks[0].topLevelPackages) + @($inventory.projects[0].frameworks[0].transitivePackages)
foreach ($package in $packages) {
    $id = [string]$package.id
    $version = [string]$package.resolvedVersion
    $packageDirectory = Join-Path $nugetRoot ($id.ToLowerInvariant() + '/' + $version)
    $nuspecPath = @(Get-ChildItem -LiteralPath $packageDirectory -Filter '*.nuspec' -File)[0].FullName
    [xml]$nuspec = Get-Content -LiteralPath $nuspecPath -Raw
    $metadata = $nuspec.package.metadata
    $license = [string]$metadata.license.InnerText
    $origin = "https://api.nuget.org/v3-flatcontainer/$($id.ToLowerInvariant())/$version/$($id.ToLowerInvariant()).$version.nupkg"
    $components.Add([ordered]@{
        id = $id; version = $version; license = $license
        copyright = [string]$metadata.copyright
        repository = [string]$metadata.repository.url
        repositoryCommit = [string]$metadata.repository.commit
        package = $origin
    })
    Add-LocalNotice -Path $nuspecPath -RelativePath "nuget/$id/package.nuspec" -Origin $origin
    if ($metadata.license.type -eq 'expression') {
        if ($license -eq 'MIT' -and -not (Test-Path -LiteralPath (Join-Path $packageDirectory 'LICENSE.txt'))) {
            $text = "$id $version`nThe MIT License (MIT)`n$([string]$metadata.copyright)`n`n$mitPermissionText"
            Add-Notice -RelativePath "nuget/$id/LICENSE.txt" -Bytes $utf8.GetBytes($text) -Origin $origin
        } elseif ($license -notin @('MIT', 'Apache-2.0')) {
            throw "Unreviewed NuGet license expression: $id $license"
        }
    } elseif ($metadata.license.type -eq 'file') {
        if (-not (Test-Path -LiteralPath (Join-Path $packageDirectory $license))) { throw "Missing license for $id" }
    } else { throw "Missing license metadata for $id" }
    foreach ($file in Get-ChildItem -LiteralPath $packageDirectory -Recurse -File | Where-Object {
        $_.Name -match '^(?i:LICENSE|LICENCE|COPYING|COPYRIGHT|NOTICE|THIRD.PARTY.NOTICES)(\.[^/]*)?$'
    }) {
        $relative = [IO.Path]::GetRelativePath($packageDirectory, $file.FullName).Replace('\', '/')
        # A packaged notice supersedes the generated expression text.
        $target = Join-Path $noticeRoot "nuget/$id/$relative"
        if (Test-Path -LiteralPath $target) { continue }
        Add-LocalNotice -Path $file.FullName -RelativePath "nuget/$id/$relative" -Origin $origin
    }
}

Add-UpstreamNotice -Url 'https://raw.githubusercontent.com/AvaloniaUI/Avalonia/984c3b833f29806eb159fb7f12a6132e936b75df/licence.md' -RelativePath 'Avalonia-upstream-MIT.txt'
Add-UpstreamNotice -Url 'https://raw.githubusercontent.com/ericsink/SQLitePCL.raw/v2.1.12/LICENSE.TXT' -RelativePath 'SQLitePCLRaw-Apache-2.0.txt'
Add-UpstreamNotice -Url 'https://raw.githubusercontent.com/ericsink/SQLitePCL.raw/v2.1.12/NOTICE.TXT' -RelativePath 'SQLitePCLRaw-NOTICE.txt'
Add-UpstreamNotice -Url 'https://raw.githubusercontent.com/tmds/Tmds.DBus/8cb04f66c330b64244e996ff68f685f27f7381a0/COPYING' -RelativePath 'Tmds-DBus-upstream-MIT.txt'
Add-UpstreamNotice -Url 'https://raw.githubusercontent.com/libsndfile/libsndfile/1.2.2/src/ALAC/LICENSE' -RelativePath 'libsndfile/ALAC-Apache-2.0.txt'

# ANGLE's D3D renderer and the retained contributed license records. Some of the
# upstream records describe optional backends/build tools rather than shipped code.
$angleCommit = 'cb8b4e1307a9d8f5ff56b8c5973bea4158ffead8'
$anglePaths = @('LICENSE', 'src/common/third_party/xxhash/LICENSE', 'src/libANGLE/overlay/LICENSE.txt',
    'src/libANGLE/renderer/vulkan/shaders/src/third_party/etc_decoder/LICENSE',
    'src/libANGLE/renderer/vulkan/shaders/src/third_party/ffx_spd/LICENSE',
    'src/third_party/ceval/LICENSE', 'src/third_party/libXNVCtrl/LICENSE', 'src/third_party/volk/LICENSE.md',
    'third_party/android_system_sdk/LICENSE', 'third_party/bazel/LICENSE', 'third_party/colorama/LICENSE',
    'third_party/flatbuffers/LICENSE', 'third_party/glslang/LICENSE', 'third_party/proguard/LICENSE',
    'third_party/r8/LICENSE', 'third_party/spirv-headers/LICENSE', 'third_party/spirv-tools/LICENSE',
    'third_party/turbine/LICENSE', 'third_party/vulkan-headers/LICENSE.txt', 'util/windows/third_party/StackWalker/LICENSE')
foreach ($path in $anglePaths) {
    Add-UpstreamNotice -Url "https://raw.githubusercontent.com/AvaloniaUI/angle/$angleCommit/$path" -RelativePath "ANGLE/$path"
}

$assimpRoot = Join-Path $repositoryRoot 'out/native/release/_deps/assimp-src'
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $assimpRoot 'contrib') -Recurse -File | Where-Object {
    $_.Name -match '^(?i:LICENSE|LICENCE|COPYING|COPYRIGHT|UNLICENSE)(\.[^/]*)?$'
}) {
    $relative = [IO.Path]::GetRelativePath($assimpRoot, $file.FullName).Replace('\', '/')
    Add-LocalNotice -Path $file.FullName -RelativePath "Assimp/$relative" `
        -Origin "https://github.com/assimp/assimp/blob/392a658f9c271be965271f45e7521a1b80ea4392/$relative"
}
$stbPath = Join-Path $assimpRoot 'contrib/stb/stb_image.h'
$stbText = [IO.File]::ReadAllText($stbPath)
$stbLicenseOffset = $stbText.LastIndexOf('ALTERNATIVE A - MIT License', [StringComparison]::Ordinal)
if ($stbLicenseOffset -lt 0) { throw 'Assimp stb_image license block was not found.' }
Add-Notice -RelativePath 'Assimp/stb_image-license.txt' -Bytes $utf8.GetBytes($stbText.Substring($stbLicenseOffset)) `
    -Origin 'https://github.com/assimp/assimp/blob/392a658f9c271be965271f45e7521a1b80ea4392/contrib/stb/stb_image.h'

$sourceLock = Get-Content -LiteralPath (Join-Path $repositoryRoot 'packaging/public-notices/sources.lock.json') -Raw | ConvertFrom-Json
$sourceItems = [Collections.Generic.List[object]]::new()
foreach ($artifact in $sourceLock.artifacts) {
    $cached = Join-Path $cacheRoot $artifact.file
    if (-not (Test-Path -LiteralPath $cached) -or (Get-FileHash -LiteralPath $cached -Algorithm SHA256).Hash.ToLowerInvariant() -ne $artifact.sha256) {
        $downloadUrl = if ($artifact.downloadUrl) { $artifact.downloadUrl } else { $artifact.url }
        $temporary = $cached + '.' + [Guid]::NewGuid().ToString('N') + '.download'
        Invoke-WebRequest -Uri $downloadUrl -OutFile $temporary -TimeoutSec 90
        if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash.ToLowerInvariant() -ne $artifact.sha256) { throw "Source archive checksum mismatch: $($artifact.id)" }
        if (Test-Path -LiteralPath $cached) { throw "Unexpected existing source cache entry: $cached" }
        Move-Item -LiteralPath $temporary -Destination $cached
    }
    if ($artifact.sha512 -and (Get-FileHash -LiteralPath $cached -Algorithm SHA512).Hash.ToLowerInvariant() -ne $artifact.sha512) { throw "Source archive SHA-512 mismatch: $($artifact.id)" }
    $entries = @(& tar -tf $cached)
    if ($LASTEXITCODE -ne 0 -or $entries.Count -lt 10) { throw "Invalid source archive: $($artifact.id)" }
    $sourceItems.Add($artifact)
    foreach ($entry in $entries | Where-Object {
        $_ -match '(?i)/(COPYING|COPYRIGHT|LICENSE|AUTHORS)(\.[^/]*)?$' -or $_ -match '/src/G72x/README.original$'
    }) {
        if ($artifact.id -eq 'vcpkg-build-recipes' -and $entry -notmatch '^vcpkg-[^/]+/LICENSE.txt$') { continue }
        $textLines = @(& tar -xOf $cached $entry)
        if ($LASTEXITCODE -ne 0) { throw "Unable to extract upstream notice: $entry" }
        $text = ($textLines -join "`n") + "`n"
        $relative = $entry.Substring($entry.IndexOf('/') + 1)
        Add-Notice -RelativePath "codecs/$($artifact.id)/$relative.txt" -Bytes $utf8.GetBytes($text) -Origin ($artifact.url + '#/' + $entry)
    }
}

[xml]$buildProperties = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props') -Raw
$version = [string]$buildProperties.Project.PropertyGroup.Version
$sourceName = "PulseGen-$version-third-party-sources.zip"
$sourceArchivePath = Join-Path $sourceRoot $sourceName
$temporarySourcePath = $sourceArchivePath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
$sourceIndex = [ordered]@{ schemaVersion = 1; components = $sourceItems.ToArray() }
$sourceIndexText = $sourceIndex | ConvertTo-Json -Depth 10
Add-Type -AssemblyName System.IO.Compression
$zip = [IO.Compression.ZipFile]::Open($temporarySourcePath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($artifact in $sourceLock.artifacts) {
        $entry = $zip.CreateEntry($artifact.file, [IO.Compression.CompressionLevel]::NoCompression)
        $entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
        $inputStream = [IO.File]::OpenRead((Join-Path $cacheRoot $artifact.file))
        $outputStream = $entry.Open()
        try { $inputStream.CopyTo($outputStream) } finally { $inputStream.Dispose(); $outputStream.Dispose() }
    }
    $entry = $zip.CreateEntry('sources-index.json')
    $entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
    $outputStream = $entry.Open()
    try { $bytes = $utf8.GetBytes($sourceIndexText); $outputStream.Write($bytes) } finally { $outputStream.Dispose() }
} finally { $zip.Dispose() }
$sourceHash = (Get-FileHash -LiteralPath $temporarySourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
if (Test-Path -LiteralPath $sourceArchivePath) {
    if ((Get-FileHash -LiteralPath $sourceArchivePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $sourceHash) {
        throw "Existing companion source archive differs; the new candidate is retained at $temporarySourcePath"
    }
    Remove-Item -LiteralPath $temporarySourcePath
} else {
    Move-Item -LiteralPath $temporarySourcePath -Destination $sourceArchivePath
}
[IO.File]::WriteAllText($sourceArchivePath + '.sha256', "$sourceHash  $sourceName`n", $utf8)
[IO.File]::WriteAllText((Join-Path $noticeRoot 'sources-index.json'), $sourceIndexText, $utf8)
Add-LocalNotice -Path (Join-Path $repositoryRoot 'packaging/public-notices/LGPL_RELINKING.txt') -RelativePath 'LGPL_RELINKING.txt' -Origin 'PulseGen distribution terms for LGPL components'
$summary = @(
    '# PulseGen public distribution notices', '',
    'The subdirectories contain full upstream copyright and license texts.',
    'NuGet package metadata records the exact resolved versions and copyright holders.',
    'The .NET, SkiaSharp and HarfBuzzSharp records also include their bundled third-party notices.',
    'Assimp and ANGLE records conservatively retain contributed and optional-backend notices;',
    'their inclusion does not assert that every optional component is linked.', '',
    'No model weights or user recordings are bundled.', '',
    '## LGPL source access and modification', '',
    'See LGPL_RELINKING.txt for the narrowly scoped modification and debugging permission.',
    "Download the companion $sourceName from the same release as the Windows ZIP.",
    "Its SHA-256 is $sourceHash.",
    'sources-index.json lists each original upstream URL, archive hash and identity evidence.',
    'That index qualifies the libogg version and the reference vcpkg recipes where the original',
    'upstream binary did not preserve complete build-tool provenance.', '',
    '| Component | Source version | License |', '| --- | --- | --- |'
)
foreach ($artifact in $sourceLock.artifacts) { $summary += "| $($artifact.id) | $($artifact.version) | $($artifact.license) |" }
Add-Notice -RelativePath 'README.md' -Bytes $utf8.GetBytes(($summary -join "`n") + "`n") -Origin 'Generated distribution index'
$index = [ordered]@{
    schemaVersion = 1; components = $components.ToArray(); documents = $documents.ToArray()
    companionSourceArchive = $sourceName; companionSourceArchiveSha256 = $sourceHash
    scope = 'Upstream notices include optional components and build-tool licenses; their inclusion does not assert that every optional component is linked.'
}
[IO.File]::WriteAllText((Join-Path $noticeRoot 'notices-index.json'), ($index | ConvertTo-Json -Depth 10), $utf8)
Write-Output "PULSEGEN_PUBLIC_NOTICES_OK documents=$($documents.Count) managedComponents=$($components.Count) sourceArchives=$($sourceItems.Count) sources=$sourceArchivePath sha256=$sourceHash"
