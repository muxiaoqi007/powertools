param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "0.9.0",
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = Join-Path $repoRoot "artifacts\desktop-$Runtime"
$serverOutput = Join-Path $outputRoot "server"

if (Test-Path -LiteralPath $outputRoot) {
    Remove-Item -LiteralPath $outputRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $serverOutput -Force | Out-Null

$selfContainedValue = if ($SelfContained) { "true" } else { "false" }
dotnet publish (Join-Path $repoRoot "PowerTools.csproj") -c $Configuration -r $Runtime --self-contained $selfContainedValue -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false -p:SatelliteResourceLanguages=zh-Hans -o $serverOutput
dotnet publish (Join-Path $repoRoot "PowerTools.Desktop\PowerTools.Desktop.csproj") -c $Configuration -r $Runtime --self-contained $selfContainedValue -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false -p:SatelliteResourceLanguages=zh-Hans -o $outputRoot
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "register-external-tool.ps1") -Destination $outputRoot -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "install-external-tool.cmd") -Destination $outputRoot -Force

Write-Host "Desktop package: $outputRoot"
