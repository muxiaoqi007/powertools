param(
    [string]$PackagePath,
    [switch]$Unregister
)

$ErrorActionPreference = "Stop"
$targetRoot = ${env:CommonProgramFiles(x86)}
if ([string]::IsNullOrWhiteSpace($targetRoot)) {
    throw "Common Program Files (x86) was not found."
}
$targetFolder = Join-Path $targetRoot "Microsoft Shared\Power BI Desktop\External Tools"
$manifestPath = Join-Path $targetFolder "PowerTools.pbitool.json"

if ($Unregister) {
    if (Test-Path -LiteralPath $manifestPath) { Remove-Item -LiteralPath $manifestPath -Force }
    Write-Host "PowerTools was removed from Power BI External Tools."
    exit 0
}

if ([string]::IsNullOrWhiteSpace($PackagePath)) {
    $localExecutable = Join-Path $PSScriptRoot "PowerTools.Desktop.exe"
    $PackagePath = if (Test-Path -LiteralPath $localExecutable) {
        $PSScriptRoot
    } else {
        Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts\desktop-win-x64"
    }
}

$executable = Join-Path ([IO.Path]::GetFullPath($PackagePath)) "PowerTools.Desktop.exe"
if (-not (Test-Path -LiteralPath $executable)) {
    throw "Desktop executable not found: $executable. Run scripts\publish-desktop.ps1 first."
}

New-Item -ItemType Directory -Path $targetFolder -Force | Out-Null
$manifest = [ordered]@{
    version = "1.0.0"
    name = "PowerTools"
    description = "Read-only Power BI model, report quality and optimization analysis"
    path = $executable
    arguments = '--server "%server%" --database "%database%"'
    iconData = "image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsEAAA7BAbiRa+0AAAANSURBVBhXY2BgYPgPAAEEAQB9ssjfAAAAAElFTkSuQmCC"
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding UTF8
Write-Host "Registered: $manifestPath"
Write-Host "Restart Power BI Desktop and select PowerTools from External Tools."
