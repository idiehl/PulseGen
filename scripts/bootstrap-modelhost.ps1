[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $repositoryRoot 'modelhost\runtime.manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$runtimeRoot = Join-Path $repositoryRoot '.runtime\modelhost\python311'
$downloadRoot = Join-Path $repositoryRoot '.runtime\downloads'
$pythonExecutable = Join-Path $runtimeRoot 'python.exe'

if ((Test-Path -LiteralPath $pythonExecutable) -and -not $Force) {
    & $pythonExecutable -c "import numpy,sys; print(f'PULSEGEN_MODELHOST_RUNTIME_OK python={sys.version.split()[0]} numpy={numpy.__version__}')"
    exit $LASTEXITCODE
}

New-Item -ItemType Directory -Path $downloadRoot -Force | Out-Null
New-Item -ItemType Directory -Path $runtimeRoot -Force | Out-Null

function Get-VerifiedArtifact {
    param([object]$Artifact, [string]$FileName)
    $target = Join-Path $downloadRoot $FileName
    if (-not (Test-Path -LiteralPath $target)) {
        Invoke-WebRequest -Uri $Artifact.source -OutFile $target
    }
    $item = Get-Item -LiteralPath $target
    $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($item.Length -ne $Artifact.sizeBytes -or $hash -ne $Artifact.sha256) {
        throw "Artifact verification failed for $FileName. Expected $($Artifact.sha256), received $hash."
    }
    return $target
}

$pythonArchive = Get-VerifiedArtifact -Artifact $manifest.runtime -FileName 'python-3.11.9-embeddable-amd64.zip'
Expand-Archive -LiteralPath $pythonArchive -DestinationPath $runtimeRoot -Force

$sitePackages = Join-Path $runtimeRoot 'Lib\site-packages'
New-Item -ItemType Directory -Path $sitePackages -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($package in $manifest.packages) {
    $wheel = Get-VerifiedArtifact -Artifact $package -FileName $package.fileName
    [System.IO.Compression.ZipFile]::ExtractToDirectory($wheel, $sitePackages, $true)
}

$pathConfiguration = Join-Path $runtimeRoot 'python311._pth'
$pathLines = @(
    'python311.zip'
    '.'
    'Lib\site-packages'
    '..\..\..\modelhost'
    'import site'
)
[IO.File]::WriteAllLines($pathConfiguration, $pathLines, [Text.UTF8Encoding]::new($false))

& $pythonExecutable -c "import numpy,sys; print(f'PULSEGEN_MODELHOST_RUNTIME_OK python={sys.version.split()[0]} numpy={numpy.__version__}')"
if ($LASTEXITCODE -ne 0) {
    throw 'Application-managed ModelHost runtime self-check failed.'
}
