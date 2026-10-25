# Starts the fake phone (windows/Tools/TestSender) for testing the Windows receiver without a phone.
#   .\scripts\run-test-sender.ps1 [-Ffmpeg] [-Lan] [-Port 47809]
param([switch]$Ffmpeg, [switch]$Lan, [int]$Port = 47809)
$project = Join-Path $PSScriptRoot "..\windows\Tools\TestSender\TestSender.csproj"
$senderArgs = @("--port", $Port)
if ($Ffmpeg) { $senderArgs += "--ffmpeg" }
if ($Lan) { $senderArgs += "--lan" }
dotnet run --project $project -- @senderArgs
