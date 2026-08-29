[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'out\verification'))

function Remove-VerificationDirectory {
    param([string]$Path)
    $resolved = [IO.Path]::GetFullPath($Path)
    $prefix = $outputRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing cleanup outside verification output: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

if (-not $NoBuild) {
    & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw 'Build phase failed.'
    }
}

$vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
$installation = & $vswhere -latest -products Microsoft.VisualStudio.Product.BuildTools -property installationPath
$ctest = Join-Path $installation 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\ctest.exe'
$preset = "windows-$($Configuration.ToLowerInvariant())"
$originalLocation = Get-Location
try {
    Set-Location -LiteralPath $repositoryRoot
    & $ctest --preset $preset
    if ($LASTEXITCODE -ne 0) {
        throw 'Native tests failed.'
    }
    dotnet test PulseGen.sln -c $Configuration --no-build --logger 'console;verbosity=minimal'
    if ($LASTEXITCODE -ne 0) {
        throw 'Managed tests failed.'
    }

    $runtimePython = Join-Path $repositoryRoot '.runtime\modelhost\python311\python.exe'
    $python = if (Test-Path -LiteralPath $runtimePython) { $runtimePython } else { (Get-Command python -ErrorAction Stop).Source }
    & $python -m unittest discover -s modelhost\tests -v
    if ($LASTEXITCODE -ne 0) {
        throw 'ModelHost tests failed.'
    }

    $firstRender = Join-Path $outputRoot 'flyby-a'
    $secondRender = Join-Path $outputRoot 'flyby-b'
    Remove-VerificationDirectory $firstRender
    Remove-VerificationDirectory $secondRender
    dotnet run --project managed\PulseGen.RenderCli\PulseGen.RenderCli.csproj -c $Configuration --no-build -- --output $firstRender
    if ($LASTEXITCODE -ne 0) { throw 'First deterministic render failed.' }
    dotnet run --project managed\PulseGen.RenderCli\PulseGen.RenderCli.csproj -c $Configuration --no-build -- --output $secondRender
    if ($LASTEXITCODE -ne 0) { throw 'Second deterministic render failed.' }

    $firstFiles = Get-ChildItem -LiteralPath $firstRender -Filter '*.wav' | Sort-Object Name
    $secondFiles = Get-ChildItem -LiteralPath $secondRender -Filter '*.wav' | Sort-Object Name
    if ($firstFiles.Count -ne 2 -or $secondFiles.Count -ne 2) {
        throw 'Expected exactly two isolated stereo receiver takes per render.'
    }
    for ($index = 0; $index -lt $firstFiles.Count; $index++) {
        if ($firstFiles[$index].Name -ne $secondFiles[$index].Name) {
            throw 'Deterministic render file sets differ.'
        }
        $firstHash = (Get-FileHash -LiteralPath $firstFiles[$index].FullName -Algorithm SHA256).Hash
        $secondHash = (Get-FileHash -LiteralPath $secondFiles[$index].FullName -Algorithm SHA256).Hash
        if ($firstHash -ne $secondHash) {
            throw "Rendered take is not bit-stable: $($firstFiles[$index].Name)"
        }
        $stream = [IO.File]::OpenRead($firstFiles[$index].FullName)
        try {
            $reader = [IO.BinaryReader]::new($stream)
            $riff = [Text.Encoding]::ASCII.GetString($reader.ReadBytes(4))
            $stream.Position = 22
            $channels = $reader.ReadUInt16()
            $sampleRate = $reader.ReadUInt32()
            if ($riff -ne 'RIFF' -or $channels -ne 2 -or $sampleRate -ne 48000) {
                throw "Invalid receiver take header: $($firstFiles[$index].Name)"
            }
        }
        finally {
            $stream.Dispose()
        }
    }
    Write-Output "PULSEGEN_VERIFY_OK configuration=$Configuration native=passed managed=passed modelhost=passed renders=bit-stable"
}
finally {
    Set-Location -LiteralPath $originalLocation
}
