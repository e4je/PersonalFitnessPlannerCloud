#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot "PersonalFitnessPlanner.sln"
$nugetConfig = Join-Path $repoRoot "NuGet.Config"
$packageCache = Join-Path $repoRoot ".packages"
$resultsDirectory = Join-Path $repoRoot "TestResults"

New-Item -ItemType Directory -Force -Path $packageCache, $resultsDirectory | Out-Null
$env:NUGET_PACKAGES = $packageCache

dotnet restore $solution --configfile $nugetConfig --packages $packageCache
if ($LASTEXITCODE -ne 0) { throw "dotnet restore 失败（退出码 $LASTEXITCODE）。" }

$arguments = @(
    "test", $solution,
    "--configuration", $Configuration,
    "--no-restore",
    "--results-directory", $resultsDirectory,
    "--logger", "trx;LogFileName=PersonalFitnessPlanner.Tests.trx"
)
if ($NoBuild) { $arguments += "--no-build" }

& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "dotnet test 失败（退出码 $LASTEXITCODE）。" }

Write-Host "[test] 测试通过；结果位于 $resultsDirectory。"
