[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$SkipBackend,
    [switch]$SkipAndroid,
    [switch]$SkipWindows,
    [switch]$SkipSmokeTest,
    [string]$Python = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$version = (Get-Content -Raw -LiteralPath (Join-Path $repoRoot "VERSION")).Trim()

if (-not $SkipTests) {
    & (Join-Path $PSScriptRoot "test-all.ps1") `
        -SkipBackend:$SkipBackend -SkipAndroid:$SkipAndroid -SkipWindows:$SkipWindows -Python $Python
}

if (-not $SkipBackend) {
    & docker build `
        --tag "personal-fitness-planner-backend:$version" `
        --file (Join-Path $repoRoot "services/backend/Dockerfile") `
        (Join-Path $repoRoot "services/backend")
    if ($LASTEXITCODE -ne 0) { throw "Backend image build failed" }
}

if (-not $SkipAndroid) {
    Push-Location (Join-Path $repoRoot "apps/android")
    try {
        & .\gradlew.bat assembleDebug --stacktrace
        if ($LASTEXITCODE -ne 0) { throw "Android debug assembly failed" }
        & .\gradlew.bat assembleRelease --stacktrace
        if ($LASTEXITCODE -ne 0) { throw "Android release assembly failed" }
        & .\scripts\copy-artifacts.ps1
    }
    finally {
        Pop-Location
    }
}

if (-not $SkipWindows) {
    $solution = Join-Path $repoRoot "apps/windows/PersonalFitnessPlanner.sln"
    & dotnet restore $solution
    if ($LASTEXITCODE -ne 0) { throw "Windows restore failed" }
    & dotnet build $solution --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Windows release build failed" }
    & (Join-Path $repoRoot "apps/windows/scripts/publish.ps1") `
        -SkipTests -SkipSmokeTest:$SkipSmokeTest
}

& (Join-Path $PSScriptRoot "package-release.ps1") `
    -AllowMissingAndroid:$SkipAndroid -AllowMissingWindows:$SkipWindows -AllowMissingBackend:$SkipBackend

Write-Host "Unified build completed. See artifacts/."
