[CmdletBinding()]
param(
    [ValidateRange(3, 15)]
    [int]$Seconds = 6,
    [ValidateRange(30, 120)]
    [double]$MinimumFramesPerSecond = 57,
    [switch]$ImportedMesh,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$originalLocation = Get-Location
try {
    Set-Location -LiteralPath $repositoryRoot
    if (-not $NoBuild) {
        & (Join-Path $PSScriptRoot 'build.ps1') -Configuration Release
        if ($LASTEXITCODE -ne 0) {
            throw 'The Release build failed before the viewport stress run.'
        }
    }

    $receiptName = if ($ImportedMesh) { 'imported-mesh-viewport-receipt.json' } else { 'viewport-receipt.json' }
    $receipt = Join-Path $repositoryRoot "out\viewport-benchmark\$receiptName"
    if (Test-Path -LiteralPath $receipt) {
        Remove-Item -LiteralPath $receipt -Force
    }
    $benchmarkArguments = @(
        'managed\PulseGen.Editor\bin\Release\net10.0\PulseGen.dll',
        '--viewport-benchmark',
        '--seconds', $Seconds,
        '--minimum-fps', $MinimumFramesPerSecond,
        '--receipt', $receipt
    )
    if ($ImportedMesh) {
        $benchmarkArguments += '--imported-mesh'
    }
    dotnet @benchmarkArguments
    if ($LASTEXITCODE -ne 0) {
        if (Test-Path -LiteralPath $receipt) {
            Get-Content -LiteralPath $receipt
        }
        $source = if ($ImportedMesh) { 'imported-mesh' } else { 'terrain' }
        throw "The 250,000-triangle $source viewport stress run failed."
    }
    $result = Get-Content -LiteralPath $receipt -Raw | ConvertFrom-Json
    if ($result.marker -ne 'PULSEGEN_VIEWPORT_STRESS_OK') {
        throw "The viewport receipt reported '$($result.marker)'."
    }
    $result | ConvertTo-Json -Depth 4 -Compress
}
finally {
    Set-Location -LiteralPath $originalLocation
}
