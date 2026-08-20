#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$ExecutablePath,
    [string]$DataDirectory,
    [ValidateRange(1, 600)]
    [int]$TimeoutSeconds = 90
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    $ExecutablePath = Join-Path $repoRoot "artifacts\PersonalFitnessPlanner.exe"
}
if ([string]::IsNullOrWhiteSpace($DataDirectory)) {
    $DataDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("健身规划 烟雾测试 " + [Guid]::NewGuid().ToString("N"))
}

$ExecutablePath = [System.IO.Path]::GetFullPath($ExecutablePath)
$DataDirectory = [System.IO.Path]::GetFullPath($DataDirectory)
$dataRoot = [System.IO.Path]::GetPathRoot($DataDirectory)
if ($DataDirectory.Length -gt $dataRoot.Length) {
    $separators = [char[]]@(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $DataDirectory = $DataDirectory.TrimEnd($separators)
}

if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
    throw "未找到待测试 EXE：$ExecutablePath。请先运行 scripts\publish.ps1。"
}
if ($DataDirectory -notmatch '[\p{IsCJKUnifiedIdeographs}]' -or $DataDirectory -notmatch '\s') {
    throw "烟雾测试数据目录必须同时包含中文和空格：$DataDirectory"
}
if ($DataDirectory.Contains('"')) {
    throw "烟雾测试数据目录不能包含双引号：$DataDirectory"
}

New-Item -ItemType Directory -Force -Path $DataDirectory | Out-Null
Write-Host "[smoke] 运行：$ExecutablePath --smoke-test --data-dir `"$DataDirectory`""

$quotedDataDirectory = '"' + $DataDirectory + '"'
$process = Start-Process `
    -FilePath $ExecutablePath `
    -ArgumentList @("--smoke-test", "--data-dir", $quotedDataDirectory) `
    -WorkingDirectory (Split-Path -Parent $ExecutablePath) `
    -WindowStyle Hidden `
    -PassThru

$timeoutMilliseconds = [int]($TimeoutSeconds * 1000)
if (-not $process.WaitForExit($timeoutMilliseconds)) {
    try {
        $process.Kill($true)
    }
    catch [System.Management.Automation.MethodException] {
        # Windows PowerShell 5.1 lacks Kill(entireProcessTree); retain a safe fallback.
        $process.Kill()
    }
    $process.WaitForExit()
    throw "EXE 烟雾测试在 $TimeoutSeconds 秒内未退出，已终止进程树；测试数据保留在 $DataDirectory。"
}

$process.Refresh()
$exitCode = $process.ExitCode
if ($exitCode -ne 0) {
    throw "EXE 烟雾测试失败（退出码 $exitCode）；测试数据保留在 $DataDirectory。"
}

$database = Join-Path $DataDirectory "fitness.db"
if (-not (Test-Path -LiteralPath $database -PathType Leaf)) {
    throw "EXE 返回成功，但未在中文路径创建 fitness.db：$database"
}

Write-Host "[smoke] 通过；数据目录：$DataDirectory"
