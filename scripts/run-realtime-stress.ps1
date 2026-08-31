[CmdletBinding()]
param(
    [ValidateRange(1, 6)]
    [int]$Seconds = 6,
    [ValidateSet(0, 1, 2)]
    [int[]]$ReceiverIndex = @(0, 1, 2),
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
            throw 'The Release build failed before the real-time stress run.'
        }
    }

    foreach ($receiver in $ReceiverIndex) {
        dotnet run --project managed\PulseGen.RenderCli\PulseGen.RenderCli.csproj -c Release --no-build -- `
            --sample realtime-stress `
            --preview-seconds $Seconds `
            --receiver-index $receiver `
            --require-clean-audio `
            --no-render
        if ($LASTEXITCODE -ne 0) {
            throw "The real-time stress run failed for receiver index $receiver."
        }
    }
}
finally {
    Set-Location -LiteralPath $originalLocation
}
