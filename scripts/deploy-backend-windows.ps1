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

    for ($attempt = 1; $attempt -le 30; $attempt++) {
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

if ($env:OS -ne 'Windows_NT') {
    throw '此脚本只能在 Windows 上运行。'
}
Assert-Administrator

$pipIndexUri = [Uri]$PipIndexUrl
if (-not $pipIndexUri.IsAbsoluteUri -or $pipIndexUri.Scheme -ne 'https' -or $pipIndexUri.UserInfo) {
    throw 'PipIndexUrl 必须是未内嵌账号或令牌的绝对 HTTPS URL。'
}

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
Set-Content -LiteralPath $installMarkerPath -Value 'Managed by PersonalFitnessPlannerCloud native deploy script.' -Encoding ASCII

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

$env:ENVIRONMENT = $environmentName
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
    & icacls.exe $installPath /inheritance:r /grant:r '*S-1-5-19:(OI)(CI)RX' '*S-1-5-32-544:(OI)(CI)F' /T /C | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw '设置安装目录 ACL 失败。'
    }
    foreach ($writablePath in @($dataPath, $logsPath)) {
        & icacls.exe $writablePath /inheritance:r /grant:r '*S-1-5-19:(OI)(CI)M' '*S-1-5-32-544:(OI)(CI)F' /T /C | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "设置可写目录 ACL 失败：$writablePath"
        }
    }

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
        $errorLog = Join-Path $logsPath 'backend.stderr.log'
        if (Test-Path -LiteralPath $errorLog) {
            Write-Warning "后端错误日志：$errorLog"
            Get-Content -LiteralPath $errorLog -Tail 80
        }
        throw '后端在 30 秒内没有通过 liveness 检查。'
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

$setupTokenPath = Join-Path $dataPath 'setup-token'
$setupToken = ''
for ($attempt = 1; $attempt -le 10 -and -not $setupToken; $attempt++) {
    if (Test-Path -LiteralPath $setupTokenPath -PathType Leaf) {
        $setupToken = (Get-Content -LiteralPath $setupTokenPath -Raw -Encoding UTF8).Trim()
    }
    if (-not $setupToken) {
        Start-Sleep -Milliseconds 500
    }
}

Write-Host ''
Write-Host '============================================================'
Write-Host 'Windows 原生后端部署完成。'
Write-Host "计划任务：$taskName"
Write-Host "运行配置：$runtimeConfigPath"
Write-Host "日志目录：$logsPath"
if ($LocalOnly) {
    Write-Host "Web 控制台：http://127.0.0.1:$Port/web/"
    Write-Host '当前仅本机可访问，适合个人电脑或先通过远程桌面完成配置。'
}
else {
    Write-Host "Web 控制台：https://$normalizedDomain/web/"
    Write-Host "请让 IIS、Caddy 或其他 HTTPS 反向代理转发到 127.0.0.1:$Port。"
}
if ($setupToken) {
    Write-Host "一次性 setup_token：$setupToken"
}
else {
    Write-Host "未读取到 setup_token，请查看：$logsPath\backend.stderr.log"
}
Write-Host '数据库名固定为 fitness；在 Web 页面填写 MySQL 地址、账号和密码。'
Write-Host '============================================================'
