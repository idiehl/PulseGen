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
            $stream.Position = 20
            $formatCode = $reader.ReadUInt16()
            $stream.Position = 22
            $channels = $reader.ReadUInt16()
            $sampleRate = $reader.ReadUInt32()
            $stream.Position = 34
            $bitsPerSample = $reader.ReadUInt16()
            if ($riff -ne 'RIFF' -or $formatCode -ne 3 -or $channels -ne 2 -or $sampleRate -ne 48000 -or $bitsPerSample -ne 32) {
                throw "Invalid receiver take header: $($firstFiles[$index].Name)"
            }
        }
        finally {
            $stream.Dispose()
        }
    }
    $firstReceipts = @(Get-ChildItem -LiteralPath $firstRender -Filter '*-render-receipt.json')
    $secondReceipts = @(Get-ChildItem -LiteralPath $secondRender -Filter '*-render-receipt.json')
    if ($firstReceipts.Count -ne 1 -or $secondReceipts.Count -ne 1) {
        throw 'Expected exactly one provenance receipt per offline render.'
    }
    if ((Get-FileHash -LiteralPath $firstReceipts[0].FullName -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $secondReceipts[0].FullName -Algorithm SHA256).Hash) {
        throw 'Deterministic render receipts differ.'
    }
    $renderReceipt = Get-Content -LiteralPath $firstReceipts[0].FullName -Raw | ConvertFrom-Json
    if ($renderReceipt.receiptVersion -ne 1 -or $renderReceipt.sceneRevisionSha256.Length -ne 64 -or
        $renderReceipt.engineVersion -notmatch '^0\.1\.0-alpha' -or $renderReceipt.sampleRate -ne 48000 -or
        $renderReceipt.sampleFormat -ne 'Float32' -or $renderReceipt.dither -ne 'None' -or
        @($renderReceipt.takes).Count -ne 2 -or $renderReceipt.dependencies[1].name -ne 'miniaudio' -or
        $renderReceipt.dependencies[1].version -ne '0.11.25') {
        throw 'Render provenance receipt is incomplete or inconsistent.'
    }
    foreach ($take in $renderReceipt.takes) {
        $takePath = Join-Path $firstRender $take.fileName
        if (-not (Test-Path -LiteralPath $takePath) -or
            (Get-FileHash -LiteralPath $takePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $take.sha256) {
            throw "Render receipt take hash is invalid: $($take.fileName)"
        }
    }
    $pcmProfiles = @(
        @{ Name = 'Pcm16'; Code = 1; Bits = 16 },
        @{ Name = 'Pcm24'; Code = 1; Bits = 24 }
    )
    foreach ($pcmProfile in $pcmProfiles) {
        $pcmOutput = Join-Path $outputRoot ("format-" + $pcmProfile.Name.ToLowerInvariant())
        Remove-VerificationDirectory $pcmOutput
        dotnet run --project managed\PulseGen.RenderCli\PulseGen.RenderCli.csproj -c $Configuration --no-build -- --output $pcmOutput --duration 0.25 --sample-format $pcmProfile.Name
        if ($LASTEXITCODE -ne 0) { throw "$($pcmProfile.Name) render failed." }
        $pcmTake = Get-ChildItem -LiteralPath $pcmOutput -Filter '*.wav' | Select-Object -First 1
        $stream = [IO.File]::OpenRead($pcmTake.FullName)
        try {
            $reader = [IO.BinaryReader]::new($stream)
            $stream.Position = 20
            $formatCode = $reader.ReadUInt16()
            $stream.Position = 34
            $bitsPerSample = $reader.ReadUInt16()
            if ($formatCode -ne $pcmProfile.Code -or $bitsPerSample -ne $pcmProfile.Bits) {
                throw "$($pcmProfile.Name) render header is invalid."
            }
        }
        finally {
            $stream.Dispose()
        }
        $pcmReceipt = Get-Content -LiteralPath (Get-ChildItem -LiteralPath $pcmOutput -Filter '*-render-receipt.json').FullName -Raw | ConvertFrom-Json
        if ($pcmReceipt.sampleFormat -ne $pcmProfile.Name -or $pcmReceipt.dither -ne 'Tpdf' -or
            [Math]::Max($pcmReceipt.peakLeft, $pcmReceipt.peakRight) -le 0) {
            throw "$($pcmProfile.Name) receipt does not record explicit TPDF dither."
        }
    }
    Write-Output "PULSEGEN_VERIFY_OK configuration=$Configuration native=passed managed=passed modelhost=passed renders=bit-stable"
}
finally {
    Set-Location -LiteralPath $originalLocation
}
