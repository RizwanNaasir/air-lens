<#
.SYNOPSIS
  Removes AirLens: the app, the virtual camera, the shortcuts and the Settings > Apps entry.
  Settings and logs in %LOCALAPPDATA%\AirLens are kept. Asks for administrator rights (UAC).
  Installed as C:\Program Files\AirLens\uninstall.ps1; Settings > Apps > AirLens > Uninstall runs it.
#>
param([switch]$Quiet)
$ErrorActionPreference = "Stop"

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    # Run from a temporary copy: the original lives in the folder that is about to be deleted.
    $copy = Join-Path $env:TEMP "airlens-uninstall.ps1"
    Copy-Item $PSCommandPath $copy -Force
    $argList = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$copy`"")
    if ($Quiet) { $argList += "-Quiet" }
    try {
        $p = Start-Process powershell.exe -Verb RunAs -ArgumentList $argList -Wait -PassThru
        exit $p.ExitCode
    } catch {
        Write-Host "AirLens was not removed: administrator rights are needed." -ForegroundColor Yellow
        exit 1
    }
}

$base = Join-Path $env:ProgramFiles "AirLens"
$dll = Join-Path $base "VirtualCamera\AirLens.VirtualCamera.dll"
$log = Join-Path $env:ProgramData "AirLens\install.log"
function Log($m) { try { "$(Get-Date -Format s) $m" | Out-File -FilePath $log -Append -Encoding Unicode } catch { } }

Add-Type -Namespace AirLens -Name Native -MemberDefinition @'
[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
public static extern bool MoveFileEx(string existingFileName, string newFileName, int flags);
'@

# Deletes a folder tree. Whatever is held open (a file in use, or a folder that is some shell's
# current directory) is deleted at the next restart instead. Returns the paths left for then.
function Remove-Tree([string]$path) {
    if (-not (Test-Path $path)) { return @() }
    try { Remove-Item $path -Recurse -Force -ErrorAction Stop; return @() } catch { }
    $pending = @()
    # Deepest first, so each folder is empty by the time Windows deletes it at boot.
    $items = @(Get-ChildItem $path -Recurse -Force -ErrorAction SilentlyContinue | Sort-Object { $_.FullName.Length } -Descending) + @(Get-Item $path -Force)
    foreach ($item in $items) {
        try { Remove-Item $item.FullName -Recurse -Force -ErrorAction Stop }
        catch {
            $pending += $item.FullName
            try { [AirLens.Native]::MoveFileEx($item.FullName, $null, 4) | Out-Null } catch { }  # MOVEFILE_DELAY_UNTIL_REBOOT
        }
    }
    return $pending
}

try {
    Write-Host "Removing AirLens..."
    foreach ($p in Get-Process AirLens.App -ErrorAction SilentlyContinue) {
        $p.CloseMainWindow() | Out-Null
        if (-not $p.WaitForExit(5000)) { $p.Kill() }
    }
    if (Test-Path $dll) {
        $code = (Start-Process regsvr32.exe -ArgumentList "/s /u `"$dll`"" -Wait -PassThru).ExitCode
        Log "unregistered virtual camera (regsvr32 exit $code)"
    }
    # Frame Server keeps a loaded camera DLL open; restarting it releases the file.
    foreach ($svc in "FrameServerMonitor", "FrameServer") {
        if ((Get-Service $svc -ErrorAction SilentlyContinue).Status -eq "Running") { Restart-Service $svc -Force -ErrorAction SilentlyContinue }
    }
    Remove-Item (Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\AirLens.lnk") -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $env:PUBLIC "Desktop\AirLens.lnk") -Force -ErrorAction SilentlyContinue
    $pending = @(Remove-Tree $base)
    Remove-Item "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AirLens" -Recurse -Force -ErrorAction SilentlyContinue
    if ($PSCommandPath -like "$env:TEMP*") { Remove-Item $PSCommandPath -Force -ErrorAction SilentlyContinue }
    if ($pending.Count -gt 0) {
        Log "uninstalled AirLens; in use, removed at next restart: $($pending -join '; ')"
        Write-Host "AirLens is uninstalled. Something still has these open, so Windows removes them at the next restart:" -ForegroundColor Green
        $pending | ForEach-Object { Write-Host "  $_" }
    } else {
        Log "uninstalled AirLens from $base"
        Write-Host "AirLens is uninstalled." -ForegroundColor Green
    }
    if (-not $Quiet) { Start-Sleep -Seconds 3 }
    exit 0
} catch {
    Log "uninstall failed: $_"
    Write-Host "Uninstall failed: $_" -ForegroundColor Red
    if (-not $Quiet) { Read-Host "Press Enter to close" | Out-Null }
    exit 1
}
