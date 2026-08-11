#requires -Version 5.1
[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$SkipSmokeTest
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\PersonalFitnessPlanner.App\PersonalFitnessPlanner.App.csproj"
$nugetConfig = Join-Path $repoRoot "NuGet.Config"
$packageCache = Join-Path $repoRoot ".packages"
$artifactRoot = Join-Path $repoRoot "artifacts"
$publishDirectory = Join-Path $artifactRoot "publish-win-x64"
$singleFileDirectory = Join-Path $artifactRoot "single-file-win-x64"
$publishedExe = Join-Path $singleFileDirectory "PersonalFitnessPlanner.exe"
$deliveryExe = Join-Path $artifactRoot "PersonalFitnessPlanner.exe"

if ([System.Environment]::OSVersion.Platform -ne [System.PlatformID]::Win32NT) {
    throw "win-x64 自包含桌面应用必须在 Windows 上发布和验收。"
}

New-Item -ItemType Directory -Force -Path $packageCache, $artifactRoot | Out-Null
$env:NUGET_PACKAGES = $packageCache

if (-not $SkipTests) {
    & (Join-Path $PSScriptRoot "test.ps1") -Configuration Release
}

dotnet restore $project `
    --runtime win-x64 `
    --configfile $nugetConfig `
    --packages $packageCache
if ($LASTEXITCODE -ne 0) { throw "发布前 restore 失败（退出码 $LASTEXITCODE）。" }

foreach ($outputDirectory in @($publishDirectory, $singleFileDirectory)) {
    $fullOutput = [System.IO.Path]::GetFullPath($outputDirectory)
    $fullArtifactRoot = [System.IO.Path]::GetFullPath($artifactRoot) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullOutput.StartsWith($fullArtifactRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝清理 artifacts 目录之外的发布路径：$fullOutput"
    }
    if (Test-Path -LiteralPath $fullOutput) {
        Remove-Item -LiteralPath $fullOutput -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $fullOutput | Out-Null
}

# Keep a multi-file fallback so SQLite/native loading can be diagnosed without
# relying on single-file extraction.
dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --no-restore `
    --output $publishDirectory `
    /p:PublishSingleFile=false `
    /p:PublishTrimmed=false
if ($LASTEXITCODE -ne 0) { throw "多文件 dotnet publish 失败（退出码 $LASTEXITCODE）。" }

# Produce the required double-clickable self-contained single EXE separately.
dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --no-restore `
    --output $singleFileDirectory `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:EnableCompressionInSingleFile=true `
    /p:PublishTrimmed=false
if ($LASTEXITCODE -ne 0) { throw "单文件 dotnet publish 失败（退出码 $LASTEXITCODE）。" }

if (-not (Test-Path -LiteralPath $publishedExe -PathType Leaf)) {
    throw "发布完成但未找到 $publishedExe。"
}

Copy-Item -LiteralPath $publishedExe -Destination $deliveryExe -Force
if (-not (Test-Path -LiteralPath $deliveryExe -PathType Leaf)) {
    throw "无法生成交付 EXE：$deliveryExe。"
}

if (-not $SkipSmokeTest) {
    & (Join-Path $PSScriptRoot "smoke-test.ps1") -ExecutablePath $deliveryExe
}

$file = Get-Item -LiteralPath $deliveryExe
Write-Host "[publish] 已生成 $($file.FullName)（$([math]::Round($file.Length / 1MB, 2)) MiB）。"
