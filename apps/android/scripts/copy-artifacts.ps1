$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$Artifacts = Join-Path $ProjectRoot 'artifacts'
New-Item -ItemType Directory -Force -Path $Artifacts | Out-Null

$DebugApk = Join-Path $ProjectRoot 'app\build\outputs\apk\debug\app-debug.apk'
$ReleaseApk = Join-Path $ProjectRoot 'app\build\outputs\apk\release\app-release-unsigned.apk'

if (-not (Test-Path -LiteralPath $DebugApk)) { throw 'Debug APK is missing. Run assembleDebug first.' }
if (-not (Test-Path -LiteralPath $ReleaseApk)) { throw 'Unsigned release APK is missing. Run assembleRelease first.' }

Copy-Item -LiteralPath $DebugApk -Destination (Join-Path $Artifacts 'PersonalFitnessPlanner-debug.apk') -Force
Copy-Item -LiteralPath $ReleaseApk -Destination (Join-Path $Artifacts 'PersonalFitnessPlanner-release-unsigned.apk') -Force
Get-ChildItem -LiteralPath $Artifacts -Filter '*.apk' | Get-FileHash -Algorithm SHA256
