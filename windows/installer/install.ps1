<#
.SYNOPSIS
  Installs AirLens for all users of this PC:
    - the app in C:\Program Files\AirLens\App (self-contained: no .NET or Windows App SDK install needed)
    - the virtual camera, registered machine-wide so Frame Server can load it
    - a Start menu shortcut, and an entry in Settings > Apps that uninstalls it
  Asks for administrator rights once (UAC). Installing again over an older version updates it.

.EXAMPLE
  Double-click Setup.cmd, or run:
  powershell -ExecutionPolicy Bypass -File install.ps1 [-DesktopShortcut] [-Quiet]
#>
param(
    [switch]$DesktopShortcut,
    # No pause on errors and no app launch at the end (scripted installs).
    [switch]$Quiet
)
$ErrorActionPreference = "Stop"

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $argList = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"")
    if ($DesktopShortcut) { $argList += "-DesktopShortcut" }
    if ($Quiet) { $argList += "-Quiet" }
    try {
        $p = Start-Process powershell.exe -Verb RunAs -ArgumentList $argList -Wait -PassThru
        exit $p.ExitCode
    } catch {
        Write-Host "AirLens was not installed: administrator rights are needed to register the virtual camera." -ForegroundColor Yellow
        exit 1
    }
}

$src = $PSScriptRoot
$base = Join-Path $env:ProgramFiles "AirLens"
$appDir = Join-Path $base "App"
$exe = Join-Path $appDir "AirLens.App.exe"
$uninstallKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AirLens"
$log = Join-Path $env:ProgramData "AirLens\install.log"
New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null
# The log is UTF-16, which is what the camera script's Tee-Object writes in Windows PowerShell. An
# odd byte count means an earlier write in another encoding misaligned it, so start a fresh one.
if ((Test-Path $log) -and ((Get-Item $log).Length % 2)) { Move-Item $log "$log.old" -Force }
function Log($m) { "$(Get-Date -Format s) $m" | Out-File -FilePath $log -Append -Encoding Unicode }

function New-Shortcut([string]$path) {
    $s = (New-Object -ComObject WScript.Shell).CreateShortcut($path)
    $s.TargetPath = $exe
    $s.WorkingDirectory = $appDir
    $s.IconLocation = "$exe,0"
    $s.Description = "Use your phone as a webcam"
    $s.Save()
}

try {
    Write-Host "Installing AirLens..."
    # A running copy holds its files open; ask it to quit, then end it. A copy in the notification
    # area has no window to close: it quits on this event (App/Tray/SingleInstance.cs). Copies from
    # before the icon close with their window.
    $quit = $null
    if ([System.Threading.EventWaitHandle]::TryOpenExisting("Local\net.silow.airlens.quit", [ref]$quit)) { [void]$quit.Set(); $quit.Dispose() }
    foreach ($p in Get-Process AirLens.App -ErrorAction SilentlyContinue) {
        $p.CloseMainWindow() | Out-Null
        if (-not $p.WaitForExit(5000)) { $p.Kill() }
    }

    # App files. /MIR makes the folder an exact copy, so files of an older version do not linger.
    & robocopy.exe (Join-Path $src "app") $appDir /MIR /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "copying the app failed (robocopy exit $LASTEXITCODE)" }

    # Virtual camera: copies the DLL to VirtualCamera\, registers it in HKLM, restarts Frame Server.
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $src "vcam\install.ps1") `
        -Dll (Join-Path $src "vcam\AirLens.VirtualCamera.dll") | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "registering the virtual camera failed (exit $LASTEXITCODE)" }

    Copy-Item (Join-Path $src "uninstall.ps1") $base -Force

    New-Shortcut (Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\AirLens.lnk")
    if ($DesktopShortcut) { New-Shortcut (Join-Path $env:PUBLIC "Desktop\AirLens.lnk") }

    # Settings > Apps entry.
    $version = (Get-Item $exe).VersionInfo.ProductVersion.Split('+')[0]
    $sizeKb = [int]((Get-ChildItem $base -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)
    $powershell = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
    New-Item $uninstallKey -Force | Out-Null
    $strings = @{
        DisplayName     = "AirLens"
        DisplayVersion  = $version
        Publisher       = "silow.net"
        DisplayIcon     = "$exe,0"
        InstallLocation = $base
        UninstallString = "`"$powershell`" -NoProfile -ExecutionPolicy Bypass -File `"$base\uninstall.ps1`""
    }
    foreach ($name in $strings.Keys) { New-ItemProperty $uninstallKey $name -Value $strings[$name] -PropertyType String -Force | Out-Null }
    foreach ($name in "NoModify", "NoRepair") { New-ItemProperty $uninstallKey $name -Value 1 -PropertyType DWord -Force | Out-Null }
    New-ItemProperty $uninstallKey EstimatedSize -Value $sizeKb -PropertyType DWord -Force | Out-Null

    Log "installed AirLens $version to $base ($sizeKb KB)"
    Write-Host "AirLens $version is installed. Open it from the Start menu." -ForegroundColor Green
    if (-not $Quiet) {
        # Explorer starts the app with the signed-in user's normal (not elevated) rights.
        Start-Process explorer.exe -ArgumentList "`"$exe`""
        Start-Sleep -Seconds 3
    }
    exit 0
} catch {
    Log "install failed: $_"
    Write-Host "Install failed: $_" -ForegroundColor Red
    Write-Host "Details: $log"
    if (-not $Quiet) { Read-Host "Press Enter to close" | Out-Null }
    exit 1
}
