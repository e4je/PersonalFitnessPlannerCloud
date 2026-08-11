[CmdletBinding()]
param(
    [string]$Python = "",
    [switch]$GenerateClients
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$backendRoot = Join-Path $repoRoot "services/backend"
if (-not $Python) {
    $venvPython = Join-Path $backendRoot ".venv/Scripts/python.exe"
    $Python = if (Test-Path -LiteralPath $venvPython) { $venvPython } else { "python" }
}

Push-Location $backendRoot
try {
    & $Python -m scripts.export_openapi
    if ($LASTEXITCODE -ne 0) { throw "FastAPI OpenAPI export failed" }
}
finally {
    Pop-Location
}

Copy-Item -Force -LiteralPath (Join-Path $backendRoot "contracts/openapi.yaml") `
    -Destination (Join-Path $repoRoot "contracts/openapi.yaml")
& (Join-Path $PSScriptRoot "sync-contracts.ps1") -Python $Python

if ($GenerateClients) {
    $generator = Get-Command openapi-generator-cli -ErrorAction SilentlyContinue
    if (-not $generator) {
        throw "openapi-generator-cli is required when -GenerateClients is used"
    }
    $spec = Join-Path $repoRoot "contracts/openapi.yaml"
    & $generator.Source generate -i $spec -g kotlin -o (Join-Path $repoRoot "contracts/generated/android") `
        --additional-properties packageName=com.personalfitnessplanner.generated,useCoroutines=true
    if ($LASTEXITCODE -ne 0) { throw "Android client generation failed" }
    & $generator.Source generate -i $spec -g csharp -o (Join-Path $repoRoot "contracts/generated/windows") `
        --additional-properties packageName=PersonalFitnessPlanner.Generated,targetFramework=net8.0
    if ($LASTEXITCODE -ne 0) { throw "Windows client generation failed" }
}

Write-Host "OpenAPI snapshot synchronized."
