@echo off
rem Installs AirLens. Windows asks once for administrator rights (needed for the virtual camera).
rem Options: -DesktopShortcut  also put a shortcut on the desktop.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
