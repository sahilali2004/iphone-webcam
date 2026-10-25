# Builds windows/VirtualCamera (PhoneWebcamSource.dll + vcamtest.exe) with the VS 2022 Build Tools.
param([ValidateSet("Release", "Debug")] [string]$Config = "Release")
$ErrorActionPreference = "Stop"
$vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -products * -latest -property installationPath
$src = Resolve-Path (Join-Path $PSScriptRoot "..\windows\VirtualCamera")
$out = Join-Path $src "build\$Config"
cmd /c "`"$vs\VC\Auxiliary\Build\vcvars64.bat`" >nul && cmake -S `"$src`" -B `"$out`" -G Ninja -DCMAKE_BUILD_TYPE=$Config >nul && cmake --build `"$out`""
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Host "Built: $out\PhoneWebcamSource.dll"
