[CmdletBinding()]
param(
    [string]$Python = "python",
    [switch]$SkipValidation
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$contracts = Join-Path $repoRoot "contracts"

if (-not $SkipValidation) {
    & (Join-Path $PSScriptRoot "validate-contracts.ps1") -Python $Python -SkipSnapshots
}

$plan = Join-Path $contracts "default-training-plan.json"
$planTargets = @(
    "apps/android/app/src/main/resources/default-training-plan.json",
    "apps/windows/src/PersonalFitnessPlanner.Infrastructure/Data/default-training-plan.json",
    "services/backend/contracts/default-training-plan.json"
)
foreach ($relativeTarget in $planTargets) {
    $target = Join-Path $repoRoot $relativeTarget
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    Copy-Item -Force -LiteralPath $plan -Destination $target
}

Copy-Item -Force -LiteralPath (Join-Path $contracts "schema-version.json") `
    -Destination (Join-Path $repoRoot "services/backend/contracts/schema-version.json")
Copy-Item -Force -LiteralPath (Join-Path $contracts "default-training-plan.schema.json") `
    -Destination (Join-Path $repoRoot "services/backend/contracts/default-training-plan.schema.json")

$exampleTargets = @(
    "apps/android/app/src/test/resources/contracts",
    "apps/windows/tests/PersonalFitnessPlanner.Tests/Contracts",
    "services/backend/contracts/examples"
)
foreach ($relativeDirectory in $exampleTargets) {
    $directory = Join-Path $repoRoot $relativeDirectory
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    Copy-Item -Force -LiteralPath (Join-Path $contracts "examples/recommendation-cases.json") -Destination $directory
    Copy-Item -Force -LiteralPath (Join-Path $contracts "examples/progression-cases.json") -Destination $directory
}

if (-not $SkipValidation) {
    & (Join-Path $PSScriptRoot "validate-contracts.ps1") -Python $Python
}

Write-Host "Contract snapshots synchronized."
