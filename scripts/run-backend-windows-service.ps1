[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$installPath = [System.IO.Path]::GetFullPath($InstallRoot)
$configPath = Join-Path $installPath 'config\service-config.json'
if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
    throw "Backend service configuration is missing: $configPath"
}

$config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
$appPath = Join-Path $installPath 'app'
$pythonPath = Join-Path $installPath 'venv\Scripts\python.exe'
$logDirectory = Join-Path $installPath 'logs'
$stdoutPath = Join-Path $logDirectory 'backend.stdout.log'
$stderrPath = Join-Path $logDirectory 'backend.stderr.log'

if (-not (Test-Path -LiteralPath $pythonPath -PathType Leaf)) {
    throw "Backend virtual environment is missing: $pythonPath"
}
if (-not (Test-Path -LiteralPath (Join-Path $appPath 'app\main.py') -PathType Leaf)) {
    throw "Backend application is missing: $appPath"
}

New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null

$env:ENVIRONMENT = [string]$config.environment
$env:DATABASE_BACKEND = [string]$config.database_backend
$env:DATABASE_URL = ''
$env:SQLITE_DATABASE_PATH = [string]$config.sqlite_database_path
$env:JWT_SECRET = ''
$env:RUNTIME_CONFIG_PATH = [string]$config.runtime_config_path
$env:CORS_ORIGINS = ConvertTo-Json -InputObject @($config.cors_origins) -Compress
$env:FORWARDED_ALLOW_IPS = '127.0.0.1,::1'
$env:LOG_LEVEL = 'INFO'
$env:PYTHONDONTWRITEBYTECODE = '1'
$env:PYTHONUNBUFFERED = '1'

$arguments = @(
    '-m', 'uvicorn', 'app.main:app',
    '--host', '127.0.0.1',
    '--port', [string]$config.port,
    '--workers', '1',
    '--proxy-headers',
    '--forwarded-allow-ips', '127.0.0.1,::1'
)

Set-Location -LiteralPath $appPath
& $pythonPath -m alembic upgrade head 1>> $stdoutPath 2>> $stderrPath
if ($LASTEXITCODE -ne 0) {
    throw "Backend database migration failed with exit code $LASTEXITCODE"
}
& $pythonPath -m scripts.seed_default_plan 1>> $stdoutPath 2>> $stderrPath
if ($LASTEXITCODE -ne 0) {
    throw "Backend default data initialization failed with exit code $LASTEXITCODE"
}
& $pythonPath @arguments 1>> $stdoutPath 2>> $stderrPath
exit $LASTEXITCODE
