# Installs the Phone Webcam virtual-camera DLL for the Windows Frame Server (needs admin; self-elevates).
#   .\scripts\install-vcam.ps1            install / update
#   .\scripts\install-vcam.ps1 -Uninstall
# The DLL must live outside the user profile (the Frame Server runs as LOCAL SERVICE) and be registered in HKLM.
param([switch]$Uninstall, [string]$Source)
$ErrorActionPreference = "Stop"
$dest = Join-Path $env:ProgramFiles "PhoneWebcam"
$dll = Join-Path $dest "PhoneWebcamSource.dll"
if (-not $Source) { $Source = Resolve-Path (Join-Path $PSScriptRoot "..\windows\VirtualCamera\build\Release\PhoneWebcamSource.dll") }

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) {
    $args = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"", "-Source", "`"$Source`"")
    if ($Uninstall) { $args += "-Uninstall" }
    $p = Start-Process powershell -Verb RunAs -ArgumentList $args -Wait -PassThru
    exit $p.ExitCode
}

# Release any running instance so the file can be replaced (the service restarts on demand).
Stop-Service FrameServer -Force -ErrorAction SilentlyContinue
Stop-Service FrameServerMonitor -Force -ErrorAction SilentlyContinue

if ($Uninstall) {
    if (Test-Path $dll) { & regsvr32.exe /s /u $dll; Remove-Item $dest -Recurse -Force }
    Write-Host "Phone Webcam virtual camera uninstalled."
    exit 0
}

New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item $Source $dll -Force
& regsvr32.exe /s $dll
if ($LASTEXITCODE -ne 0) { Write-Error "regsvr32 failed ($LASTEXITCODE)"; exit 1 }
Write-Host "Phone Webcam virtual camera installed: $dll"
