# Builds the Android app and installs it on the connected device over USB.
$ErrorActionPreference = "Stop"
$env:JAVA_HOME = "C:\Program Files\Android\Android Studio\jbr"
$root = Join-Path $PSScriptRoot "..\android"
& (Join-Path $root "gradlew.bat") -p $root installDebug
$adb = Join-Path $env:LOCALAPPDATA "Android\Sdk\platform-tools\adb.exe"
& $adb shell am start -n local.phonewebcam/.ui.MainActivity
