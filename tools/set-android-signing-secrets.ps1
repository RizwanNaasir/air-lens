<#
.SYNOPSIS
  Gives the Release workflow the Android signing key, as four repository secrets. Run it once, on
  the PC that holds the key. It must be the key that signed the APKs people already have (so far
  this PC's debug keystore), or their phones refuse to update to the new APK.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\set-android-signing-secrets.ps1
#>
param(
    [string]$Repo = "RizwanNaasir/air-lens",
    # The GitHub account that owns the repository; this PC has several signed in to gh.
    [string]$Account = "RizwanNaasir",
    [string]$Keystore = (Join-Path $env:USERPROFILE ".android\debug.keystore"),
    [string]$Alias = "androiddebugkey",
    # Android's published default for debug keystores; pass your own for a real release key.
    [string]$Password = "android"
)
$ErrorActionPreference = "Stop"

if (-not (Test-Path $Keystore)) { throw "No keystore at $Keystore" }
$token = gh auth token --user $Account 2>$null
if (-not $token) { throw "gh has no login for $Account. Run: gh auth login" }

$previous = $env:GH_TOKEN
$env:GH_TOKEN = $token # only for this script: gh's active account stays as it is
try {
    # Short values as --body: piping text to a program in Windows PowerShell adds a line break,
    # which would end up in the password.
    function Set-Secret([string]$Name, [string]$Value) {
        gh secret set $Name --repo $Repo --body $Value
        if ($LASTEXITCODE -ne 0) { throw "could not set $Name on $Repo" }
        Write-Host "set $Name"
    }
    # The keystore through stdin, so it never appears on a command line; base64 --decode in the
    # workflow ignores the trailing line break.
    [Convert]::ToBase64String([IO.File]::ReadAllBytes($Keystore)) | gh secret set ANDROID_KEYSTORE_BASE64 --repo $Repo
    if ($LASTEXITCODE -ne 0) { throw "could not set ANDROID_KEYSTORE_BASE64 on $Repo" }
    Write-Host "set ANDROID_KEYSTORE_BASE64"
    Set-Secret "ANDROID_KEYSTORE_PASSWORD" $Password
    Set-Secret "ANDROID_KEY_ALIAS" $Alias
    Set-Secret "ANDROID_KEY_PASSWORD" $Password
    Write-Host ""
    Write-Host "Secrets on ${Repo}:"
    gh secret list --repo $Repo
}
finally {
    $env:GH_TOKEN = $previous
}
