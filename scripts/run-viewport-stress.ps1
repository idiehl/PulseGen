[CmdletBinding()]
param(
    [ValidateRange(3, 15)]
    [int]$Seconds = 6,
    [ValidateRange(30, 120)]
    [double]$MinimumFramesPerSecond = 57,
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

    $receipt = Join-Path $repositoryRoot 'out\viewport-benchmark\viewport-receipt.json'
    if (Test-Path -LiteralPath $receipt) {
        Remove-Item -LiteralPath $receipt -Force
    }
    dotnet managed\PulseGen.Editor\bin\Release\net10.0\PulseGen.Editor.dll `
        --viewport-benchmark `
        --seconds $Seconds `
        --minimum-fps $MinimumFramesPerSecond `
        --receipt $receipt
    if ($LASTEXITCODE -ne 0) {
        if (Test-Path -LiteralPath $receipt) {
            Get-Content -LiteralPath $receipt
        }
        throw 'The 250,000-triangle viewport stress run failed.'
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
