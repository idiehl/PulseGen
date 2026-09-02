[CmdletBinding()]
param(
    [ValidateRange(2, 200)]
    [int]$Queries = 12,
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
            throw 'The Release build failed before the reflection-monitor stress run.'
        }
    }

    $fixtureProject = Join-Path $repositoryRoot 'out\viewport-benchmark\ImportedMesh250K.pulseproj'
    if (-not (Test-Path -LiteralPath (Join-Path $fixtureProject 'project.json'))) {
        & (Join-Path $PSScriptRoot 'run-viewport-stress.ps1') `
            -Seconds 3 `
            -MinimumFramesPerSecond 30 `
            -ImportedMesh `
            -NoBuild
        if ($LASTEXITCODE -ne 0) {
            throw 'The deterministic 250,000-triangle imported-mesh fixture could not be prepared.'
        }
    }

    $receipt = Join-Path $repositoryRoot 'out\reflection-monitor-stress\reflection-monitor-receipt.json'
    if (Test-Path -LiteralPath $receipt) {
        Remove-Item -LiteralPath $receipt -Force
    }
    dotnet managed\PulseGen.RenderCli\bin\Release\net10.0\PulseGen.RenderCli.dll `
        --project $fixtureProject `
        --reflection-monitor-queries $Queries `
        --enable-imported-reflections `
        --add-benchmark-reflection-plane `
        --reflection-monitor-receipt $receipt `
        --no-render
    if ($LASTEXITCODE -ne 0) {
        if (Test-Path -LiteralPath $receipt) {
            Get-Content -LiteralPath $receipt
        }
        throw 'The retained reflection-monitor stress run failed.'
    }

    $result = Get-Content -LiteralPath $receipt -Raw | ConvertFrom-Json
    if ($result.marker -ne 'PULSEGEN_REFLECTION_MONITOR_STRESS_OK' -or
        -not $result.deterministic -or
        $result.configurationCount -ne 1 -or
        $result.retainedQueryCount -ne $Queries -or
        $result.retainedCompletedQueryCount -ne ($Queries + 2) -or
        $result.importedMeshCount -lt 1 -or
        $result.probeRouteCount -lt 1 -or
        $result.speedupVersusOneShot -le 1) {
        throw "The reflection-monitor receipt failed its lifecycle, determinism, or speedup checks: $receipt"
    }
    $result | ConvertTo-Json -Depth 5 -Compress
}
finally {
    Set-Location -LiteralPath $originalLocation
}
