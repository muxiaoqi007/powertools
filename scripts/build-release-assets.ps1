param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$BaseVersion,
    [string]$BaselineDirectory,
    [string]$Runtime = "win-x64",
    [switch]$FrameworkDependent,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
if ($Version -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$') { throw "Version must be numeric." }
if (-not [string]::IsNullOrWhiteSpace($BaseVersion) -and $BaseVersion -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$') { throw "BaseVersion must be numeric." }
if ($Runtime -notmatch '^[A-Za-z0-9-]+$') { throw "Runtime contains invalid characters." }
$repoRoot = Split-Path -Parent $PSScriptRoot
$desktopRoot = Join-Path $repoRoot "artifacts\desktop-$Runtime"
$releaseRoot = Join-Path $repoRoot "artifacts\release-$Version"
if (Test-Path -LiteralPath $releaseRoot) { Remove-Item -LiteralPath $releaseRoot -Recurse -Force }
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null

$installerParameters = @{ Version = $Version; Runtime = $Runtime }
if (-not $SkipBuild) {
    if ($FrameworkDependent) { $installerParameters.FrameworkDependent = $true }
    & (Join-Path $PSScriptRoot "build-installer.ps1") @installerParameters
    if ($LASTEXITCODE -ne 0) { throw "Installer build failed." }
}

$installer = Join-Path $repoRoot "artifacts\installer\PowerTools-Setup-$Version-win-x64.exe"
if (-not (Test-Path -LiteralPath $installer)) { throw "Installer not found: $installer" }
if (-not (Test-Path -LiteralPath $desktopRoot)) { throw "Desktop package not found: $desktopRoot" }
Copy-Item -LiteralPath $installer -Destination $releaseRoot -Force
$portable = Join-Path $releaseRoot "PowerTools-Desktop-$Version-$Runtime.zip"
Compress-Archive -Path (Join-Path $desktopRoot "*") -DestinationPath $portable -CompressionLevel Optimal

if (-not [string]::IsNullOrWhiteSpace($BaseVersion) -and -not [string]::IsNullOrWhiteSpace($BaselineDirectory)) {
    $delta = & (Join-Path $PSScriptRoot "build-update-package.ps1") -FromVersion $BaseVersion -ToVersion $Version -BaselineDirectory $BaselineDirectory -CurrentDirectory $desktopRoot -OutputDirectory $releaseRoot -Runtime $Runtime
    if ($LASTEXITCODE -ne 0) { throw "Delta package build failed." }
}

$updateAssets = @(Get-ChildItem -LiteralPath $releaseRoot -File | Where-Object { $_.Name -like "PowerTools-Setup-*" -or $_.Name -like "PowerTools-Delta-*" } | ForEach-Object {
    [ordered]@{
        name = $_.Name
        url = ('https://github.com/muxiaoqi007/powertools/releases/download/v' + $Version + '/' + $_.Name)
        size = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
})
$channel = [ordered]@{
    schemaVersion = 1
    version = $Version
    name = ('PowerTools ' + $Version)
    notes = 'See the GitHub Release page for complete update notes.'
    publishedAt = [DateTimeOffset]::UtcNow.ToString('o')
    releaseUrl = ('https://github.com/muxiaoqi007/powertools/releases/tag/v' + $Version)
    assets = $updateAssets
}
$channel | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $releaseRoot "PowerTools-Update-win-x64.json") -Encoding utf8

Get-ChildItem -LiteralPath $releaseRoot -File | ForEach-Object {
    [pscustomobject]@{ Name = $_.Name; Size = $_.Length; SHA256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
} | Format-Table -AutoSize
Write-Host "Release assets: $releaseRoot"
