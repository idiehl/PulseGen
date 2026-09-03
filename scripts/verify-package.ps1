[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackagePath,
    [string]$OutputDirectory,
    [ValidateRange(10, 600)]
    [int]$TimeoutSeconds = 120,
    [switch]$SkipRuntimeSmoke
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$verificationRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'out\package-verification'))
$fullPackagePath = [IO.Path]::GetFullPath($PackagePath)
$maximumArchiveEntries = 20000
$maximumExpandedArchiveBytes = 4L * 1024 * 1024 * 1024

function Assert-ChildPath {
    param([string]$Path, [string]$Parent)
    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullParent = [IO.Path]::GetFullPath($Parent).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($fullParent, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing generated-file operation outside $fullParent"
    }
    return $fullPath
}

function Remove-GeneratedDirectory {
    param([string]$Path)
    $resolved = Assert-ChildPath -Path $Path -Parent $verificationRoot
    if (Test-Path -LiteralPath $resolved) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

if (-not (Test-Path -LiteralPath $fullPackagePath)) {
    throw "Package path does not exist: $fullPackagePath"
}

$packageLeaf = if ([IO.Path]::GetExtension($fullPackagePath).Equals('.zip', [StringComparison]::OrdinalIgnoreCase)) {
    [IO.Path]::GetFileNameWithoutExtension($fullPackagePath)
}
else {
    Split-Path -Leaf $fullPackagePath
}
$verificationOutput = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $verificationRoot $packageLeaf
}
else {
    Assert-ChildPath -Path $OutputDirectory -Parent $verificationRoot
}
Remove-GeneratedDirectory -Path $verificationOutput
New-Item -ItemType Directory -Path $verificationOutput -Force | Out-Null

$extractionRoot = $null
$packageRoot = $fullPackagePath
try {
    if ([IO.Path]::GetExtension($fullPackagePath).Equals('.zip', [StringComparison]::OrdinalIgnoreCase)) {
        Add-Type -AssemblyName System.IO.Compression
        $extractionRoot = Assert-ChildPath -Path (Join-Path $verificationOutput 'extracted') -Parent $verificationRoot
        New-Item -ItemType Directory -Path $extractionRoot -Force | Out-Null
        $archive = [IO.Compression.ZipFile]::OpenRead($fullPackagePath)
        try {
            if ($archive.Entries.Count -gt $maximumArchiveEntries) {
                throw "Package archive contains more than $maximumArchiveEntries entries."
            }
            $extractionPrefix = $extractionRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
            $expandedBytes = 0L
            $extractionTargets = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach ($entry in $archive.Entries) {
                if ($entry.Length -lt 0 -or $entry.Length -gt ($maximumExpandedArchiveBytes - $expandedBytes)) {
                    throw 'Package archive expands beyond the 4 GiB verification limit.'
                }
                $expandedBytes += $entry.Length
                $relative = $entry.FullName.Replace('/', [IO.Path]::DirectorySeparatorChar)
                $target = [IO.Path]::GetFullPath((Join-Path $extractionRoot $relative))
                if (-not $target.StartsWith($extractionPrefix, [StringComparison]::OrdinalIgnoreCase) -or
                    -not $extractionTargets.Add($target)) {
                    throw "Archive contains an unsafe or duplicate path: $($entry.FullName)"
                }
                if ([string]::IsNullOrEmpty($entry.Name)) {
                    New-Item -ItemType Directory -Path $target -Force | Out-Null
                    continue
                }
                New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
                $input = $entry.Open()
                try {
                    $output = [IO.File]::Open($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                    try { $input.CopyTo($output) } finally { $output.Dispose() }
                }
                finally { $input.Dispose() }
            }
        }
        finally { $archive.Dispose() }

        $roots = @(Get-ChildItem -LiteralPath $extractionRoot -Directory)
        if ($roots.Count -ne 1 -or -not (Test-Path -LiteralPath (Join-Path $roots[0].FullName 'package-manifest.json'))) {
            throw 'The package archive must contain exactly one manifest-bearing root directory.'
        }
        $packageRoot = $roots[0].FullName
    }
    elseif (-not (Test-Path -LiteralPath $fullPackagePath -PathType Container)) {
        throw 'PackagePath must be an extracted package directory or a .zip archive.'
    }

    $manifestPath = Join-Path $packageRoot 'package-manifest.json'
    $sumsPath = Join-Path $packageRoot 'SHA256SUMS.txt'
    if (-not (Test-Path -LiteralPath $manifestPath) -or -not (Test-Path -LiteralPath $sumsPath)) {
        throw 'The package manifest or SHA256SUMS.txt is missing.'
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.product -ne 'PulseGen' -or
        $manifest.runtimeIdentifier -ne 'win-x64' -or $manifest.packageType -ne 'portable-self-contained') {
        throw 'The package manifest identity or schema is invalid.'
    }

    $declared = @{}
    foreach ($file in @($manifest.files)) {
        $relative = [string]$file.path
        if ([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative) -or
            $relative.Split('/') -contains '..' -or $declared.ContainsKey($relative)) {
            throw "The package manifest contains an unsafe or duplicate path: $relative"
        }
        $target = [IO.Path]::GetFullPath((Join-Path $packageRoot $relative.Replace('/', [IO.Path]::DirectorySeparatorChar)))
        $packagePrefix = [IO.Path]::GetFullPath($packageRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $target.StartsWith($packagePrefix, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $target -PathType Leaf)) {
            throw "A declared package file is missing or unsafe: $relative"
        }
        $item = Get-Item -LiteralPath $target
        $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($item.Length -ne [long]$file.bytes -or $hash -ne [string]$file.sha256) {
            throw "Package payload verification failed: $relative"
        }
        $declared[$relative] = $hash
    }

    $actualPayload = @(Get-ChildItem -LiteralPath $packageRoot -File -Recurse | ForEach-Object {
        [IO.Path]::GetRelativePath($packageRoot, $_.FullName).Replace([IO.Path]::DirectorySeparatorChar, '/')
    } | Where-Object { $_ -notin @('package-manifest.json', 'SHA256SUMS.txt') })
    if ($actualPayload.Count -ne $declared.Count -or @($actualPayload | Where-Object { -not $declared.ContainsKey($_) }).Count -ne 0) {
        throw 'The package contains undeclared payload files or omits declared files.'
    }

    $sumEntries = @{}
    foreach ($line in Get-Content -LiteralPath $sumsPath) {
        if ($line -notmatch '^([0-9a-f]{64})  (.+)$') {
            throw "Invalid SHA256SUMS entry: $line"
        }
        if ($sumEntries.ContainsKey($Matches[2])) {
            throw "Duplicate SHA256SUMS entry: $($Matches[2])"
        }
        $sumEntries[$Matches[2]] = $Matches[1]
    }
    $sumTargets = @(Get-ChildItem -LiteralPath $packageRoot -File -Recurse | ForEach-Object {
        [IO.Path]::GetRelativePath($packageRoot, $_.FullName).Replace([IO.Path]::DirectorySeparatorChar, '/')
    } | Where-Object { $_ -ne 'SHA256SUMS.txt' })
    if ($sumTargets.Count -ne $sumEntries.Count) {
        throw 'SHA256SUMS.txt does not cover every package file exactly once.'
    }
    foreach ($relative in $sumTargets) {
        if (-not $sumEntries.ContainsKey($relative)) {
            throw "SHA256SUMS.txt omits $relative"
        }
        $target = Join-Path $packageRoot $relative.Replace('/', [IO.Path]::DirectorySeparatorChar)
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -ne $sumEntries[$relative]) {
            throw "SHA256SUMS verification failed: $relative"
        }
    }

    $selfTest = $null
    if (-not $SkipRuntimeSmoke) {
        $entryPoint = Join-Path $packageRoot ([string]$manifest.entryPoint)
        if (-not (Test-Path -LiteralPath $entryPoint -PathType Leaf)) {
            throw "The packaged entry point is missing: $entryPoint"
        }
        $selfTestReceipt = Join-Path $verificationOutput 'package-self-test.json'
        $renderOutput = Join-Path $verificationOutput 'render'
        $startInfo = [Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $entryPoint
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.WorkingDirectory = $packageRoot
        $startInfo.ArgumentList.Add('--package-self-test')
        $startInfo.ArgumentList.Add('--receipt')
        $startInfo.ArgumentList.Add($selfTestReceipt)
        $startInfo.ArgumentList.Add('--output')
        $startInfo.ArgumentList.Add($renderOutput)
        $process = [Diagnostics.Process]::Start($startInfo)
        if ($null -eq $process) {
            throw 'The packaged PulseGen process could not be started.'
        }
        try {
            if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
                $process.Kill($true)
                throw "The packaged self-test exceeded $TimeoutSeconds seconds."
            }
            if ($process.ExitCode -ne 0) {
                $failure = if (Test-Path -LiteralPath $selfTestReceipt) { Get-Content -LiteralPath $selfTestReceipt -Raw } else { 'No receipt was written.' }
                throw "The packaged self-test failed with exit code $($process.ExitCode). $failure"
            }
        }
        finally { $process.Dispose() }

        $selfTest = Get-Content -LiteralPath $selfTestReceipt -Raw | ConvertFrom-Json
        if ($selfTest.marker -ne 'PULSEGEN_PACKAGE_SELF_TEST_OK' -or
            @($selfTest.checks | Where-Object { -not $_.passed }).Count -ne 0 -or
            @(Get-ChildItem -LiteralPath $renderOutput -Filter '*.wav').Count -ne 2) {
            throw 'The packaged runtime self-test receipt is incomplete or unsuccessful.'
        }

        $postSmokePayload = @(Get-ChildItem -LiteralPath $packageRoot -File -Recurse | ForEach-Object {
            [IO.Path]::GetRelativePath($packageRoot, $_.FullName).Replace([IO.Path]::DirectorySeparatorChar, '/')
        } | Where-Object { $_ -notin @('package-manifest.json', 'SHA256SUMS.txt') })
        if ($postSmokePayload.Count -ne $declared.Count -or
            @($postSmokePayload | Where-Object { -not $declared.ContainsKey($_) }).Count -ne 0) {
            throw 'The packaged runtime changed or added payload files during its self-test.'
        }
        foreach ($relative in $postSmokePayload) {
            $target = Join-Path $packageRoot $relative.Replace('/', [IO.Path]::DirectorySeparatorChar)
            if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -ne $declared[$relative]) {
                throw "The packaged runtime changed $relative during its self-test."
            }
        }
    }

    $verificationReceipt = [ordered]@{
        schemaVersion = 1
        marker = 'PULSEGEN_PACKAGE_VERIFY_OK'
        product = 'PulseGen'
        version = $manifest.version
        runtimeIdentifier = $manifest.runtimeIdentifier
        packageSha256 = if (Test-Path -LiteralPath $fullPackagePath -PathType Leaf) { (Get-FileHash -LiteralPath $fullPackagePath -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
        payloadFiles = $declared.Count
        payloadBytes = [long](($manifest.files | Measure-Object -Property bytes -Sum).Sum)
        runtimeSmoke = -not [bool]$SkipRuntimeSmoke
        selfTestMarker = $selfTest.marker
        createdAtUtc = [DateTimeOffset]::UtcNow
    }
    $verificationReceiptPath = Join-Path $verificationOutput 'package-verification.json'
    [IO.File]::WriteAllText($verificationReceiptPath, ($verificationReceipt | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    Write-Output "PULSEGEN_PACKAGE_VERIFY_OK version=$($manifest.version) files=$($declared.Count) receipt=$verificationReceiptPath"
}
finally {
    if ($null -ne $extractionRoot -and (Test-Path -LiteralPath $extractionRoot)) {
        Remove-Item -LiteralPath (Assert-ChildPath -Path $extractionRoot -Parent $verificationRoot) -Recurse -Force
    }
}
