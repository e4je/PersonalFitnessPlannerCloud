# 后端非 Docker 部署

项目提供两种不依赖 Docker 的单机部署方式。两种方式都使用同一套 FastAPI 后端、首次数据库配置页面和固定的 `fitness` 数据库，不会把 MySQL 密码写进仓库。

| 系统 | 后台运行方式 | HTTPS | 适合场景 |
| --- | --- | --- | --- |
| Ubuntu | Python 3.12 虚拟环境 + systemd | 脚本自动配置 Nginx 与 Let's Encrypt | 长期在线的公网服务器 |
| Windows | Python 3.12 虚拟环境 +计划任务 | 公网模式需另配 Caddy、IIS 或现有网关 | Windows Server、长期在线的 Windows 电脑 |

原生部署只安装后端，MySQL 8 需要已经存在于本机、内网或云数据库。与容器部署不同，如果 MySQL 就在同一台机器上，首次向导通常可以填写 `127.0.0.1`；仍应限制 MySQL 监听地址和防火墙来源，不要向公网开放 3306。

同一台机器只能选择一种后端运行方式：Docker、Ubuntu systemd 或 Windows 计划任务都会占用后端端口，不能同时启动。切换方式前先停止旧后端，但不要删除旧运行配置；需要保留现有登录状态时按文末说明迁移 `backend-config.json`。

## Ubuntu 原生部署

### 前置条件

- Ubuntu 22.04、24.04 或 26.04。
- 一个已解析到服务器公网 IP 的域名，以及开放的 TCP 80/443。
- Python 3.12。脚本会在系统软件源提供该版本时自动安装；如果系统使用自定义 Python，可通过 `--python-bin` 指定。
- 后端宿主机能够访问的 MySQL 8。

从公开仓库拉取并执行：

```bash
git clone https://github.com/e4je/PersonalFitnessPlannerCloud.git
cd PersonalFitnessPlannerCloud

sudo bash scripts/deploy-backend-ubuntu-native.sh \
  --domain fitness.example.com \
  --email admin@example.com
```

脚本会显示变更摘要并要求确认。自动化执行可以增加 `--yes`。如果 `python3.12` 不在系统路径中：

```bash
sudo bash scripts/deploy-backend-ubuntu-native.sh \
  --domain fitness.example.com \
  --email admin@example.com \
  --python-bin /opt/python-3.12/bin/python3.12
```

只有在默认 PyPI 确实无法连接时，才应指定自己信任的 HTTPS 镜像：

```bash
sudo bash scripts/deploy-backend-ubuntu-native.sh \
  --domain fitness.example.com \
  --email admin@example.com \
  --pip-index-url https://你的可信镜像/simple
```

依赖仍使用 `requirements.lock` 中的 SHA-256 哈希验证。不要使用来源不明的包镜像。

### 脚本创建的资源

- `/opt/personal-fitness-planner/app`：当前后端源码副本。
- `/opt/personal-fitness-planner/venv`：当前 Python 3.12 虚拟环境。
- `/opt/personal-fitness-planner/app.previous` 和 `venv.previous`：上一次发布，用于失败恢复。
- `/var/lib/personal-fitness-planner/backend-config.json`：首次设置后保存数据库连接和 JWT 密钥，目录权限仅允许服务账号访问。
- `/etc/personal-fitness-planner/backend-native.env`：不含数据库密码的 systemd 环境配置。
- `personal-fitness-planner-backend.service`：以受限 `pfp-backend` 账号运行的单实例服务。
- Nginx 站点与 Certbot 管理的 HTTPS 证书。

后端仅监听 `127.0.0.1:8000`，公网请求必须经过 Nginx。systemd 单元启用了只读系统目录、独立临时目录、无新增权限、空 capability 集合等限制；唯一的应用可写位置是 `/var/lib/personal-fitness-planner`。

### 首次配置数据库

脚本结束时会显示一次性 `setup_token`。打开：

```text
https://fitness.example.com/web/
```

填写 MySQL 地址、端口、账号、密码和初始化码。后端会：

1. 检查 MySQL 版本并识别固定的 `fitness` 库；
2. 库不存在时创建，存在时直接读取现有结构；
3. 执行 Alembic 升级和幂等默认计划初始化；
4. 生成 JWT 密钥并把私有运行配置原子写入数据目录。

`/health/live` 在向导阶段返回 200；`/health/ready` 会在初始化完成后从 503 变为 200。

### Ubuntu 运维

查看状态和日志：

```bash
sudo systemctl status personal-fitness-planner-backend
sudo journalctl -u personal-fitness-planner-backend -n 200 --no-pager
curl -fsS https://fitness.example.com/health/live
curl -fsS https://fitness.example.com/health/ready
```

更新时先备份数据库，再拉取代码并重新运行相同脚本：

```bash
cd ~/PersonalFitnessPlannerCloud
git pull --ff-only origin main
sudo bash scripts/deploy-backend-ubuntu-native.sh \
  --domain fitness.example.com \
  --email admin@example.com \
  --yes
```

脚本会先构建全新的虚拟环境，通过导入检查后才停止旧服务；新服务无法通过健康检查时会恢复上一版本。`/var/lib/personal-fitness-planner` 不参与发布替换，因此数据库连接和 JWT 不会被更新操作覆盖。

数据库初始化完成后，可在 root shell 中交互读取管理员密码并执行一次性创建命令：

```bash
sudo -i
read -r -p "Admin email: " ADMIN_EMAIL
read -r -s -p "Admin password: " ADMIN_PASSWORD && echo
export ADMIN_EMAIL ADMIN_PASSWORD
export ENVIRONMENT=production
export RUNTIME_CONFIG_PATH=/var/lib/personal-fitness-planner/backend-config.json
cd /opt/personal-fitness-planner/app
sudo --preserve-env=ADMIN_EMAIL,ADMIN_PASSWORD,ENVIRONMENT,RUNTIME_CONFIG_PATH \
  -u pfp-backend /opt/personal-fitness-planner/venv/bin/python \
  -m scripts.create_admin
unset ADMIN_EMAIL ADMIN_PASSWORD ENVIRONMENT RUNTIME_CONFIG_PATH
exit
```

## Windows 原生部署

### 前置条件

- Windows 10/11 或启用了任务计划程序的 Windows Server。
- 64 位 Python 3.12；建议安装 Python Launcher (`py.exe`)。
- 管理员 PowerShell。
- 后端能够访问的 MySQL 8。

公开仓库可直接使用 HTTPS 拉取：

```powershell
git clone https://github.com/e4je/PersonalFitnessPlannerCloud.git
Set-Location .\PersonalFitnessPlannerCloud
```

仅在这台 Windows 机器上使用时，选择本机模式：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\deploy-backend-windows.ps1 `
  -LocalOnly
```

本机模式监听 `127.0.0.1:8000`，打开 `http://127.0.0.1:8000/web/`。它不会创建公网防火墙规则，适合个人电脑、远程桌面内使用或先完成首次配置。

需要让 Android、其他电脑或公网访问时，使用域名模式：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\deploy-backend-windows.ps1 `
  -Domain fitness.example.com
```

域名模式仍只监听 `127.0.0.1:8000`，并启用生产 HTTPS 要求。必须让 Caddy、IIS 或已有的 HTTPS 网关在同一台 Windows 机器上终止 TLS，再反向代理到该地址。最小 Caddy 站点逻辑为：

```caddyfile
fitness.example.com {
    reverse_proxy 127.0.0.1:8000
}
```

反向代理必须保留 `Host`，并发送正确的 `X-Forwarded-Proto: https`。项目只信任来自 loopback 的代理头。Android 客户端不接受明文 HTTP，因此公网或跨设备使用不能选择本机 HTTP 模式。

如果没有 `py.exe`，可显式指定解释器：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\deploy-backend-windows.ps1 `
  -LocalOnly `
  -PythonPath 'C:\Program Files\Python312\python.exe'
```

### Windows 安装内容

默认安装根目录为：

```text
C:\ProgramData\PersonalFitnessPlannerCloud
```

其中：

- `app`、`venv`：当前程序和虚拟环境；
- `app.previous`、`venv.previous`：上一次发布；
- `data\backend-config.json`：数据库连接与 JWT 密钥；
- `logs\backend.stdout.log`、`logs\backend.stderr.log`：服务日志；
- `config\service-config.json`：不含 MySQL 密码的服务参数；
- 计划任务 `PersonalFitnessPlannerCloud-Backend`：使用受限的本机 `LOCAL SERVICE` 账号开机启动，失败后自动重试。

安装目录 ACL 仅授予 `LOCAL SERVICE` 和本机管理员组：服务账号对程序、虚拟环境和服务配置只有读取/执行权限，只能写入 `data` 与 `logs`。后端不会监听公网网卡，也不会自动修改 Windows 防火墙。

脚本结束时会显示 Web 地址和一次性 `setup_token`。数据库初始化步骤与 Ubuntu 完全相同。如果未显示令牌，可在管理员 PowerShell 查看：

```powershell
Get-Content "$env:ProgramData\PersonalFitnessPlannerCloud\data\setup-token"
Get-Content "$env:ProgramData\PersonalFitnessPlannerCloud\logs\backend.stderr.log" -Tail 100
```

### Windows 运维

```powershell
Get-ScheduledTask -TaskName PersonalFitnessPlannerCloud-Backend
Stop-ScheduledTask -TaskName PersonalFitnessPlannerCloud-Backend
Start-ScheduledTask -TaskName PersonalFitnessPlannerCloud-Backend
Invoke-WebRequest http://127.0.0.1:8000/health/live -UseBasicParsing
```

更新时先备份数据库，然后在仓库中拉取并重新运行原命令：

```powershell
git pull --ff-only origin main
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\deploy-backend-windows.ps1 `
  -Domain fitness.example.com
```

数据目录不会被更新脚本替换。创建首个管理员时，在管理员 PowerShell 中运行：

```powershell
$installRoot = Join-Path $env:ProgramData 'PersonalFitnessPlannerCloud'
$env:ADMIN_EMAIL = Read-Host 'Admin email'
$securePassword = Read-Host 'Admin password' -AsSecureString
$passwordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassword)
try {
    $env:ADMIN_PASSWORD = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPointer)
    $env:ENVIRONMENT = 'production'
    $env:RUNTIME_CONFIG_PATH = Join-Path $installRoot 'data\backend-config.json'
    Push-Location (Join-Path $installRoot 'app')
    try {
        & (Join-Path $installRoot 'venv\Scripts\python.exe') -m scripts.create_admin
        if ($LASTEXITCODE -ne 0) { throw '创建管理员失败。' }
    }
    finally {
        Pop-Location
    }
}
finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPointer)
    Remove-Item Env:ADMIN_EMAIL, Env:ADMIN_PASSWORD, Env:ENVIRONMENT, Env:RUNTIME_CONFIG_PATH -ErrorAction SilentlyContinue
}
```

## 换服务器与 JWT

连接同一个 MySQL 只能恢复账号、计划和训练数据，不会自动恢复旧服务器的 JWT 密钥。希望现有设备继续保持登录时，迁移旧服务器的 `backend-config.json` 到新系统对应的数据目录，并在复制前停止两端后端服务；文件包含数据库密码和 JWT，必须使用加密通道传输并保持私有权限。

不迁移该文件也可以：在新服务器完成首次向导后会生成新的 JWT 密钥，所有设备重新登录即可，数据库中的账号和健身数据不会丢失。
