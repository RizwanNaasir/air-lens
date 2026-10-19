<#
.SYNOPSIS
  Builds the AirLens setup: windows\installer\out\AirLens-<version>\ (run Setup.cmd in it) and the
  same folder as a zip. Contains the self-contained WinUI app, the virtual-camera DLL and the
  install/uninstall scripts.

  Needs the .NET 8 SDK, Visual Studio 2022 Build Tools with Desktop C++ (for the camera DLL; pass
  -SkipNative to reuse an existing build), and an FFmpeg 8.x LGPL shared build (DLLs and LICENSE.txt) in src\AirLens.App\ffmpeg.

  The zip is the release file the app's updater installs: upload it to a GitHub release tagged
  v<version> under the name it has here, AirLens-<version>-windows-x64.zip.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File windows\installer\build.ps1
  powershell -ExecutionPolicy Bypass -File windows\installer\build.ps1 -SkipNative -Version 0.1.0.1   # a test build
#>
param([switch]$SkipNative, [switch]$NoZip, [string]$Version)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$appProject = Join-Path $root "src\AirLens.App\AirLens.App.csproj"
$vcamProject = Join-Path $root "src\AirLens.VirtualCamera\AirLens.VirtualCamera.vcxproj"
$dll = Join-Path $root "src\AirLens.VirtualCamera\bin\Release\AirLens.VirtualCamera.dll"

if (-not (Get-ChildItem (Join-Path $root "src\AirLens.App\ffmpeg") -Filter "avcodec-*.dll" -ErrorAction SilentlyContinue)) {
    throw "FFmpeg DLLs missing: put an FFmpeg 8.x LGPL shared build's DLLs and LICENSE.txt in src\AirLens.App\ffmpeg"
}

if (-not $SkipNative) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    $msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
    if (-not $msbuild) { throw "MSBuild not found: install Visual Studio 2022 Build Tools with Desktop C++, or pass -SkipNative" }
    Write-Host "Building the virtual camera DLL..."
    & $msbuild $vcamProject /p:Configuration=Release /p:Platform=x64 /m /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) { throw "virtual camera build failed" }
}
if (-not (Test-Path $dll)) { throw "virtual camera DLL not found: $dll" }

$version = if ($Version) { $Version } else { (Select-Xml -Path (Join-Path $root "Directory.Build.props") -XPath "//Version").Node.InnerText }
$out = Join-Path $PSScriptRoot "out"
$stage = Join-Path $out "AirLens-$version"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

Write-Host "Publishing the app..."
& dotnet publish $appProject -c Release -r win-x64 --self-contained true -o (Join-Path $stage "app") -nologo -v:minimal "-p:Version=$version"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
Get-ChildItem (Join-Path $stage "app") -Recurse -Filter *.pdb | Remove-Item -Force
if (-not (Test-Path (Join-Path $stage "app\ffmpeg\avcodec-*.dll"))) { throw "publish output has no FFmpeg DLLs" }
# A distributed AirLens must carry an LGPL FFmpeg and its licence (THIRD-PARTY-NOTICES.txt).
if (-not (Test-Path (Join-Path $stage "app\ffmpeg\LICENSE.txt")) -or -not (Test-Path (Join-Path $stage "app\THIRD-PARTY-NOTICES.txt"))) { throw "publish output lacks FFmpeg's LICENSE.txt or THIRD-PARTY-NOTICES.txt" }
$avcodec = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes((Get-Item (Join-Path $stage "app\ffmpeg\avcodec-*.dll")).FullName))
if ($avcodec.Contains("--enable-gpl")) { throw "the bundled FFmpeg is a GPL build; use an LGPL build (see AirLens.App.csproj)" }

New-Item -ItemType Directory (Join-Path $stage "vcam") | Out-Null
Copy-Item $dll, (Join-Path $root "src\AirLens.VirtualCamera\install.ps1") (Join-Path $stage "vcam")
Copy-Item (Join-Path $PSScriptRoot "install.ps1"), (Join-Path $PSScriptRoot "uninstall.ps1"), (Join-Path $PSScriptRoot "Setup.cmd") $stage

$mb = [math]::Round((Get-ChildItem $stage -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)
Write-Host "Setup folder: $stage ($mb MB)"
if (-not $NoZip) {
    $zip = Join-Path $out "AirLens-$version-windows-x64.zip"
    Compress-Archive -Path $stage -DestinationPath $zip -Force
    Write-Host "Zip: $zip ($([math]::Round((Get-Item $zip).Length / 1MB)) MB)"
}
