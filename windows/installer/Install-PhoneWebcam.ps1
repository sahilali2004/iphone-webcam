# Phone Webcam installer (self-elevating). Run Install.cmd, or:
#   powershell -ExecutionPolicy Bypass -File Install-PhoneWebcam.ps1 [-Uninstall] [-RemoveUserData] [-Quiet]
# Installs to C:\Program Files\PhoneWebcam: the app, the virtual camera (registered for the Windows Frame
# Server), and a Start menu shortcut. Nothing is downloaded unless you agree to fetch Android platform-tools.
param([switch]$Uninstall, [switch]$RemoveUserData, [switch]$Quiet)
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$dest = Join-Path $env:ProgramFiles "PhoneWebcam"
$dll = Join-Path $dest "PhoneWebcamSource.dll"
$shortcut = Join-Path ([Environment]::GetFolderPath("CommonPrograms")) "Phone Webcam.lnk"

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) {
    $argList = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"")
    if ($Uninstall) { $argList += "-Uninstall" }
    if ($RemoveUserData) { $argList += "-RemoveUserData" }
    if ($Quiet) { $argList += "-Quiet" }
    $p = Start-Process powershell -Verb RunAs -ArgumentList $argList -Wait -PassThru
    exit $p.ExitCode
}

function Ask($question) {
    if ($Quiet) { return $false }
    return (Read-Host "$question [Y/n]") -notmatch '^[nN]'
}

Get-Process PhoneWebcam -ErrorAction SilentlyContinue | Stop-Process -Force
Stop-Service FrameServer, FrameServerMonitor -Force -ErrorAction SilentlyContinue

if ($Uninstall) {
    if (Test-Path $dll) { Start-Process regsvr32.exe -ArgumentList "/s", "/u", "`"$dll`"" -Wait | Out-Null }
    Remove-Item $shortcut -ErrorAction SilentlyContinue
    Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue
    if ($RemoveUserData) {
        Get-ChildItem "C:\Users\*\AppData\Local\PhoneWebcam" -Directory -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
    }
    Write-Host "Phone Webcam was uninstalled." -ForegroundColor Green
    exit 0
}

$build = [Environment]::OSVersion.Version.Build
if ($build -lt 22000) {
    Write-Host "Phone Webcam needs Windows 11 (build 22000 or later). This PC is build $build." -ForegroundColor Red
    exit 1
}

Write-Host "Installing Phone Webcam to $dest"
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item (Join-Path $here "app\*") $dest -Recurse -Force
Copy-Item (Join-Path $here "PhoneWebcamSource.dll") $dll -Force
# regsvr32 is a GUI-subsystem program: PowerShell doesn't wait for it or set $LASTEXITCODE, so use Start-Process -Wait.
$reg = Start-Process regsvr32.exe -ArgumentList "/s", "`"$dll`"" -Wait -PassThru
if ($reg.ExitCode -ne 0) { Write-Host "Registering the virtual camera failed ($($reg.ExitCode))." -ForegroundColor Red; exit 1 }

$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = Join-Path $dest "PhoneWebcam.exe"
$link.WorkingDirectory = $dest
$link.Description = "Use your phone as a webcam"
$link.Save()

# USB for Android needs adb (Android SDK platform-tools). It can't be bundled (Google's licence), so reuse or fetch it.
$candidates = @((Join-Path $dest "platform-tools\adb.exe"), (Join-Path $env:LOCALAPPDATA "Android\Sdk\platform-tools\adb.exe"))
$onPath = Get-Command adb -ErrorAction SilentlyContinue
if ($onPath) { $candidates += $onPath.Source }
$adb = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $adb -and (Ask "USB connections to Android need Google's Android platform-tools (about 7 MB). Download from dl.google.com now?")) {
    $zip = Join-Path $env:TEMP "platform-tools.zip"
    Invoke-WebRequest "https://dl.google.com/android/repository/platform-tools-latest-windows.zip" -OutFile $zip -UseBasicParsing
    Expand-Archive $zip -DestinationPath $dest -Force
    Remove-Item $zip
    $adb = Join-Path $dest "platform-tools\adb.exe"
}

# Offer to install the phone app on a connected Android device.
$apk = Join-Path $here "PhoneWebcam-android.apk"
if ($adb -and (Test-Path $apk)) {
    $devices = & $adb devices | Select-String "\tdevice$"
    if ($devices -and (Ask "An Android device is connected. Install the Phone Webcam app on it?")) { & $adb install -r $apk }
}

Write-Host ""
Write-Host "Phone Webcam is installed. Start it from the Start menu: Phone Webcam." -ForegroundColor Green
Write-Host "Other apps list the camera as `"Phone Webcam (Windows Virtual Camera)`" while Phone Webcam is running."
