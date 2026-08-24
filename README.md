# Personal Fitness Planner Cloud

一个供个人使用的跨平台健身记录与训练计划项目，包含 Android 客户端、Windows 桌面端和可选的云同步服务。

不连接服务器时，Android 和 Windows 客户端都可以独立记录训练；部署后端后，两端可以同步计划、训练记录、每日状态和有氧记录。项目内置一套适合新手的 A/B 全身训练计划，并根据训练历史、恢复情况和计划进度给出当天建议。

> 本项目以个人使用和自行托管为目标，不提供公开在线服务，也没有应用商店发布计划。

## 主要功能

- 内置新手增肌减脂 A/B 全身计划，每个训练日包含 8 个训练位置和可替换动作。
- 根据最近训练、休息天数、每周训练次数和疲劳程度推荐 A、B、恢复或有氧。
- 记录动作、重量、次数、组数、RIR、疼痛、动作质量和训练备注。
- 根据历史完成情况给出下次重量建议。
- 保存训练历史、每日恢复状态和有氧记录，并支持数据导出。
- Android 使用 Room 离线存储，Windows 使用 SQLite 离线存储。
- 连接云端后采用增量同步和本地待传队列，断网时仍可正常记录。
- 支持计划版本和训练快照，计划更新后不会改变过去的训练记录。
- 登录令牌在 Android Keystore 或 Windows DPAPI 中加密保存。
- 后端提供同源 Web 控制台 `/web/`：普通用户注册/登录，管理员账号管理、注册开关、用户训练概览以及计划草稿、发布和分配。
- Android/Windows 登录后可选择“上传本地”或“云端覆盖”；云端覆盖在存在未上传 Outbox 时会自动阻止，避免误丢离线记录。

## 项目组成

```text
PersonalFitnessPlannerCloud/
├─ apps/android/       Android 客户端（Kotlin、Jetpack Compose、Room）
├─ apps/windows/       Windows 客户端（.NET 10、WPF、SQLite）
├─ services/backend/   云同步服务（FastAPI、SQLAlchemy、Alembic、MySQL 8）
├─ contracts/          OpenAPI、默认计划和跨端规则测试数据
├─ infra/              Docker Compose 与 MySQL 配置
├─ scripts/            测试、构建和打包脚本
└─ docs/               架构、安全、测试和构建说明
```

各模块的详细说明：

- [Android 客户端](apps/android/README.md)
- [Windows 客户端](apps/windows/README.md)
- [后端服务](services/backend/README.md)
- [完整构建说明](docs/build-and-release.md)

## 直接下载个人使用版

每次向 GitHub 推送提交或 tag，Actions 都会自动运行测试并生成构建产物。打开仓库的 **Actions** 页面，进入成功的 `ci` 任务，在页面底部下载：

- `android-debug-and-reports`：包含可直接安装的 Debug APK 和 Android 检查报告。
- `windows-publish-and-results`：包含 `PersonalFitnessPlanner.exe`、Windows 自包含发布目录和测试结果。

如果只是想保留一个版本节点，可以打 tag：

```powershell
git tag -a v1.0.0 -m "v1.0.0"
git push origin v1.0.0
```

符合 `v1.2.3` 或 `1.2.3` 格式的 tag 还会触发独立的 Release workflow；检查全部通过后，GitHub Release 会附带 APK、Windows EXE 和 SHA-256 清单。tag 不会自动修改应用内部版本号。

### 安装提示

- Android 产物是 Debug APK，适合个人安装。如果手机上已经安装了签名不同的同包名应用，需要先卸载旧版本。
- Windows EXE 是 `win-x64` 自包含程序，不需要预装 .NET Runtime；因为没有代码签名，首次运行时 Windows 可能显示 SmartScreen 提示。
- Actions 构建产物需要从对应任务页面下载；语义版本 tag 的长期产物也可以直接从仓库的 Releases 页面下载。

## 不使用 Docker 部署后端

已有外部 MySQL 8 时，可以直接原生部署后端，不需要 Docker。

Ubuntu 脚本使用 Python 3.12 虚拟环境和 systemd，并自动配置 Nginx 与 Let's Encrypt：

```bash
sudo bash scripts/deploy-backend-ubuntu-native.sh \
  --domain fitness.example.com \
  --email admin@example.com
```

Windows 可以安装为受限 `LOCAL SERVICE` 账号运行的开机计划任务。本机个人使用：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\deploy-backend-windows.ps1 `
  -LocalOnly
```

Windows 公网模式改用 `-Domain fitness.example.com`，并让同机 Caddy、IIS 或已有网关提供 HTTPS。两个脚本都只监听 loopback、保留上一次发布、复用私有运行数据，并在最后显示首次数据库配置所需的 `setup_token`。数据库账号和密码只在 Web 向导中提交，数据库名固定为 `fitness`。完整步骤见[非 Docker 部署说明](docs/native-backend-deployment.md)。

仍希望使用容器时，原来的 Ubuntu Docker 自动部署入口继续保留：

```bash
sudo bash scripts/deploy-backend-ubuntu.sh \
  --domain fitness.example.com \
  --email admin@example.com
```

Docker 版本说明见 [Ubuntu Docker 部署说明](docs/ubuntu-backend-deployment.md)。

## 本地启动云同步服务

只有需要仓库内置 MySQL 时才必须使用 Docker。容器化本地启动脚本会生成不提交到 Git 的 `.env`，写入随机数据库密码和 JWT 密钥，并启用 `bundled-db` profile：

```powershell
.\scripts\bootstrap-dev.ps1
```

也可以手动复制 `.env.example`，填写必要配置后启动内置 MySQL：

```powershell
docker compose --env-file .env -f infra/docker-compose.yml --profile bundled-db up -d --build
```

如果已有 MySQL 8，也可以不配置数据库变量，先只启动后端：

```powershell
docker compose -f infra/docker-compose.yml up -d --build backend
docker compose -f infra/docker-compose.yml logs backend
```

然后打开 `http://127.0.0.1:8000/web/`。首次启动页面会要求填写 MySQL 地址、端口、账号、密码，以及日志中的一次性 `setup_token`。数据库名固定为 `fitness`：不存在时后端创建，已存在时读取版本和表信息，再自动运行 Alembic 与默认计划 seed。连接账号需要具备创建 `fitness`（若尚不存在）、升级表结构和读写业务表的权限。远程部署必须先配置 HTTPS，再在页面提交数据库凭据。

默认情况下：

- API 监听 `http://127.0.0.1:8000`。
- MySQL 只在 Docker 内部网络中开放，不映射到宿主机。
- 数据保存在 Docker volume `personal_fitness_planner_mysql_data` 中。
- 首次页面生成的数据库连接与 JWT 密钥保存在 `personal_fitness_planner_backend_config` 私有 volume 中。

部署完成后可打开 `https://<你的域名>/web/` 使用 Web 控制台。首次数据库配置完成后，页面会进入注册/登录；再按后端文档创建超级管理员。管理员可以在“系统设置”关闭公开注册，也可以在“账号管理”创建普通账号、停用账号或重置密码。数据库凭据只在首次设置时通过同源 HTTPS 提交，页面不会保存，Android/Windows 客户端也不会接触这些凭据。

这个 HTTP 地址适合浏览器、接口调试工具和同机运行的 Windows 客户端。Android 客户端只接受 HTTPS，即使是 Debug APK 或 `localhost` 也不会放行明文 HTTP。若要让 Android 连接自建后端，需要在 API 前配置带可信证书的 HTTPS 反向代理，并在应用中填写对应的 HTTPS 地址。

## 本地测试与构建

### 全部检查

在 Windows PowerShell 中运行：

```powershell
.\scripts\test-all.ps1
.\scripts\build-all.ps1
```

`build-all.ps1` 会构建 Android、Windows 和后端镜像，并将可用文件与 SHA-256 清单汇总到本地 `artifacts/`。完整前置条件和参数见[构建说明](docs/build-and-release.md)。

### GitHub Release

推送语义版本 tag（例如 `0.0.2` 或 `v0.0.2`）会运行独立的 Release workflow。检查全部通过后，对应 GitHub Release 会包含：

- 可安装的 Android Debug APK；
- Windows x64 自包含单文件 EXE；
- `SHA256SUMS.txt` 校验清单。

已有且位于默认分支历史中的 tag，可在 GitHub Actions 的 `release` workflow 中手动输入 tag 重新发布。Debug APK 使用 CI 临时调试签名，后续构建的证书可能不同；Android 若拒绝覆盖安装，需要先卸载旧 Debug APK。个人数据应先完成云端同步或备份。

### Android

需要 JDK 17 或 21、Android SDK Platform 36 和匹配的 Build Tools 36.x。

```powershell
cd apps\android
.\gradlew.bat test
.\gradlew.bat lint --max-workers=1
.\gradlew.bat assembleDebug
```

### Windows

需要 Windows 10/11 x64 和 .NET 10 SDK。

```powershell
dotnet restore apps\windows\PersonalFitnessPlanner.sln
dotnet test apps\windows\PersonalFitnessPlanner.sln -c Release
.\apps\windows\scripts\publish.ps1
```

### Backend

需要 Python 3.12。建议先在 `services/backend` 中创建虚拟环境并安装开发依赖：

```powershell
cd services\backend
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -e ".[dev]"
.\.venv\Scripts\python.exe -m pytest -m "not mysql"
```

## 自动检查

GitHub Actions 当前会执行：

- 共享契约校验和敏感信息扫描。
- Backend Ruff、Mypy、快速测试、MySQL 8 迁移与种子测试、OpenAPI 漂移检查。
- Backend 锁定依赖的 Python 漏洞审计，以及 Windows 直接/传递 NuGet 依赖的 high/critical 漏洞门禁。
- Android 单元测试、Lint 和 Debug APK 构建。
- Windows Restore、Release 构建、测试、自包含发布和启动检查。

本轮安全修复提交后的完整状态以 GitHub Actions 结果为准；不要仅凭本地部分测试判断 APK/EXE 已可发布。CI 配置位于 [`.github/workflows/ci.yml`](.github/workflows/ci.yml)，依赖更新检查位于 [`.github/dependabot.yml`](.github/dependabot.yml)。

## 跨端契约

三端共同使用以下文件，修改后需要同步校验：

- [`contracts/openapi.yaml`](contracts/openapi.yaml)：API 接口定义。
- [`contracts/default-training-plan.json`](contracts/default-training-plan.json)：内置训练计划。
- [`contracts/schema-version.json`](contracts/schema-version.json)：API 与数据结构版本。
- [`contracts/examples/`](contracts/examples/)：推荐和重量进阶规则的共享测试数据。

修改契约后运行：

```powershell
.\scripts\sync-contracts.ps1
.\scripts\validate-contracts.ps1
```

## 数据与隐私

- 客户端不会直接连接 MySQL，也不会保存数据库账号。
- 本地训练数据默认只保存在当前设备；启用云同步后才会上传到自己部署的后端。
- `.env`、Android `local.properties`、签名文件、数据库文件和构建产物均已加入 `.gitignore`。
- 不要把真实密码、JWT 密钥、Android 签名文件或个人训练数据库提交到 Git。
- 更换账号时，客户端会先检查未同步记录，避免把前一个账号的数据上传到新账号。

## 更多文档

- [源码与模块说明](docs/source-handoff.md)
- [安全说明](docs/security-review.md)
- [测试记录](docs/test-report.md)
- [端到端测试范围](docs/e2e-report.md)
- [构建与产物说明](docs/build-and-release.md)
