[CmdletBinding()]
param(
    [switch]$SkipBackend,
    [switch]$SkipAndroid,
    [switch]$SkipWindows,
    [switch]$IncludeMySql,
    [string]$Python = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$backendRoot = Join-Path $repoRoot "services/backend"

if (-not $Python) {
    $venvPython = Join-Path $backendRoot ".venv/Scripts/python.exe"
    $Python = if (Test-Path -LiteralPath $venvPython) { $venvPython } else { "python" }
}

& (Join-Path $PSScriptRoot "validate-contracts.ps1") -Python $Python

if (-not $SkipBackend) {
    Push-Location $backendRoot
    try {
        & $Python -m pytest -m "not mysql"
        if ($LASTEXITCODE -ne 0) { throw "Backend pytest failed" }
        if ($IncludeMySql) {
            if (-not $env:TEST_DATABASE_URL) {
                throw "IncludeMySql requires TEST_DATABASE_URL pointing to a disposable *_test database"
            }
            & $Python -m pytest -m mysql
            if ($LASTEXITCODE -ne 0) { throw "Backend MySQL integration tests failed" }
        }
    }
    finally {
        Pop-Location
    }
}

if (-not $SkipAndroid) {
    Push-Location (Join-Path $repoRoot "apps/android")
    try {
        & .\gradlew.bat test --stacktrace
        if ($LASTEXITCODE -ne 0) { throw "Android unit tests failed" }
        & .\gradlew.bat lint --max-workers=1 --stacktrace
        if ($LASTEXITCODE -ne 0) { throw "Android lint failed" }
    }
    finally {
        Pop-Location
    }
}

if (-not $SkipWindows) {
    $solution = Join-Path $repoRoot "apps/windows/PersonalFitnessPlanner.sln"
    & dotnet restore $solution
    if ($LASTEXITCODE -ne 0) { throw "Windows restore failed" }
    & dotnet test $solution --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Windows tests failed" }
}

Write-Host "All selected test gates passed."
