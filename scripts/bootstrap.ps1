[CmdletBinding()]
param(
    [switch]$InstallMissing,
    [switch]$IncludeModelHost
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'

function Require-Command {
    param([string]$Name, [string]$WingetId)
    if (Get-Command $Name -ErrorAction SilentlyContinue) {
        return
    }
    if (-not $InstallMissing) {
        throw "$Name is missing. Re-run with -InstallMissing to install $WingetId."
    }
    winget install --id $WingetId --exact --silent --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0) {
        throw "winget failed to install $WingetId."
    }
}

Require-Command -Name 'dotnet' -WingetId 'Microsoft.DotNet.SDK.10'
$dotnetVersion = dotnet --version
if (-not $dotnetVersion.StartsWith('10.', [StringComparison]::Ordinal)) {
    throw "PulseGen requires .NET SDK 10; found $dotnetVersion."
}

$installation = if (Test-Path -LiteralPath $vswhere) {
    & $vswhere -latest -products Microsoft.VisualStudio.Product.BuildTools -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
}
if (-not $installation) {
    if (-not $InstallMissing) {
        throw 'MSVC Build Tools with the x64 C++ workload are missing. Re-run with -InstallMissing.'
    }
    winget install --id Microsoft.VisualStudio.2022.BuildTools --exact --silent --accept-package-agreements --accept-source-agreements --override '--wait --quiet --norestart --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended'
    if ($LASTEXITCODE -ne 0) {
        throw 'Visual Studio Build Tools installation failed.'
    }
    $installation = & $vswhere -latest -products Microsoft.VisualStudio.Product.BuildTools -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
}

$cmake = Join-Path $installation 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
$ninja = Join-Path $installation 'Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe'
$vcvars = Join-Path $installation 'VC\Auxiliary\Build\vcvars64.bat'
foreach ($path in ($cmake, $ninja, $vcvars)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Required native build component is missing: $path"
    }
}

if ($IncludeModelHost) {
    & (Join-Path $PSScriptRoot 'bootstrap-modelhost.ps1')
    if ($LASTEXITCODE -ne 0) {
        throw 'ModelHost runtime bootstrap failed.'
    }
}

Write-Output "PULSEGEN_BOOTSTRAP_OK dotnet=$dotnetVersion vs=$installation"
