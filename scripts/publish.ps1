# Builds everything into dist\PhoneWebcam-<version>\ and a zip: self-contained Windows app, virtual camera DLL,
# Android APK, installer. Run from any directory.
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$version = "0.1.0"
$out = Join-Path $root "dist\PhoneWebcam-$version"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null

Write-Host "Windows app..."
dotnet publish (Join-Path $root "windows\App\App.csproj") -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o (Join-Path $out "app") | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "Virtual camera..."
& (Join-Path $PSScriptRoot "build-vcam.ps1") | Out-Null
Copy-Item (Join-Path $root "windows\VirtualCamera\build\Release\PhoneWebcamSource.dll") $out

Write-Host "Android app..."
$env:JAVA_HOME = "C:\Program Files\Android\Android Studio\jbr"
& (Join-Path $root "android\gradlew.bat") -p (Join-Path $root "android") assembleDebug -q
if ($LASTEXITCODE -ne 0) { throw "Android build failed" }
Copy-Item (Join-Path $root "android\app\build\outputs\apk\debug\app-debug.apk") (Join-Path $out "PhoneWebcam-android.apk")

Copy-Item (Join-Path $root "windows\installer\*") $out -Exclude "README.md"
$zip = Join-Path $root "dist\PhoneWebcam-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip
Write-Host "Done: $zip"
