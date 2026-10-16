<#
.SYNOPSIS
  Installs (or with -Uninstall removes) the AirLens virtual camera media source. Needs an elevated
  PowerShell, once: Frame Server loads the source as Local Service, so the COM class must be in HKLM
  and the DLL somewhere Local Service can read (not under a user profile).

.EXAMPLE
  Start-Process powershell -Verb RunAs -ArgumentList '-ExecutionPolicy Bypass -File install.ps1'
#>
param(
    [string]$Dll = (Join-Path $PSScriptRoot "bin\Release\AirLens.VirtualCamera.dll"),
    [switch]$Uninstall
)
$ErrorActionPreference = "Stop"
$target = Join-Path $env:ProgramFiles "AirLens\VirtualCamera"
$installed = Join-Path $target "AirLens.VirtualCamera.dll"
$log = Join-Path $env:ProgramData "AirLens\install.log"
New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null
function Log($m) { "$(Get-Date -Format s) $m" | Tee-Object -FilePath $log -Append }

if ($Uninstall) {
    if (Test-Path $installed) { & regsvr32.exe /s /u $installed; Log "unregistered (exit $LASTEXITCODE)" }
    # Frame Server keeps a loaded DLL open; restarting it releases the file so it can be deleted.
    foreach ($svc in "FrameServerMonitor", "FrameServer") {
        if ((Get-Service $svc -ErrorAction SilentlyContinue).Status -eq "Running") { Restart-Service $svc -Force -ErrorAction SilentlyContinue }
    }
    Remove-Item -Recurse -Force $target -ErrorAction SilentlyContinue
    Log "removed $target"
    exit 0
}

if (-not (Test-Path $Dll)) { throw "build the DLL first: $Dll" }
New-Item -ItemType Directory -Force $target | Out-Null
# A loaded copy (Frame Server keeps it while a camera is open) cannot be overwritten; rename it away.
if (Test-Path $installed) {
    try { Copy-Item $Dll $installed -Force }
    catch { Rename-Item $installed "$installed.old-$(Get-Date -Format yyyyMMddHHmmss)"; Copy-Item $Dll $installed -Force }
} else {
    Copy-Item $Dll $installed -Force
}
$code = (Start-Process regsvr32.exe -ArgumentList "/s `"$installed`"" -Wait -PassThru).ExitCode
Log "installed $installed, regsvr32 exit $code"
# Frame Server keeps a loaded source DLL in memory, so an update only takes effect after a restart.
# This briefly interrupts any app that is using a camera right now.
foreach ($svc in "FrameServerMonitor", "FrameServer") {
    if ((Get-Service $svc -ErrorAction SilentlyContinue).Status -eq "Running") {
        Restart-Service $svc -Force -ErrorAction SilentlyContinue
        Log "restarted $svc"
    }
}
# Old copies renamed away during earlier updates can be deleted once nothing has them loaded.
Get-ChildItem $target -Filter "*.old-*" | Remove-Item -Force -ErrorAction SilentlyContinue
# Frame Server log folder: let the service write its trace file.
& icacls (Split-Path $log) /grant "*S-1-5-19:(OI)(CI)M" | Out-Null
exit $code
