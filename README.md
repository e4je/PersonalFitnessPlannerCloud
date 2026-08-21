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

## 项目组成

```text
PersonalFitnessPlannerCloud/
├─ apps/android/       Android 客户端（Kotlin、Jetpack Compose、Room）
├─ apps/windows/       Windows 客户端（.NET 8、WPF、SQLite）
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

tag 会触发同一套 CI，但不会自动创建 GitHub Release，也不会自动修改应用内部版本号。

### 安装提示

- Android 产物是 Debug APK，适合个人安装。如果手机上已经安装了签名不同的同包名应用，需要先卸载旧版本。
- Windows EXE 是 `win-x64` 自包含程序，不需要预装 .NET Runtime；因为没有代码签名，首次运行时 Windows 可能显示 SmartScreen 提示。
- Actions 构建产物来自私有仓库，只有有权限的 GitHub 账号可以下载。

## 本地启动云同步服务

需要 Docker Engine 和 Docker Compose。首次启动会在本地生成不提交到 Git 的 `.env`，其中包含随机数据库密码和 JWT 密钥：

```powershell
.\scripts\bootstrap-dev.ps1
```

也可以手动复制 `.env.example`，填写必要配置后启动：

```powershell
docker compose --env-file .env -f infra/docker-compose.yml up -d --build
```

默认情况下：

- API 监听 `http://127.0.0.1:8000`。
- MySQL 只在 Docker 内部网络中开放，不映射到宿主机。
- 数据保存在 Docker volume `personal_fitness_planner_mysql_data` 中。

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

需要 JDK 17 或 21、Android SDK Platform 35 和 Build Tools 35.0.0。

```powershell
cd apps\android
.\gradlew.bat test
.\gradlew.bat lint --max-workers=1
.\gradlew.bat assembleDebug
```

### Windows

需要 Windows 10/11 x64 和 .NET 8 SDK。

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
