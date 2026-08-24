# Personal Fitness Planner Cloud Backend

这是 Android APK 与 Windows EXE 共用的 FastAPI 云端后端。新部署默认使用本机 SQLite 文件；MySQL 8 作为可选兼容模式保留。后端提供 JWT 认证、RBAC、动作与器械目录、不可变计划版本、计划分配、训练/准备度/有氧记录、离线幂等同步和审计。

## 已实现范围

- Python 3.12、FastAPI、Pydantic v2、SQLAlchemy 2、Alembic、SQLite；可选 MySQL 8。
- 25 张表：规范要求的 23 张表，以及动作-肌群、动作-器械两个必要关联表。
- Argon2 密码哈希；短时 Access Token；仅存摘要、可轮换/撤销且具重放检测的 Refresh Token。
- 数据库实时 RBAC（不会仅信任 JWT 内角色）；管理操作与同步冲突审计。
- 草稿计划校验、发布、完整计划树发布后不可变、新版本与用户分配。
- 客户端 UUID、乐观锁、软删除、历史计划/动作/处方快照、重复训练组防护。
- `Idempotency-Key` 首次响应回放；同键异载荷返回 409。
- 增量同步游标、保留窗口、过旧游标 `full_resync_required`、Android/Windows 批次兼容。普通用户仅同步本人记录/分配、全局目录，以及系统、本人拥有或有效分配的已发布计划版本；逻辑计划和草稿仅管理员可见。
- 结构化 JSON 日志、CORS 白名单、按实际 ASGI 字节执行的请求体大小限制、登录限速、无配置泄露的健康检查。
- 同源 Web 控制台（`/web/`）：普通用户注册/登录和云端概览；管理员账号、角色、停用/密码重置、用户训练概览、注册开关及计划草稿/发布/分配。
- 零数据库配置即可启动：自动创建私有 `fitness.db`、持久化随机 JWT 密钥；各部署入口自动迁移并 seed。
- 完整 OpenAPI、首个迁移、默认 A/B 计划 seed、Docker Compose、测试与 smoke 脚本。

## 目录

```text
backend/
├─ app/{api,core,db,models,repositories,schemas,seed,services,sync}
├─ alembic/versions/20260809_0001_initial_schema.py
├─ contracts/openapi.yaml
├─ scripts/{seed_default_plan,create_admin,create_user,backup_sqlite,export_openapi,smoke_test}.py
├─ tests/
├─ Dockerfile
├─ docker-compose.yml
├─ pyproject.toml
├─ requirements.lock
└─ .env.example
```

## 快速启动

后端既可以原生运行，也可以使用 Docker，不需要预装数据库。Ubuntu 单服务器推荐直接从仓库根运行非 Docker 脚本：

```bash
sudo bash scripts/deploy-backend-ubuntu-native.sh \
  --domain fitness.example.com \
  --email admin@example.com
```

Windows 本机使用可以在管理员 PowerShell 中安装为开机任务：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\deploy-backend-windows.ps1 `
  -LocalOnly `
  -Port 18000
```

Windows 公网模式使用 `-Domain fitness.example.com`，并通过 Caddy、IIS 或现有 HTTPS 网关代理到 loopback。完整说明见仓库根的 `docs/native-backend-deployment.md`。

Ubuntu Docker 生产部署入口仍然可用：

```bash
sudo bash scripts/deploy-backend-ubuntu.sh \
  --domain fitness.example.com \
  --email admin@example.com
```

脚本会安装缺失的 Docker 依赖、启动单个 backend，并配置 Nginx/Let's Encrypt。详细说明见仓库根的 `docs/ubuntu-backend-deployment.md`。

### 默认 SQLite

只启动 backend 即可：

```bash
docker compose up -d --build backend
docker compose logs backend
```

容器入口会依次执行 `alembic upgrade head`、幂等 seed，再启动 API。`fitness.db` 与自动生成的 `jwt-secret` 都位于 `/app-data`，官方 Compose 将其保存在 `backend_config` 私有 volume。打开 `http://127.0.0.1:8000/web/` 后会直接进入注册/登录。

### 可选 MySQL 8

旧部署和明确需要 MySQL 的环境可以继续使用。设置 `DATABASE_BACKEND=mysql`，填写强密码并显式启用 `bundled-db` profile：

```bash
cp .env.example .env
# 编辑 .env：DATABASE_BACKEND=mysql，并填写 MYSQL_PASSWORD、
# MYSQL_ROOT_PASSWORD、JWT_SECRET。
docker compose --profile bundled-db up -d --build
docker compose ps
python -m scripts.smoke_test
```

也可提供完整 `DATABASE_URL`，或在 `DATABASE_BACKEND=mysql` 且未填写凭据时使用原有的一次性 Web 向导。旧版 `backend-config.json` 会优先恢复，升级不会静默切换到空 SQLite。MySQL profile 不映射 3306 到宿主机。

## 本地 Python 开发

```powershell
cd PersonalFitnessPlannerCloud\services\backend
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -e ".[dev]"
# 可先在仓库根运行 scripts\bootstrap-dev.ps1 -NoStart 生成根 .env。
.\.venv\Scripts\alembic.exe upgrade head
.\.venv\Scripts\python.exe -m scripts.seed_default_plan
.\.venv\Scripts\uvicorn.exe app.main:app --reload
```

上述 `python` 必须是 Python 3.12；也可显式传入其绝对路径。Windows 本地开发使用 Uvicorn；Gunicorn 只在 Linux 容器运行。

## 管理员与 seed

默认计划 seed 幂等，可重复执行：

```bash
python -m scripts.seed_default_plan
```

规范数据量为：2 个训练日、16 个训练位置、79 个位置动作选项、66 个唯一动作、52 条完整器械需求。计划规则包括每周 3 次、至少休息 1 天、A/B 交替、前两周最多 2 个正式组、第三周执行完整组数、目标 RIR 2～3。

容器健康后显式创建一次管理员。密码只注入本次 `exec` 进程，不保存在长期运行的 backend 容器配置中，也不会输出：

```bash
read -r -p "Admin email: " ADMIN_EMAIL
read -r -s -p "Admin password: " ADMIN_PASSWORD && echo
export ADMIN_EMAIL ADMIN_PASSWORD
docker compose exec -e ADMIN_EMAIL -e ADMIN_PASSWORD backend \
  python -m scripts.create_admin
unset ADMIN_PASSWORD
```

已有用户会被幂等提升；只有在命令末尾显式传入 `--update-password` 才更新已有密码。创建后不要把 `ADMIN_PASSWORD` 写入 `.env`。

也可以通过 Web 控制台注册普通账号。管理员可在“系统设置”关闭公开注册；关闭后仍可使用下方受控命令预置普通用户。命令重复执行默认不会更改已有密码，只有显式增加 `--update-password` 才轮换：

```bash
read -r -p "User email: " USER_EMAIL
read -r -s -p "User password: " USER_PASSWORD && echo
export USER_EMAIL USER_PASSWORD
docker compose exec -e USER_EMAIL -e USER_PASSWORD backend \
  python -m scripts.create_user
unset USER_PASSWORD
```

命令要求 canonical seed 已完成。新用户或没有 active/scheduled assignment 的既有用户会自动获得 canonical published 默认计划；已有有效 assignment 不会被替换，重复执行不会创建重复 assignment。

## API

认证与初始化：

```text
GET  /api/v1/setup/status     POST /api/v1/setup/database
POST /api/v1/auth/login       POST /api/v1/auth/refresh
POST /api/v1/auth/logout      GET  /api/v1/me
GET  /api/v1/bootstrap        GET  /api/v1/recommendation/today
```

计划、目录、训练与同步：

```text
GET /api/v1/plans/current             GET    /api/v1/plans/{plan_version_id}
GET /api/v1/exercises                 GET    /api/v1/equipment
GET /api/v1/workout-sessions          GET    /api/v1/workout-sessions/{id}
POST/PATCH/DELETE /api/v1/workout-sessions[/{id}]
POST/GET /api/v1/readiness            POST/GET /api/v1/cardio-sessions
GET /api/v1/sync/changes              POST   /api/v1/sync/batch
```

管理接口位于 `/api/v1/admin`，覆盖动作、器械、逻辑计划、版本、发布、分配、审计日志和同步状态。交互文档位于 `/docs`，固定契约为 `contracts/openapi.yaml`。

### Web 控制台

部署后打开 `https://<你的域名>/web/`。未配置时页面只把数据库凭据提交给同源 Setup API，提交结束立即清空，不写浏览器存储；正常登录后只在 `sessionStorage` 保存本次会话的 Bearer/Refresh Token。所有管理员操作仍由服务端实时 RBAC 校验。数据库初始化后建议先用 `scripts.create_admin` 创建超级管理员，再登录 Web 控制台维护其他账号。

新增接口包括：

```text
GET/POST /api/v1/auth/registration-status|register
GET/PATCH /api/v1/admin/settings/registration
GET/POST /api/v1/admin/users
PATCH /api/v1/admin/users/{user_id}
GET /api/v1/admin/users/{user_id}/overview
GET /api/v1/admin/plans
GET /api/v1/admin/plan-versions/{version_id}
```

管理员修改计划必须遵循“创建草稿 → 保存/校验 → 发布 → 分配”；已发布版本不可原地修改。

Android 兼容约定：登录使用 JSON；所有写入使用 `Idempotency-Key`；分页统一返回 `items/cursor/next_cursor/has_more`；计划为 `days → slots → options`；时间为 ISO 8601；训练状态在 wire 层为大写。

## 数据与同步语义

- 业务对象 ID 为 UUID 字符串；数据库连接会固定为 UTC；另存 `local_date` 和用户 IANA 时区。
- 动作、器械、计划由服务器权威管理。已发布计划及其日/位置/选项不可修改；修改必须创建新版本。
- 新分配仅影响后续新训练；历史训练保留计划、动作和处方快照。
- PATCH 可用 `expected_version`（DELETE 也支持 `If-Match`）；兼容期允许旧客户端省略。提供版本时，服务器会在数据库行锁内原子校验，冲突返回 HTTP 409、`server_copy` 并写审计；新版客户端应始终发送最近一次服务器副本的版本。省略版本的写入仍会被行锁串行化，但不具备拒绝旧副本覆盖的完整乐观并发语义。
- 同步游标是服务端单调序列。游标早于保留窗口时返回 `full_resync_required=true`；该断档页不得应用或推进 cursor，客户端应重新调用 bootstrap。
- bootstrap 返回本人全部未软删 workout/readiness/cardio、assignments、active catalog、当前/相关 assignment 计划版本、user/permissions/recommendation 和一致性 cursor。新版客户端以此替换服务器权威缓存并保护 pending Outbox。
- 请求体上限按实际 ASGI 流累计字节，而不只依赖 `Content-Length`；chunked/HTTP 2 请求和未读取正文的端点同样会在超限时返回 413。生产网关仍应设置独立的外层限制。

## 测试与验收

快速测试使用隔离的临时 SQLite，真实数据库测试必须显式提供仅供测试的 MySQL URL：

```bash
pytest
pip-audit --strict --require-hashes --disable-pip -r requirements.lock
TEST_DATABASE_URL='mysql+pymysql://fitness:password@mysql:3306/fitness_test' pytest -m mysql
alembic upgrade head
python -m scripts.seed_default_plan
python -m scripts.export_openapi
docker compose --profile bundled-db up -d --build
python -m scripts.smoke_test
```

测试保护会拒绝明显的生产数据库名称；MySQL 集成用例未设置 `TEST_DATABASE_URL` 时会明确 skip，而不是静默改用 SQLite。

## 备份与恢复

SQLite 可在后端运行时生成一致性备份：

```powershell
.\.venv\Scripts\python.exe -m scripts.backup_sqlite --output "D:\backup\fitness.db"
```

不指定 `--output` 时，脚本会在实时数据库旁的 `backups/` 创建带 UTC 时间戳的副本，并对副本执行 `PRAGMA integrity_check`。目标文件已存在时脚本拒绝覆盖。

恢复前必须停止后端并保留当前文件，然后替换 `fitness.db`；同时迁移 `jwt-secret` 才能让现有登录保持有效。启动后检查：

```bash
alembic current
curl -fsS http://127.0.0.1:8000/health/ready
```

备份文件包含账号和个人训练数据，应加密保存且不要提交到 Git。可选 MySQL 模式仍使用数据库平台自己的事务备份与恢复工具。

## 生产部署检查

- 设置 `ENVIRONMENT=production`、真实 CORS 白名单与可信 TLS 终止代理。SQLite 模式自动生成强随机 JWT 密钥，也可由 Secret Manager 显式注入。
- 保护 `fitness.db`、`jwt-secret` 和 `backend_config` volume，不得打包进镜像、提交到仓库或备份到公开位置。
- 只发布 HTTPS Web 入口。SQLite 不使用网络端口；可选 MySQL 的 3306 也不得向公网开放。
- 先备份，再运行 Alembic；滚动发布前验证 `/health/ready` 与 OpenAPI 兼容性。
- 在网关补充全局登录限速、WAF、请求大小和 TLS 策略；默认容器虽为单 worker，应用内限速仍只是当前进程的防护。
- 聚合 JSON 日志并为 `SYNC_CONFLICT`、refresh token 重放、管理发布操作配置告警。

更完整的部署步骤见 `docs/DEPLOYMENT.md`。

## 已知限制

- full bootstrap 当前直接返回全部 active 个人历史且未分页；超长历史应演进为一致性快照分页，避免大响应体和内存峰值。
- 部分统一错误响应和 admin/recommendation OpenAPI schema 仍需继续收紧；健康记录已用行锁串行化，但兼容期可省略 `expected_version`，真实 MySQL 并发行为仍需专项验证。
- 登录限速存于进程内；官方镜像默认一个 worker，多容器/多实例的全局限速仍需由 API 网关或 Redis 补充。
- 发布不可变由服务层和 SQLAlchemy flush 防护实现；绕过应用直接执行 SQL 的高权限账号仍可改库。
- 已提供可由管理员开关控制的公开注册；当前仍没有密码找回或邮件验证接口，个人部署应通过 Web 管理员页面或受控运维命令完成账号生命周期管理。
- 增量 change feed 已按保留窗口判断失效，但历史清理需要由部署方配置周期任务。
