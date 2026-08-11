[CmdletBinding()]
param(
    [switch]$AllowMissingAndroid,
    [switch]$AllowMissingWindows,
    [switch]$AllowMissingBackend
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $repoRoot "artifacts"
$version = (Get-Content -Raw -LiteralPath (Join-Path $repoRoot "VERSION")).Trim()
$targets = @("android", "windows", "backend", "contracts", "checksums")
New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null

foreach ($name in $targets) {
    $path = [IO.Path]::GetFullPath((Join-Path $artifactRoot $name))
    $safeRoot = [IO.Path]::GetFullPath($artifactRoot) + [IO.Path]::DirectorySeparatorChar
    if (-not $path.StartsWith($safeRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean artifact path outside artifacts/: $path"
    }
    if (Test-Path -LiteralPath $path) {
        Remove-Item -Recurse -Force -LiteralPath $path
    }
    New-Item -ItemType Directory -Force -Path $path | Out-Null
}

$androidSource = Join-Path $repoRoot "apps/android/artifacts"
$androidApks = @(Get-ChildItem -File -LiteralPath $androidSource -Filter "*.apk" -ErrorAction SilentlyContinue)
if ($androidApks.Count -eq 0 -and -not $AllowMissingAndroid) {
    throw "No Android APK found. Run the Android build first."
}
foreach ($file in $androidApks) {
    Copy-Item -Force -LiteralPath $file.FullName -Destination (Join-Path $artifactRoot "android")
}

$windowsSource = Join-Path $repoRoot "apps/windows/artifacts"
$windowsExe = Join-Path $windowsSource "PersonalFitnessPlanner.exe"
if (-not (Test-Path -LiteralPath $windowsExe) -and -not $AllowMissingWindows) {
    throw "No Windows EXE found. Run the Windows publish first."
}
if (Test-Path -LiteralPath $windowsExe) {
    Copy-Item -Force -LiteralPath $windowsExe -Destination (Join-Path $artifactRoot "windows")
}
if (Test-Path -LiteralPath (Join-Path $windowsSource "publish-win-x64")) {
    Copy-Item -Recurse -Force -LiteralPath (Join-Path $windowsSource "publish-win-x64") `
        -Destination (Join-Path $artifactRoot "windows/publish-win-x64")
}

$dockerfile = Join-Path $repoRoot "services/backend/Dockerfile"
if (-not (Test-Path -LiteralPath $dockerfile) -and -not $AllowMissingBackend) {
    throw "Backend Dockerfile is missing"
}
if (Test-Path -LiteralPath $dockerfile) {
    Copy-Item -Force -LiteralPath $dockerfile -Destination (Join-Path $artifactRoot "backend")
    Copy-Item -Force -LiteralPath (Join-Path $repoRoot "infra/docker-compose.yml") `
        -Destination (Join-Path $artifactRoot "backend")
}
if (-not $AllowMissingBackend) {
    $backendImage = "personal-fitness-planner-backend:$version"
    & docker image inspect $backendImage *> $null
    if ($LASTEXITCODE -ne 0) {
        throw "Backend image $backendImage is missing. Run the backend build first."
    }
    $imageArchive = Join-Path $artifactRoot "backend/personal-fitness-planner-backend-$version.tar"
    & docker save --output $imageArchive $backendImage
    if ($LASTEXITCODE -ne 0) { throw "Failed to export backend image $backendImage" }
}

Copy-Item -Recurse -Force -Path (Join-Path $repoRoot "contracts/*") `
    -Destination (Join-Path $artifactRoot "contracts")

$checksumRoot = Join-Path $artifactRoot "checksums"
$manifest = Join-Path $checksumRoot "SHA256SUMS.txt"
$lines = Get-ChildItem -Recurse -File -LiteralPath $artifactRoot |
    Where-Object { $_.FullName -ne $manifest } |
    Sort-Object FullName |
    ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($artifactRoot, $_.FullName).Replace("\", "/")
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
        "$hash  $relative"
    }
[IO.File]::WriteAllLines($manifest, $lines, [Text.UTF8Encoding]::new($false))
Write-Host "Release package assembled at $artifactRoot"
