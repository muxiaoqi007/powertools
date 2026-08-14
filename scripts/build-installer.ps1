param(
    [string]$Version = "0.9.0",
    [string]$Runtime = "win-x64",
    [switch]$FrameworkDependent,
    [switch]$SkipPublish,
    [string]$InnoCompiler
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$packageRoot = Join-Path $repoRoot "artifacts\desktop-$Runtime"
$outputRoot = Join-Path $repoRoot "artifacts\installer"
$installerDefinition = Join-Path $repoRoot "installer\PowerTools.iss"

if (-not $SkipPublish) {
    $publishParameters = @{ Runtime = $Runtime; Version = $Version }
    if (-not $FrameworkDependent) { $publishParameters.SelfContained = $true }
    & (Join-Path $PSScriptRoot "publish-desktop.ps1") @publishParameters
    if ($LASTEXITCODE -ne 0) { throw "Desktop publishing failed with exit code $LASTEXITCODE." }
}

if (-not (Test-Path -LiteralPath (Join-Path $packageRoot "PowerTools.Desktop.exe"))) {
    throw "Desktop package not found: $packageRoot"
}

if ([string]::IsNullOrWhiteSpace($InnoCompiler)) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    $candidates = @(
        $command.Source,
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_) }
    $InnoCompiler = $candidates | Select-Object -First 1
}

if ([string]::IsNullOrWhiteSpace($InnoCompiler) -or -not (Test-Path -LiteralPath $InnoCompiler)) {
    throw "Inno Setup 6 compiler was not found. Install it with: winget install --id JRSoftware.InnoSetup --exact"
}

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
& $InnoCompiler "/DAppVersion=$Version" "/DSourceDir=$packageRoot" "/DOutputDir=$outputRoot" $installerDefinition
if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed with exit code $LASTEXITCODE." }

$installer = Join-Path $outputRoot "PowerTools-Setup-$Version-win-x64.exe"
if (-not (Test-Path -LiteralPath $installer)) { throw "Expected installer was not created: $installer" }
Write-Host "Installer: $installer"
