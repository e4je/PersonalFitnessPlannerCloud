# 后端非 Docker 部署

后端默认把云端数据保存在服务器本机的 SQLite 文件中，不需要安装 MySQL。Ubuntu 与 Windows 脚本都会安装独立 Python 3.12 环境、自动执行 Alembic 迁移和默认计划初始化，并以受限系统账号运行单个后端实例。

只需要映射或代理后端 Web 端口。SQLite 没有网络端口，不要把 `fitness.db` 放在网络共享中或直接对公网开放。

## Ubuntu

前置条件：

- Ubuntu 22.04、24.04 或 26.04；
- 域名已解析到服务器，并开放 TCP 80/443；
- 一个用于 Let's Encrypt 通知的邮箱；
- 能下载 Python 包。脚本会尝试安装缺少的 Python 3.12、Nginx 和 Certbot。

```bash
git clone https://github.com/e4je/PersonalFitnessPlannerCloud.git
cd PersonalFitnessPlannerCloud
sudo bash scripts/deploy-backend-ubuntu-native.sh \
  --domain fitness.example.com \
  --email admin@example.com
```

脚本创建：

- `/opt/personal-fitness-planner/app`：当前后端程序；
- `/opt/personal-fitness-planner/venv`：独立 Python 环境；
- `/var/lib/personal-fitness-planner/fitness.db`：SQLite 数据库；
- `/var/lib/personal-fitness-planner/jwt-secret`：JWT 签名密钥；
- `/etc/personal-fitness-planner/backend-native.env`：服务环境；
- `personal-fitness-planner-backend.service`：仅监听 `127.0.0.1:8000` 的 systemd 服务；
- Nginx 反向代理和 Let's Encrypt 证书。

验证：

```bash
systemctl status personal-fitness-planner-backend --no-pager
journalctl -u personal-fitness-planner-backend -n 100 --no-pager
curl -fsS https://fitness.example.com/health/live
curl -fsS https://fitness.example.com/health/ready
```

打开 `https://fitness.example.com/web/` 即可注册或登录，不再填写数据库地址、账号、密码或 `setup_token`。

更新前先备份，再拉取并重跑同一命令：

```bash
cd ~/PersonalFitnessPlannerCloud
git pull --ff-only origin main
sudo bash scripts/deploy-backend-ubuntu-native.sh \
  --domain fitness.example.com \
  --email admin@example.com \
  --yes
```

程序更新失败时脚本会恢复上一发布；`/var/lib/personal-fitness-planner` 不参与替换。

## Windows

前置条件：Windows 10/11 或 Windows Server、64 位 Python 3.12，以及“以管理员身份运行”的 PowerShell。

本机模式示例：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\deploy-backend-windows.ps1 `
  -LocalOnly `
  -Port 18000
```

公网模式：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\deploy-backend-windows.ps1 `
  -Domain js.riniba.top `
  -Port 18000
```

公网模式仍只监听 `127.0.0.1`。请用 Caddy 或 IIS 把可信 HTTPS 转发到 `127.0.0.1:18000`。Caddy 示例：

```caddyfile
js.riniba.top {
    reverse_proxy 127.0.0.1:18000
}
```

默认安装目录：

- `C:\ProgramData\PersonalFitnessPlannerCloud\data\fitness.db`：SQLite 数据库；
- `C:\ProgramData\PersonalFitnessPlannerCloud\data\jwt-secret`：JWT 签名密钥；
- `C:\ProgramData\PersonalFitnessPlannerCloud\logs`：日志；
- `PersonalFitnessPlannerCloud-Backend`：以 `LOCAL SERVICE` 运行的开机计划任务。

如果上一次失败部署已经收紧目录权限，先在管理员 PowerShell 恢复当前管理员权限，再重跑最新脚本：

```powershell
$installRoot = 'C:\ProgramData\PersonalFitnessPlannerCloud'
$currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
& icacls.exe $installRoot /grant:r "*$($currentSid):(OI)(CI)F" /T /C
```

最新版脚本还会在确认项目管理标记且目录内没有符号链接后，自动恢复所有权并重建 ACL 继承；`LOCAL SERVICE` 对程序只有读取/执行权限，对 `data`、`logs` 有修改权限。首次迁移允许最多 90 秒。若仍失败，脚本会直接显示计划任务状态、返回码以及 stdout/stderr；“没有创建任何日志”表示任务脚本或服务配置仍被系统策略阻止读取。

端口冲突时不要结束未知进程，改用另一个 `-Port`。验证：

```powershell
Get-ScheduledTask -TaskName PersonalFitnessPlannerCloud-Backend
Invoke-WebRequest http://127.0.0.1:18000/health/live -UseBasicParsing
Invoke-WebRequest http://127.0.0.1:18000/health/ready -UseBasicParsing
Get-Content 'C:\ProgramData\PersonalFitnessPlannerCloud\logs\backend.stderr.log' -Tail 100
```

## 创建首个管理员

公开注册只创建普通用户。需要管理员时，使用服务器上的一次性运维命令；密码不要写入脚本或 shell 历史。

Ubuntu：

```bash
sudo -i
read -r -p "Admin email: " ADMIN_EMAIL
read -r -s -p "Admin password: " ADMIN_PASSWORD && echo
export ADMIN_EMAIL ADMIN_PASSWORD ENVIRONMENT=production
export RUNTIME_CONFIG_PATH=/var/lib/personal-fitness-planner/backend-config.json
cd /opt/personal-fitness-planner/app
sudo --preserve-env=ADMIN_EMAIL,ADMIN_PASSWORD,ENVIRONMENT,RUNTIME_CONFIG_PATH \
  -u pfp-backend /opt/personal-fitness-planner/venv/bin/python -m scripts.create_admin
unset ADMIN_EMAIL ADMIN_PASSWORD ENVIRONMENT RUNTIME_CONFIG_PATH
exit
```

Windows：

```powershell
$installRoot = Join-Path $env:ProgramData 'PersonalFitnessPlannerCloud'
$env:ADMIN_EMAIL = Read-Host 'Admin email'
$securePassword = Read-Host 'Admin password' -AsSecureString
$pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassword)
try {
    $env:ADMIN_PASSWORD = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    $env:ENVIRONMENT = 'production'
    $env:RUNTIME_CONFIG_PATH = Join-Path $installRoot 'data\backend-config.json'
    Push-Location (Join-Path $installRoot 'app')
    & (Join-Path $installRoot 'venv\Scripts\python.exe') -m scripts.create_admin
    Pop-Location
}
finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    Remove-Item Env:ADMIN_EMAIL,Env:ADMIN_PASSWORD,Env:ENVIRONMENT,Env:RUNTIME_CONFIG_PATH -ErrorAction SilentlyContinue
}
```

## 备份、恢复与换服务器

运行中一致性备份：

```bash
cd /opt/personal-fitness-planner/app
sudo -u pfp-backend env \
  ENVIRONMENT=production \
  RUNTIME_CONFIG_PATH=/var/lib/personal-fitness-planner/backend-config.json \
  /opt/personal-fitness-planner/venv/bin/python \
  -m scripts.backup_sqlite
```

```powershell
$root = 'C:\ProgramData\PersonalFitnessPlannerCloud'
$env:ENVIRONMENT = 'production'
$env:RUNTIME_CONFIG_PATH = "$root\data\backend-config.json"
Push-Location "$root\app"
& "$root\venv\Scripts\python.exe" -m scripts.backup_sqlite `
  --output 'D:\Backups\fitness.db'
Pop-Location
Remove-Item Env:ENVIRONMENT,Env:RUNTIME_CONFIG_PATH -ErrorAction SilentlyContinue
```

恢复时先停止服务，保留当前 `fitness.db`，再用备份替换并启动服务。不要在后端运行时直接复制实时数据库文件；使用上面的备份命令。

换服务器时至少迁移 `fitness.db`。若同时迁移 `jwt-secret`，已登录设备可继续使用现有令牌；不迁移密钥不会丢账号、计划或训练数据，但所有设备都要重新登录。两个文件都含敏感信息，应通过加密通道传输并限制权限。

可选 MySQL 兼容模式见后端 README；新部署不需要它。
