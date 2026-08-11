[CmdletBinding()]
param(
    [string]$Python = "python",
    [switch]$SkipSnapshots
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$contract = Join-Path $repoRoot "contracts/default-training-plan.json"
$schema = Join-Path $repoRoot "contracts/default-training-plan.schema.json"

if (-not (Test-Json -LiteralPath $contract -SchemaFile $schema -ErrorAction Stop)) {
    throw "default-training-plan.json failed JSON Schema validation"
}

$arguments = @((Join-Path $PSScriptRoot "validate_contracts.py"))
if ($SkipSnapshots) { $arguments += "--skip-snapshots" }
& $Python @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Cross-contract invariant validation failed"
}
