[CmdletBinding()]
param(
    [switch]$NoStart,
    [switch]$SkipPythonEnvironment,
    [string]$Python = "python"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$envPath = Join-Path $repoRoot ".env"
$backend = Join-Path $repoRoot "services/backend"

function New-RandomBase64Secret {
    param([Parameter(Mandatory)][int]$ByteCount)

    $bytes = New-Object byte[] $ByteCount
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $generator.GetBytes($bytes)
    }
    finally {
        $generator.Dispose()
    }
    return [Convert]::ToBase64String($bytes)
}

if (-not (Test-Path -LiteralPath $envPath)) {
    $jwtSecret = New-RandomBase64Secret -ByteCount 48
    $content = Get-Content -Raw -LiteralPath (Join-Path $repoRoot ".env.example")
    $content = $content -replace "(?m)^JWT_SECRET=$", "JWT_SECRET=$jwtSecret"
    [IO.File]::WriteAllText($envPath, $content, [Text.UTF8Encoding]::new($false))
    Write-Host "Created root .env with random local-only secrets."
}

if (-not $SkipPythonEnvironment) {
    $venvPython = Join-Path $backend ".venv/Scripts/python.exe"
    if (-not (Test-Path -LiteralPath $venvPython)) {
        & $Python -m venv (Join-Path $backend ".venv")
        if ($LASTEXITCODE -ne 0) { throw "Failed to create backend virtual environment" }
    }
    & $venvPython -m pip install -e "$backend[dev]"
    if ($LASTEXITCODE -ne 0) { throw "Failed to install backend development dependencies" }
}

& (Join-Path $PSScriptRoot "sync-contracts.ps1") -Python (
    if (Test-Path -LiteralPath (Join-Path $backend ".venv/Scripts/python.exe")) {
        Join-Path $backend ".venv/Scripts/python.exe"
    } else {
        $Python
    }
)

if (-not $NoStart) {
    & docker compose --env-file (Join-Path $repoRoot ".env") -f (Join-Path $repoRoot "infra/docker-compose.yml") up -d --build backend
    if ($LASTEXITCODE -ne 0) { throw "Docker Compose startup failed" }
    & docker compose --env-file (Join-Path $repoRoot ".env") -f (Join-Path $repoRoot "infra/docker-compose.yml") ps backend
}
