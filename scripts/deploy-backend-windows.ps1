[CmdletBinding(DefaultParameterSetName = 'Public')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Public')]
    [ValidateNotNullOrEmpty()]
    [string]$Domain,

    [Parameter(Mandatory = $true, ParameterSetName = 'Local')]
    [switch]$LocalOnly,

    [ValidateRange(1024, 65535)]
    [int]$Port = 8000,

    [ValidateNotNullOrEmpty()]
    [string]$InstallRoot = (Join-Path $env:ProgramData 'PersonalFitnessPlannerCloud'),

    [string]$PythonPath = '',

    [ValidatePattern('^https://')]
    [string]$PipIndexUrl = 'https://pypi.org/simple'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$taskName = 'PersonalFitnessPlannerCloud-Backend'
$scriptDirectory = Split-Path -Parent $PSCommandPath
$repositoryRoot = Split-Path -Parent $scriptDirectory
$backendSource = Join-Path $repositoryRoot 'services\backend'
$runnerSource = Join-Path $scriptDirectory 'run-backend-windows-service.ps1'

function Write-DeployLog {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host "[deploy] $Message"
}

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw '请使用“以管理员身份运行”的 PowerShell 执行此脚本。'
    }
}

function Resolve-Python312 {
    $candidate = $null
    if ($PythonPath) {
        $candidate = (Resolve-Path -LiteralPath $PythonPath -ErrorAction Stop).Path
    }
    else {
        $launcher = Get-Command py.exe -ErrorAction SilentlyContinue
        if ($launcher) {
            $candidate = (& $launcher.Source -3.12 -c 'import sys; print(sys.executable)')
            if ($LASTEXITCODE -ne 0) {
                $candidate = $null
            }
            elseif ($candidate) {
                $candidate = $candidate.Trim()
            }
        }
        if (-not $candidate) {
            $python = Get-Command python.exe -ErrorAction SilentlyContinue
            if ($python) {
                $candidate = $python.Source
            }
        }
    }

    if (-not $candidate -or -not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw '未找到 Python 3.12。请从 python.org 安装 64 位 Python 3.12，或用 -PythonPath 指定 python.exe。'
    }

    # Windows PowerShell 5.1 removes nested double quotes while constructing
    # native-process arguments. Keep this probe quote-free so ``python -c``
    # receives the same source under both Windows PowerShell and PowerShell 7.
    $versionOutput = @(& $candidate -c 'import sys; print(sys.version_info[0], sys.version_info[1], sep=chr(46))')
    $versionExitCode = $LASTEXITCODE
    $version = if ($versionOutput.Count -gt 0) { ([string]$versionOutput[-1]).Trim() } else { '' }
    if ($versionExitCode -ne 0 -or $version -ne '3.12') {
        throw "后端要求 Python 3.12，当前解释器版本为 $version：$candidate"
    }
    return [System.IO.Path]::GetFullPath($candidate)
}

function Stop-BackendTask {
    $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if (-not $task) {
        return
    }

    if ($task.State -eq 'Running') {
        Write-DeployLog '停止旧的后端计划任务'
        Stop-ScheduledTask -TaskName $taskName
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        do {
            Start-Sleep -Milliseconds 500
            $task = Get-ScheduledTask -TaskName $taskName
        } while ($task.State -eq 'Running' -and [DateTime]::UtcNow -lt $deadline)
        if ($task.State -eq 'Running') {
            throw '旧后端任务在 20 秒内没有停止；未替换应用文件。'
        }
    }
}

function Wait-BackendLiveness {
    param([Parameter(Mandatory = $true)][int]$BackendPort)

    for ($attempt = 1; $attempt -le 90; $attempt++) {
        try {
            $response = Invoke-WebRequest -Uri "http://127.0.0.1:$BackendPort/health/live" -UseBasicParsing -TimeoutSec 2
            if ($response.StatusCode -eq 200) {
                return $true
            }
        }
        catch {
            Start-Sleep -Seconds 1
        }
    }
    return $false
}

function Set-ManagedInstallPermissions {
    param(
        [Parameter(Mandatory = $true)][string]$ManagedRoot,
        [Parameter(Mandatory = $true)][string[]]$WritablePaths,
        [Parameter(Mandatory = $true)][string]$InstallerGrant
    )

    # A failed older deployment may have left child files with protected ACLs
    # that no longer inherit the repaired root permissions. Take ownership only
    # after the caller has validated the project marker and rejected reparse
    # points, then rebuild one predictable inheritance tree.
    & takeown.exe /F $ManagedRoot /A /R /D Y /SKIPSL | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "无法取得既有安装目录的所有权：$ManagedRoot"
    }

    & icacls.exe $ManagedRoot /inheritance:r /grant:r $InstallerGrant '*S-1-5-19:(OI)(CI)RX' '*S-1-5-32-544:(OI)(CI)F' /Q | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "无法设置安装目录根 ACL：$ManagedRoot"
    }

    $childrenPattern = Join-Path $ManagedRoot '*'
    & icacls.exe $childrenPattern /reset /T /Q | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "无法重置安装目录子项 ACL：$ManagedRoot"
    }

    foreach ($writablePath in $WritablePaths) {
        & icacls.exe $writablePath /inheritance:e /grant:r '*S-1-5-19:(OI)(CI)M' /Q | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "无法设置服务可写目录 ACL：$writablePath"
        }
    }
}

function Write-BackendTaskDiagnostics {
    param(
        [Parameter(Mandatory = $true)][int]$BackendPort,
        [Parameter(Mandatory = $true)][string]$LogsPath
    )

    $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if ($task) {
        $taskInfo = Get-ScheduledTaskInfo -TaskName $taskName -ErrorAction SilentlyContinue
        $resultText = if ($taskInfo) {
            $resultValue = [int64]$taskInfo.LastTaskResult
            $resultHex = '{0:X8}' -f ($resultValue -band [uint32]::MaxValue)
            "$resultValue (0x$resultHex)"
        }
        else {
            'unknown'
        }
        Write-Warning "计划任务状态：$($task.State)；最近返回码：$resultText"
    }
    else {
        Write-Warning "未找到计划任务 $taskName；任务注册可能被系统安全策略阻止。"
    }

    foreach ($logName in @('backend.stderr.log', 'backend.stdout.log')) {
        $logPath = Join-Path $LogsPath $logName
        if (Test-Path -LiteralPath $logPath -PathType Leaf) {
            Write-Warning "后端日志：$logPath"
            Get-Content -LiteralPath $logPath -Tail 100
        }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $LogsPath 'backend.stderr.log')) -and
        -not (Test-Path -LiteralPath (Join-Path $LogsPath 'backend.stdout.log'))) {
        Write-Warning '任务没有创建任何日志，通常表示 LOCAL SERVICE 无法读取任务脚本或服务配置。'
    }
    Write-Warning "本机探针：http://127.0.0.1:$BackendPort/health/live"
}

function Assert-BackendPortAvailable {
    param([Parameter(Mandatory = $true)][int]$BackendPort)

    $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $BackendPort -ErrorAction SilentlyContinue)
    if ($listeners.Count -eq 0) {
        return
    }

    $descriptions = foreach ($ownerId in @($listeners.OwningProcess | Sort-Object -Unique)) {
        $process = Get-Process -Id $ownerId -ErrorAction SilentlyContinue
        if ($process) {
            "$($process.ProcessName) (PID $ownerId)"
        }
        else {
            "PID $ownerId"
        }
    }
    throw "后端端口 $BackendPort 已被占用：$($descriptions -join ', ')。请关闭冲突程序，或用 -Port 指定其他空闲端口。"
}

if ($env:OS -ne 'Windows_NT') {
    throw '此脚本只能在 Windows 上运行。'
}
Assert-Administrator

$pipIndexUri = [Uri]$PipIndexUrl
if (-not $pipIndexUri.IsAbsoluteUri -or $pipIndexUri.Scheme -ne 'https' -or $pipIndexUri.UserInfo) {
    throw 'PipIndexUrl 必须是未内嵌账号或令牌的绝对 HTTPS URL。'
}
$installerSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$installerFullControl = "*$($installerSid):(OI)(CI)F"

foreach ($requiredPath in @(
    (Join-Path $backendSource 'app\main.py'),
    (Join-Path $backendSource 'requirements.lock'),
    $runnerSource
)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "仓库文件不完整：$requiredPath"
    }
}

$installPath = [System.IO.Path]::GetFullPath($InstallRoot)
$installDriveRoot = [System.IO.Path]::GetPathRoot($installPath)
if ($installPath.TrimEnd('\') -eq $installDriveRoot.TrimEnd('\')) {
    throw 'InstallRoot 不能是磁盘根目录。'
}
$installMarkerPath = Join-Path $installPath '.personal-fitness-planner-native-install'
if (Test-Path -LiteralPath $installPath -PathType Container) {
    $installItem = Get-Item -LiteralPath $installPath -Force
    if (($installItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "InstallRoot 不能是符号链接或目录联接：$installPath"
    }
    $existingItems = @(Get-ChildItem -LiteralPath $installPath -Force -ErrorAction Stop)
    if ($existingItems.Count -gt 0 -and -not (Test-Path -LiteralPath $installMarkerPath -PathType Leaf)) {
        throw "InstallRoot 已包含其他文件且没有项目管理标记，脚本不会修改该目录：$installPath"
    }
}

$normalizedDomain = ''
if ($PSCmdlet.ParameterSetName -eq 'Public') {
    $normalizedDomain = $Domain.Trim().ToLowerInvariant()
    if ($normalizedDomain.Length -gt 253 -or
        $normalizedDomain -notmatch '^[a-z0-9](?:[a-z0-9.-]*[a-z0-9])$' -or
        $normalizedDomain -notmatch '\.' -or
        $normalizedDomain.Contains('..')) {
        throw "域名格式无效：$Domain"
    }
    foreach ($label in $normalizedDomain.Split('.')) {
        if ($label.Length -gt 63 -or $label -notmatch '^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$') {
            throw "域名标签格式无效：$label"
        }
    }
}

$python = Resolve-Python312
$appPath = Join-Path $installPath 'app'
$appNewPath = Join-Path $installPath 'app.new'
$appPreviousPath = Join-Path $installPath 'app.previous'
$venvPath = Join-Path $installPath 'venv'
$venvNewPath = Join-Path $installPath 'venv.new'
$venvPreviousPath = Join-Path $installPath 'venv.previous'
$dataPath = Join-Path $installPath 'data'
$configDirectory = Join-Path $installPath 'config'
$serviceDirectory = Join-Path $installPath 'service'
$logsPath = Join-Path $installPath 'logs'
$serviceConfigPath = Join-Path $configDirectory 'service-config.json'
$installedRunnerPath = Join-Path $serviceDirectory 'run-backend.ps1'

Write-DeployLog "安装目录：$installPath"
New-Item -ItemType Directory -Path $installPath, $dataPath, $configDirectory, $serviceDirectory, $logsPath -Force | Out-Null
if (-not (Test-Path -LiteralPath $installMarkerPath -PathType Leaf)) {
    Set-Content -LiteralPath $installMarkerPath -Value 'Managed by PersonalFitnessPlannerCloud native deploy script.' -Encoding ASCII
}

$existingReparsePoints = @(
    Get-ChildItem -LiteralPath $installPath -Force -Recurse -ErrorAction Stop |
        Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }
)
if ($existingReparsePoints.Count -gt 0) {
    throw "安装目录中存在符号链接或目录联接，脚本不会递归修改 ACL：$($existingReparsePoints[0].FullName)"
}
Write-DeployLog '修复并统一既有安装目录权限'
Set-ManagedInstallPermissions -ManagedRoot $installPath -WritablePaths @($dataPath, $logsPath) -InstallerGrant $installerFullControl

foreach ($stalePath in @($appNewPath, $venvNewPath)) {
    if (Test-Path -LiteralPath $stalePath) {
        Remove-Item -LiteralPath $stalePath -Recurse -Force
    }
}

Write-DeployLog '复制后端程序到临时发布目录'
New-Item -ItemType Directory -Path $appNewPath -Force | Out-Null
foreach ($directoryName in @('app', 'alembic', 'scripts', 'contracts')) {
    Copy-Item -LiteralPath (Join-Path $backendSource $directoryName) -Destination $appNewPath -Recurse -Force
}
foreach ($fileName in @('alembic.ini', 'pyproject.toml', 'requirements.lock')) {
    Copy-Item -LiteralPath (Join-Path $backendSource $fileName) -Destination (Join-Path $appNewPath $fileName) -Force
}

Write-DeployLog '创建独立 Python 3.12 虚拟环境并安装锁定依赖'
& $python -m venv $venvNewPath
if ($LASTEXITCODE -ne 0) {
    throw '创建 Python 虚拟环境失败。请确认 Python 3.12 安装时包含 pip 和 venv。'
}
$venvNewPython = Join-Path $venvNewPath 'Scripts\python.exe'
$pipArguments = @(
    '-m', 'pip', 'install',
    '--disable-pip-version-check',
    '--require-hashes',
    '--no-deps',
    '--index-url', $PipIndexUrl,
    '-r', (Join-Path $appNewPath 'requirements.lock')
)
& $venvNewPython @pipArguments
if ($LASTEXITCODE -ne 0) {
    throw '安装后端 Python 依赖失败。'
}

$environmentName = if ($LocalOnly) { 'development' } else { 'production' }
$corsOrigins = if ($LocalOnly) {
    @("http://127.0.0.1:$Port", "http://localhost:$Port")
}
else {
    @("https://$normalizedDomain")
}
$runtimeConfigPath = Join-Path $dataPath 'backend-config.json'
$databasePath = Join-Path $dataPath 'fitness.db'
$jwtSecretPath = Join-Path $dataPath 'jwt-secret'

$env:ENVIRONMENT = $environmentName
$env:DATABASE_BACKEND = 'sqlite'
$env:DATABASE_URL = ''
$env:SQLITE_DATABASE_PATH = $databasePath
$env:JWT_SECRET = ''
$env:RUNTIME_CONFIG_PATH = $runtimeConfigPath
$env:CORS_ORIGINS = ConvertTo-Json -InputObject $corsOrigins -Compress
$env:PYTHONDONTWRITEBYTECODE = '1'
Push-Location -LiteralPath $appNewPath
try {
    & $venvNewPython -c 'from app.main import app; assert app.title'
    if ($LASTEXITCODE -ne 0) {
        throw '后端导入检查失败。'
    }
}
finally {
    Pop-Location
}

$currentAppExists = Test-Path -LiteralPath $appPath -PathType Container
$currentVenvExists = Test-Path -LiteralPath (Join-Path $venvPath 'Scripts\python.exe') -PathType Leaf
if ($currentAppExists -ne $currentVenvExists) {
    throw '既有 Windows 原生部署不完整，脚本不会自动替换。'
}
$hadPreviousRelease = $currentAppExists -and $currentVenvExists
$releaseSwapStarted = $false

try {
    Stop-BackendTask
    Assert-BackendPortAvailable -BackendPort $Port

    foreach ($previousPath in @($appPreviousPath, $venvPreviousPath)) {
        if (Test-Path -LiteralPath $previousPath) {
            Remove-Item -LiteralPath $previousPath -Recurse -Force
        }
    }
    $releaseSwapStarted = $true
    if ($currentAppExists) {
        Move-Item -LiteralPath $appPath -Destination $appPreviousPath
    }
    if ($currentVenvExists) {
        Move-Item -LiteralPath $venvPath -Destination $venvPreviousPath
    }
    Move-Item -LiteralPath $appNewPath -Destination $appPath
    Move-Item -LiteralPath $venvNewPath -Destination $venvPath

    Copy-Item -LiteralPath $runnerSource -Destination $installedRunnerPath -Force
    $serviceConfig = [ordered]@{
        environment = $environmentName
        database_backend = 'sqlite'
        sqlite_database_path = $databasePath
        runtime_config_path = $runtimeConfigPath
        cors_origins = $corsOrigins
        port = $Port
    }
    $serviceConfig | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $serviceConfigPath -Encoding UTF8

    $reparsePoints = @(
        Get-ChildItem -LiteralPath $installPath -Force -Recurse -ErrorAction Stop |
            Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }
    )
    if ($reparsePoints.Count -gt 0) {
        throw "安装目录中存在符号链接或目录联接，脚本不会递归修改 ACL：$($reparsePoints[0].FullName)"
    }

    Write-DeployLog '限制程序为只读，并仅向 LOCAL SERVICE 开放数据与日志写入权限'
    Set-ManagedInstallPermissions -ManagedRoot $installPath -WritablePaths @($dataPath, $logsPath) -InstallerGrant $installerFullControl

    # Fail before task registration if the installer itself cannot read the two
    # files that LOCAL SERVICE must consume at process start.
    Get-Content -LiteralPath $serviceConfigPath -Raw -Encoding UTF8 | Out-Null
    Get-Content -LiteralPath $installedRunnerPath -Raw -Encoding UTF8 | Out-Null

    $windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $actionArguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}" -InstallRoot "{1}"' -f $installedRunnerPath, $installPath
    $action = New-ScheduledTaskAction -Execute $windowsPowerShell -Argument $actionArguments
    $trigger = New-ScheduledTaskTrigger -AtStartup
    $principal = New-ScheduledTaskPrincipal -UserId 'S-1-5-19' -LogonType ServiceAccount -RunLevel Limited
    $taskSettingsParameters = @{
        StartWhenAvailable = $true
        RestartCount = 999
        RestartInterval = (New-TimeSpan -Minutes 1)
        ExecutionTimeLimit = [TimeSpan]::Zero
        AllowStartIfOnBatteries = $true
        DontStopIfGoingOnBatteries = $true
    }
    $settings = New-ScheduledTaskSettingsSet @taskSettingsParameters

    Write-DeployLog '注册并启动 Windows 开机任务'
    $registerTaskParameters = @{
        TaskName = $taskName
        Action = $action
        Trigger = $trigger
        Principal = $principal
        Settings = $settings
        Description = 'Personal Fitness Planner Cloud backend (native Python)'
        Force = $true
    }
    Register-ScheduledTask @registerTaskParameters | Out-Null
    Start-ScheduledTask -TaskName $taskName

    if (-not (Wait-BackendLiveness -BackendPort $Port)) {
        Write-BackendTaskDiagnostics -BackendPort $Port -LogsPath $logsPath
        throw '后端在 90 秒内没有通过 liveness 检查。'
    }
}
catch {
    $deploymentError = $_
    if ($releaseSwapStarted -and $hadPreviousRelease) {
        Write-Warning '新版本部署失败，正在恢复上一版本。'
        try {
            Stop-BackendTask
            if (Test-Path -LiteralPath $appPreviousPath -PathType Container) {
                if (Test-Path -LiteralPath $appPath) {
                    Remove-Item -LiteralPath $appPath -Recurse -Force
                }
                Move-Item -LiteralPath $appPreviousPath -Destination $appPath
            }
            if (Test-Path -LiteralPath (Join-Path $venvPreviousPath 'Scripts\python.exe') -PathType Leaf) {
                if (Test-Path -LiteralPath $venvPath) {
                    Remove-Item -LiteralPath $venvPath -Recurse -Force
                }
                Move-Item -LiteralPath $venvPreviousPath -Destination $venvPath
            }
            if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
                Start-ScheduledTask -TaskName $taskName
            }
        }
        catch {
            Write-Warning "自动恢复上一版本时也发生错误：$($_.Exception.Message)"
        }
    }
    throw $deploymentError
}

Write-Host ''
Write-Host '============================================================'
Write-Host 'Windows 原生后端部署完成。'
Write-Host "计划任务：$taskName"
Write-Host "本地数据库：$databasePath"
Write-Host "JWT 密钥文件：$jwtSecretPath"
Write-Host "日志目录：$logsPath"
if ($LocalOnly) {
    Write-Host "Web 控制台：http://127.0.0.1:$Port/web/"
    Write-Host '当前仅本机可访问，适合个人电脑或先通过远程桌面完成配置。'
}
else {
    Write-Host "Web 控制台：https://$normalizedDomain/web/"
    Write-Host "请让 IIS、Caddy 或其他 HTTPS 反向代理转发到 127.0.0.1:$Port。"
}
Write-Host 'SQLite 表结构和默认训练计划已自动初始化，不需要安装或配置 MySQL。'
Write-Host '请只映射后端 Web 端口；不要把数据库文件作为网络共享直接暴露。'
Write-Host '============================================================'
