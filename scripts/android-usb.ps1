# USB mode for the Android app: forwards PC 127.0.0.1:47809 to the tablet's port 47809 through adb.
# Then connect the Windows app to 127.0.0.1. Re-run after replugging the cable.
$adb = Join-Path $env:LOCALAPPDATA "Android\Sdk\platform-tools\adb.exe"
if (-not (Test-Path $adb)) { $adb = "adb" }
$devices = & $adb devices | Select-String "\tdevice$"
if (-not $devices) {
    Write-Host "No Android device found. Plug it in, enable USB debugging, and tap Allow on the device." -ForegroundColor Yellow
    exit 1
}
& $adb forward tcp:47809 tcp:47809
Write-Host "USB link ready. In the Windows app, connect to 127.0.0.1 port 47809." -ForegroundColor Green
