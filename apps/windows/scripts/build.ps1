#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot "PersonalFitnessPlanner.sln"
$nugetConfig = Join-Path $repoRoot "NuGet.Config"
$packageCache = Join-Path $repoRoot ".packages"

if ([System.Environment]::OSVersion.Platform -ne [System.PlatformID]::Win32NT) {
    throw "PersonalFitnessPlanner 是 Windows 桌面应用，构建脚本只能在 Windows 上运行。"
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -eq $dotnet) {
    throw "未找到 dotnet。请安装 .NET 8 SDK（x64）后重试。"
}

$sdkVersions = @(dotnet --list-sdks | ForEach-Object {
    if ($_ -match '^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)') {
        [version]"$($Matches.major).$($Matches.minor).$($Matches.patch)"
    }
})
if (-not ($sdkVersions | Where-Object { $_.Major -ge 8 })) {
    throw "未找到可用的 .NET 8 或更高版本 SDK。"
}

New-Item -ItemType Directory -Force -Path $packageCache | Out-Null
$env:NUGET_PACKAGES = $packageCache

Write-Host "[build] SDK: $(dotnet --version)"
Write-Host "[build] NuGet 缓存: $packageCache"
dotnet restore $solution --configfile $nugetConfig --packages $packageCache
if ($LASTEXITCODE -ne 0) { throw "dotnet restore 失败（退出码 $LASTEXITCODE）。" }

dotnet build $solution --configuration $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw "dotnet build 失败（退出码 $LASTEXITCODE）。" }

Write-Host "[build] $Configuration 构建完成。"
