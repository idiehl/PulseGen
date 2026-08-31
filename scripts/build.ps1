[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw 'Visual Studio Installer discovery tool is missing. Run scripts/bootstrap.ps1.'
}
$installation = & $vswhere -latest -products Microsoft.VisualStudio.Product.BuildTools -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installation) {
    throw 'MSVC C++ Build Tools are missing. Run scripts/bootstrap.ps1.'
}
$cmakeDirectory = Join-Path $installation 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin'
$ninjaDirectory = Join-Path $installation 'Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja'
$vcvars = Join-Path $installation 'VC\Auxiliary\Build\vcvars64.bat'
$preset = "windows-$($Configuration.ToLowerInvariant())"
$nativeCache = Join-Path $repositoryRoot "out\native\$($Configuration.ToLowerInvariant())\CMakeCache.txt"
$refreshConfigure = ''
if (Test-Path -LiteralPath $nativeCache) {
    $cachedCompiler = Select-String -LiteralPath $nativeCache -Pattern '^CMAKE_C_COMPILER:FILEPATH=' -ErrorAction SilentlyContinue
    if ($null -eq $cachedCompiler -or $cachedCompiler.Line.EndsWith('NOTFOUND', [StringComparison]::Ordinal)) {
        $refreshConfigure = ' --fresh'
    }
}
$originalLocation = Get-Location
try {
    Set-Location -LiteralPath $repositoryRoot
    & (Join-Path $PSScriptRoot 'bootstrap-codecs.ps1')
    if ($LASTEXITCODE -ne 0) {
        throw 'Codec runtime bootstrap failed.'
    }
    # Expand the caller PATH before vcvars augments it. Appending %PATH% after
    # vcvars on the same cmd.exe line expands too early and silently discards cl.exe.
    $nativeCommand = "set `"PATH=$cmakeDirectory;$ninjaDirectory;%PATH%`" && call `"$vcvars`" && cmake$refreshConfigure --preset $preset && cmake --build --preset $preset"
    & cmd.exe /d /s /c $nativeCommand
    if ($LASTEXITCODE -ne 0) {
        throw "Native $Configuration build failed."
    }

    if (-not $NoRestore) {
        dotnet restore PulseGen.sln
        if ($LASTEXITCODE -ne 0) {
            throw 'Managed dependency restore failed.'
        }
    }
    dotnet build PulseGen.sln -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Managed $Configuration build failed."
    }

    $nativeDll = Join-Path $repositoryRoot "out\native\$($Configuration.ToLowerInvariant())\native\PulseGen.Engine.dll"
    foreach ($project in ('PulseGen.Editor', 'PulseGen.RenderCli')) {
        $destination = Join-Path $repositoryRoot "managed\$project\bin\$Configuration\net10.0"
        Copy-Item -LiteralPath $nativeDll -Destination $destination -Force
    }
    Write-Output "PULSEGEN_BUILD_OK configuration=$Configuration"
}
finally {
    Set-Location -LiteralPath $originalLocation
}
