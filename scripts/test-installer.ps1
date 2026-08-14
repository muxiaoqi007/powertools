param(
    [string]$InstallerPath = (Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts\installer\PowerTools-Setup-0.10.0-win-x64.exe")
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
$isAdministrator = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdministrator) { throw "Installer lifecycle testing requires an elevated PowerShell session." }

$InstallerPath = [IO.Path]::GetFullPath($InstallerPath)
if (-not (Test-Path -LiteralPath $InstallerPath)) { throw "Installer not found: $InstallerPath" }

$installRoot = Join-Path $env:ProgramFiles "PowerTools"
$desktopExecutable = Join-Path $installRoot "PowerTools.Desktop.exe"
$serverExecutable = Join-Path $installRoot "server\PowerTools.exe"
$uninstaller = Join-Path $installRoot "unins000.exe"
$externalToolsRoot = Join-Path ${env:CommonProgramFiles(x86)} "Microsoft Shared\Power BI Desktop\External Tools"
$manifestPath = Join-Path $externalToolsRoot "PowerTools.pbitool.json"
if (Test-Path -LiteralPath $uninstaller) {
    throw "An installed PowerTools instance already exists at $installRoot. Run lifecycle testing on a clean machine to avoid removing a real installation."
}
$hadManifest = Test-Path -LiteralPath $manifestPath
$previousManifest = if ($hadManifest) { [IO.File]::ReadAllBytes($manifestPath) } else { $null }
$serverProcess = $null
$uninstalled = $false

try {
    $install = Start-Process -FilePath $InstallerPath -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-" -WindowStyle Hidden -Wait -PassThru
    if ($install.ExitCode -ne 0) { throw "Installer exited with code $($install.ExitCode)." }
    if (-not (Test-Path -LiteralPath $desktopExecutable)) { throw "Installed desktop executable was not found." }
    if (-not (Test-Path -LiteralPath $serverExecutable)) { throw "Installed server executable was not found." }
    if (-not (Test-Path -LiteralPath $manifestPath)) { throw "Power BI external tool manifest was not created." }

    $manifest = Get-Content -Raw -LiteralPath $manifestPath -Encoding UTF8 | ConvertFrom-Json
    if (-not ([IO.Path]::GetFullPath($manifest.path)).Equals([IO.Path]::GetFullPath($desktopExecutable), [StringComparison]::OrdinalIgnoreCase)) {
        throw "External tool manifest points to an unexpected executable: $($manifest.path)"
    }
    if ($manifest.arguments -ne '--server "%server%" --database "%database%"') { throw "External tool arguments are invalid." }
    $iconPrefix = "image/png;base64,"
    if (-not $manifest.iconData.StartsWith($iconPrefix, [StringComparison]::Ordinal)) { throw "External tool icon data is missing or invalid." }
    $iconBytes = [Convert]::FromBase64String($manifest.iconData.Substring($iconPrefix.Length))
    $iconStream = [IO.MemoryStream]::new($iconBytes)
    $iconImage = [Drawing.Image]::FromStream($iconStream)
    try {
        if ($iconImage.Width -ne 64 -or $iconImage.Height -ne 64) { throw "External tool icon is not 64x64 pixels." }
    }
    finally {
        $iconImage.Dispose()
        $iconStream.Dispose()
    }

    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start(); $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port; $listener.Stop()
    $serverProcess = Start-Process -FilePath $serverExecutable -ArgumentList "--urls", "http://127.0.0.1:$port" -WorkingDirectory (Split-Path -Parent $serverExecutable) -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    $health = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        try { $health = Invoke-RestMethod "http://127.0.0.1:$port/health/live" -TimeoutSec 2; break } catch { Start-Sleep -Milliseconds 250 }
    }
    if ($null -eq $health -or $health.status -ne "live") { throw "Installed server did not become healthy." }
    if ($null -ne $serverProcess -and -not $serverProcess.HasExited) { Stop-Process -Id $serverProcess.Id -Force; $serverProcess.WaitForExit() }
    $serverProcess = $null

    if (-not (Test-Path -LiteralPath $uninstaller)) { throw "Uninstaller was not created." }
    $uninstall = Start-Process -FilePath $uninstaller -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART" -WindowStyle Hidden -Wait -PassThru
    if ($uninstall.ExitCode -ne 0) { throw "Uninstaller exited with code $($uninstall.ExitCode)." }
    $uninstalled = $true
    if (Test-Path -LiteralPath $desktopExecutable) { throw "Desktop executable remained after uninstall." }
    if (Test-Path -LiteralPath $manifestPath) { throw "External tool manifest remained after uninstall." }

    Write-Host "Installer lifecycle test passed. Version: $($health.version)"
}
finally {
    if ($null -ne $serverProcess -and -not $serverProcess.HasExited) { Stop-Process -Id $serverProcess.Id -Force -ErrorAction SilentlyContinue }
    if (-not $uninstalled -and (Test-Path -LiteralPath $uninstaller)) {
        Start-Process -FilePath $uninstaller -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART" -WindowStyle Hidden -Wait | Out-Null
    }
    if ($hadManifest) {
        New-Item -ItemType Directory -Path $externalToolsRoot -Force | Out-Null
        [IO.File]::WriteAllBytes($manifestPath, $previousManifest)
    }
    elseif (Test-Path -LiteralPath $manifestPath) {
        Remove-Item -LiteralPath $manifestPath -Force
    }
}
