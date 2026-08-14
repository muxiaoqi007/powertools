param(
    [Parameter(Mandatory = $true)][string]$FromVersion,
    [Parameter(Mandatory = $true)][string]$ToVersion,
    [Parameter(Mandatory = $true)][string]$BaselineDirectory,
    [string]$CurrentDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts\desktop-win-x64"),
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts\updates"),
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
if ($FromVersion -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$' -or $ToVersion -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$') { throw "FromVersion and ToVersion must be numeric versions." }
if ($Runtime -notmatch '^[A-Za-z0-9-]+$') { throw "Runtime contains invalid characters." }
$baselineRoot = [IO.Path]::GetFullPath($BaselineDirectory)
$currentRoot = [IO.Path]::GetFullPath($CurrentDirectory)
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path -LiteralPath $baselineRoot -PathType Container)) { throw "Baseline directory not found: $baselineRoot" }
if (-not (Test-Path -LiteralPath $currentRoot -PathType Container)) { throw "Current package directory not found: $currentRoot" }
if ($baselineRoot -eq $currentRoot) { throw "Baseline and current package directories must be different." }
if (-not (Test-Path -LiteralPath (Join-Path $baselineRoot "PowerTools.Desktop.exe"))) { throw "Baseline is not a PowerTools desktop package." }
if (-not (Test-Path -LiteralPath (Join-Path $currentRoot "PowerTools.Desktop.exe"))) { throw "Current directory is not a PowerTools desktop package." }

function Get-FileMap([string]$Root) {
    $map = @{}
    $rootUri = [Uri]($Root.TrimEnd('\') + '\')
    Get-ChildItem -LiteralPath $Root -Recurse -File | ForEach-Object {
        $relative = [Uri]::UnescapeDataString($rootUri.MakeRelativeUri([Uri]$_.FullName).ToString()).Replace('\', '/')
        $map[$relative] = [pscustomobject]@{ FullName = $_.FullName; Size = $_.Length; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    return $map
}

$baseline = Get-FileMap $baselineRoot
$current = Get-FileMap $currentRoot
$changed = @($current.Keys | Where-Object { -not $baseline.ContainsKey($_) -or $baseline[$_].Sha256 -ne $current[$_].Sha256 } | Sort-Object)
$removed = @($baseline.Keys | Where-Object { -not $current.ContainsKey($_) } | Sort-Object)
if ($changed.Count -eq 0 -and $removed.Count -eq 0) { throw "No file changes were found between $FromVersion and $ToVersion." }

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("powertools-delta-" + [Guid]::NewGuid().ToString("N"))
$payloadRoot = Join-Path $temporaryRoot "payload"
New-Item -ItemType Directory -Path $payloadRoot -Force | Out-Null
try {
    $manifestFiles = foreach ($relative in $changed) {
        $source = $current[$relative].FullName
        $destination = Join-Path $payloadRoot $relative.Replace('/', [IO.Path]::DirectorySeparatorChar)
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination -Force
        [ordered]@{ path = $relative; sha256 = $current[$relative].Sha256; size = $current[$relative].Size }
    }
    $manifest = [ordered]@{
        schemaVersion = 1
        fromVersion = $FromVersion
        toVersion = $ToVersion
        runtime = $Runtime
        createdAt = [DateTimeOffset]::UtcNow.ToString('o')
        files = @($manifestFiles)
        removedFiles = @($removed)
    }
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $temporaryRoot "update-package.json") -Encoding utf8
    New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
    $package = Join-Path $outputRoot "PowerTools-Delta-$FromVersion-to-$ToVersion-$Runtime.zip"
    if (Test-Path -LiteralPath $package) { Remove-Item -LiteralPath $package -Force }
    Compress-Archive -Path (Join-Path $temporaryRoot "*") -DestinationPath $package -CompressionLevel Optimal
    $hash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "Delta package: $package"
    Write-Host "Changed files: $($changed.Count); removed files: $($removed.Count)"
    Write-Host "SHA256: $hash"
    Get-Item -LiteralPath $package
}
finally {
    $safeTempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    $resolvedTemporary = [IO.Path]::GetFullPath($temporaryRoot)
    if ($resolvedTemporary.StartsWith($safeTempRoot, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedTemporary)) {
        Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force
    }
}
