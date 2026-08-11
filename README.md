# Personal Fitness Planner Cloud

这是 Android、Windows 和 FastAPI/MySQL 三端统一维护仓库。三套原始交付源码已完整迁入可独立构建的模块；APK、EXE、`build/bin/obj`、Gradle/NuGet/Python 缓存和本机配置没有混入统一源码树。

```text
PersonalFitnessPlannerCloud/
├─ apps/android/              Kotlin + Compose + Room
├─ apps/windows/              .NET 8 + WPF + SQLite
├─ services/backend/          FastAPI + SQLAlchemy + Alembic + MySQL 8
├─ contracts/                 OpenAPI、默认计划、版本和共享测试向量
├─ infra/                     Docker Compose 与 MySQL 约定
├─ scripts/                   统一校验、测试、构建和打包
├─ docs/                      交接、安全、测试与 E2E 状态
└─ artifacts/                 后续统一构建产物（默认不提交）
```

## 权威契约

- API：`contracts/openapi.yaml`，由 FastAPI 运行时导出。
- Schema/API 版本：`contracts/schema-version.json`。
- 默认计划：`contracts/default-training-plan.json`，由 `default-training-plan.schema.json` 校验。
- 推荐与进阶规则：`contracts/examples/*.json`。

默认计划固定为 `beginner_recomp_ab_v1` / v1：A、B 各 8 个位置，共 79 个位置选项、66 个全局动作 UUID 和 52 个器械 UUID。三端随包计划文件必须与根文件逐字一致。

修改契约后运行：

```powershell
.\scripts\sync-contracts.ps1
.\scripts\validate-contracts.ps1
```

若系统没有可直接调用的 `python`，向脚本显式传入 Python 3.12 路径，例如
`.\scripts\validate-contracts.ps1 -Python C:\path\to\python.exe`。

## 本地后端

要求 Docker Engine/Compose 和 Python 3.12。首次启动脚本会生成不提交的根 `.env`，使用随机本地密码和 JWT 密钥：

```powershell
.\scripts\bootstrap-dev.ps1
```

等价的 Compose 入口：

```powershell
docker compose --env-file .env -f infra/docker-compose.yml up -d --build
```

后端默认监听 `127.0.0.1:8000`，MySQL 3306 不发布到宿主机；持久卷名为 `personal_fitness_planner_mysql_data`。生产环境必须在受信任的 TLS 终止代理后部署 API。

## 独立测试与构建

后端：

```powershell
cd services/backend
python -m pytest
python -m scripts.export_openapi
```

Android：

```powershell
cd apps/android
.\gradlew.bat test
.\gradlew.bat lint --max-workers=1
.\gradlew.bat assembleDebug
.\gradlew.bat assembleRelease
```

Windows：

```powershell
dotnet restore apps/windows/PersonalFitnessPlanner.sln
dotnet build apps/windows/PersonalFitnessPlanner.sln -c Release
dotnet test apps/windows/PersonalFitnessPlanner.sln -c Release
.\apps\windows\scripts\publish.ps1
```

统一入口：

```powershell
.\scripts\test-all.ps1
.\scripts\build-all.ps1
```

`build-all.ps1` 会顺序执行门禁并调用 `package-release.ps1`，最终把 APK、EXE、后端部署文件、契约和 SHA-256 清单放入 `artifacts/`。

## 当前交付阶段

本轮按项目安排完成源码、契约与同步边界整合。根契约/安全/脚本门禁、backend 55 项快速测试、Windows 74 项 xUnit 与 WPF 编译均已通过；Android 本轮没有形成完整 Gradle 测试结果。新的统一 APK、EXE、Docker 镜像、真实 MySQL 测试和三端 E2E 留到后续统一构建阶段；现有历史产物仍位于原始 `01_Android_APK` / `02_Windows_EXE` 交接目录，不冒充本仓库的新构建结果。

详细状态：

- `docs/source-handoff.md`
- `docs/security-review.md`
- `docs/test-report.md`
- `docs/e2e-report.md`
- `docs/build-and-release.md`
- `AGENTS.md`
