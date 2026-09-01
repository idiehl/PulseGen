[CmdletBinding()]
param(
    [ValidateRange(1, 6)]
    [int]$Seconds = 6,
    [ValidateSet(0, 1, 2)]
    [int[]]$ReceiverIndex = @(0, 1, 2),
    [string]$ReceiptPath,
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

    $diagnostics = @()
    foreach ($receiver in $ReceiverIndex) {
        $outputLines = @(dotnet run --project managed\PulseGen.RenderCli\PulseGen.RenderCli.csproj -c Release --no-build -- `
            --sample realtime-stress `
            --preview-seconds $Seconds `
            --receiver-index $receiver `
            --require-clean-audio `
            --no-render)
        $exitCode = $LASTEXITCODE
        $outputLines | Write-Output
        if ($exitCode -ne 0) {
            throw "The real-time stress run failed for receiver index $receiver."
        }
        $diagnosticLine = $outputLines |
            Where-Object { $_ -match '^\{"marker":"PULSEGEN_PREVIEW_DIAGNOSTICS_OK"' } |
            Select-Object -Last 1
        if (-not $diagnosticLine) {
            throw "The real-time stress run for receiver index $receiver did not emit its machine-readable diagnostic."
        }
        $diagnostics += $diagnosticLine | ConvertFrom-Json
    }

    $resolvedReceipt = if ([string]::IsNullOrWhiteSpace($ReceiptPath)) {
        Join-Path $repositoryRoot 'out\realtime-stress\realtime-stress-receipt.json'
    } elseif ([IO.Path]::IsPathRooted($ReceiptPath)) {
        [IO.Path]::GetFullPath($ReceiptPath)
    } else {
        [IO.Path]::GetFullPath((Join-Path $repositoryRoot $ReceiptPath))
    }
    $receiptDirectory = Split-Path -Parent $resolvedReceipt
    [IO.Directory]::CreateDirectory($receiptDirectory) | Out-Null
    $receipt = [ordered]@{
        marker = 'PULSEGEN_REALTIME_STRESS_OK'
        generatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        engineVersion = $diagnostics[0].engine
        scene = $diagnostics[0].scene
        previewSecondsPerReceiver = $Seconds
        requestedReceiverIndices = @($ReceiverIndex)
        sampleRate = $diagnostics[0].SampleRate
        periodFrames = $diagnostics[0].PeriodFrames
        emitterCount = $diagnostics[0].emitterCount
        receiverCount = $diagnostics[0].receiverCount
        deadlineMissCountTotal = [uint64](($diagnostics | Measure-Object -Property CallbackDeadlineMissCount -Sum).Sum)
        callbackLoadPercentMaximum = [double](($diagnostics | Measure-Object -Property CallbackLoadPercentMaximum -Maximum).Maximum)
        runs = @($diagnostics)
    }
    $temporaryReceipt = "$resolvedReceipt.tmp-$PID"
    try {
        $json = $receipt | ConvertTo-Json -Depth 8
        [IO.File]::WriteAllText($temporaryReceipt, $json, [Text.UTF8Encoding]::new($false))
        [IO.File]::Move($temporaryReceipt, $resolvedReceipt, $true)
    }
    finally {
        if (Test-Path -LiteralPath $temporaryReceipt) {
            Remove-Item -LiteralPath $temporaryReceipt -Force
        }
    }
    [ordered]@{
        marker = 'PULSEGEN_REALTIME_STRESS_OK'
        receipt = $resolvedReceipt
        receiverRuns = $diagnostics.Count
        deadlineMissCountTotal = $receipt.deadlineMissCountTotal
        callbackLoadPercentMaximum = $receipt.callbackLoadPercentMaximum
    } | ConvertTo-Json -Compress
}
finally {
    Set-Location -LiteralPath $originalLocation
}
