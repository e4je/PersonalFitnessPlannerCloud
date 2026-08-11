# Personal Fitness Planner Cloud Backend

这是 Android APK 与 Windows EXE 共用的 FastAPI + MySQL 8 云端后端。它提供 JWT 认证、RBAC、动作与器械目录、不可变计划版本、计划分配、训练/准备度/有氧记录、离线幂等同步和审计。

## 已实现范围

- Python 3.12、FastAPI、Pydantic v2、SQLAlchemy 2、Alembic、MySQL 8。
- 25 张表：规范要求的 23 张表，以及动作-肌群、动作-器械两个必要关联表。
- Argon2 密码哈希；短时 Access Token；仅存摘要、可轮换/撤销且具重放检测的 Refresh Token。
- 数据库实时 RBAC（不会仅信任 JWT 内角色）；管理操作与同步冲突审计。
- 草稿计划校验、发布、完整计划树发布后不可变、新版本与用户分配。
- 客户端 UUID、乐观锁、软删除、历史计划/动作/处方快照、重复训练组防护。
- `Idempotency-Key` 首次响应回放；同键异载荷返回 409。
- 增量同步游标、保留窗口、过旧游标 `full_resync_required`、Android/Windows 批次兼容。普通用户仅同步本人记录/分配、全局目录，以及系统、本人拥有或有效分配的已发布计划版本；逻辑计划和草稿仅管理员可见。
- 结构化 JSON 日志、CORS 白名单、请求体大小限制、登录限速、无配置泄露的健康检查。
- 完整 OpenAPI、首个迁移、默认 A/B 计划 seed、Docker Compose、测试与 smoke 脚本。

## 目录

```text
backend/
├─ app/{api,core,db,models,repositories,schemas,seed,services,sync}
├─ alembic/versions/20260809_0001_initial_schema.py
├─ contracts/openapi.yaml
├─ scripts/{seed_default_plan,create_admin,create_user,export_openapi,smoke_test}.py
├─ tests/
├─ Dockerfile
├─ docker-compose.yml
├─ pyproject.toml
├─ requirements.lock
└─ .env.example
```

## 快速启动（推荐）

要求：Docker Engine 与 Compose 插件可用。MySQL 不需要在 Windows 安装成服务。

```bash
cp .env.example .env
# 编辑 .env，至少更换 MYSQL_PASSWORD、MYSQL_ROOT_PASSWORD、JWT_SECRET。
docker compose up -d --build
docker compose ps
python -m scripts.smoke_test
```

启动顺序由容器入口自动执行：`alembic upgrade head` → 幂等 seed → Gunicorn/Uvicorn。MySQL 仅在 Compose 内部网络暴露 3306；后端默认只绑定宿主机 `127.0.0.1:8000`。管理员需在服务健康后按下文执行一次性创建命令。

## 本地 Python 开发

```powershell
cd PersonalFitnessPlannerCloud\services\backend
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -e ".[dev]"
# 推荐先在仓库根运行 scripts\bootstrap-dev.ps1 -NoStart，它会生成根 .env。
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

当前没有公开注册接口。普通用户通过同样受控且幂等的命令预置；重复执行默认不会更改已有密码，只有显式增加 `--update-password` 才轮换：

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

Android 兼容约定：登录使用 JSON；所有写入使用 `Idempotency-Key`；分页统一返回 `items/cursor/next_cursor/has_more`；计划为 `days → slots → options`；时间为 ISO 8601；训练状态在 wire 层为大写。

## 数据与同步语义

- 业务对象 ID 为 UUID 字符串；数据库连接会固定为 UTC；另存 `local_date` 和用户 IANA 时区。
- 动作、器械、计划由服务器权威管理。已发布计划及其日/位置/选项不可修改；修改必须创建新版本。
- 新分配仅影响后续新训练；历史训练保留计划、动作和处方快照。
- PATCH 可用 `expected_version`（DELETE 也支持 `If-Match`）；冲突返回 HTTP 409、`server_copy` 并写审计。
- 同步游标是服务端单调序列。游标早于保留窗口时返回 `full_resync_required=true`；该断档页不得应用或推进 cursor，客户端应重新调用 bootstrap。
- bootstrap 返回本人全部未软删 workout/readiness/cardio、assignments、active catalog、当前/相关 assignment 计划版本、user/permissions/recommendation 和一致性 cursor。新版客户端以此替换服务器权威缓存并保护 pending Outbox。

## 测试与验收

快速测试使用隔离的临时 SQLite，真实数据库测试必须显式提供仅供测试的 MySQL URL：

```bash
pytest
TEST_DATABASE_URL='mysql+pymysql://fitness:password@mysql:3306/fitness_test' pytest -m mysql
alembic upgrade head
python -m scripts.seed_default_plan
python -m scripts.export_openapi
docker compose up -d --build
python -m scripts.smoke_test
```

测试保护会拒绝明显的生产数据库名称；MySQL 集成用例未设置 `TEST_DATABASE_URL` 时会明确 skip，而不是静默改用 SQLite。

## 备份与恢复

先创建只允许运维账号访问的 `backups/`。备份使用一致性事务：

```bash
docker compose --profile tools run --rm -T backup > backups/fitness-$(date +%F-%H%M%S).sql
```

恢复会覆盖/合并目标库中的对象，必须先核对目标环境并保留旧备份：

```bash
docker compose exec -T mysql sh -c 'exec mysql -uroot -p"$MYSQL_ROOT_PASSWORD" "$MYSQL_DATABASE"' \
  < backups/fitness-YYYY-MM-DD-HHMMSS.sql
alembic current
```

生产环境建议由云数据库快照与加密对象存储承担主备份，本命令用于受控运维和恢复演练。

## 生产部署检查

- 设置 `ENVIRONMENT=production`、强随机 `JWT_SECRET`、真实 CORS 白名单与 TLS 终止代理。
- 不发布 MySQL 端口；数据库账号采用最小权限；密钥由 Secret Manager 注入，不写 `.env` 或镜像。
- 先备份，再运行 Alembic；滚动发布前验证 `/health/ready` 与 OpenAPI 兼容性。
- 在网关补充全局登录限速、WAF、请求大小和 TLS 策略；应用内限速是单进程防护。
- 聚合 JSON 日志并为 `SYNC_CONFLICT`、refresh token 重放、管理发布操作配置告警。

更完整的部署步骤见 `docs/DEPLOYMENT.md`。

## 已知限制

- full bootstrap 当前直接返回全部 active 个人历史且未分页；超长历史应演进为一致性快照分页，避免大响应体和内存峰值。
- 部分统一错误响应、admin/recommendation OpenAPI schema 和原子乐观锁仍需继续收紧，并在真实 MySQL 并发测试验证。

- 登录限速存于进程内；多 worker/多实例的全局限速需由 API 网关或 Redis 补充。
- 发布不可变由服务层和 SQLAlchemy flush 防护实现；绕过应用直接执行 SQL 的高权限账号仍可改库。
- 当前没有公开注册、密码找回或邮件验证接口；用户生命周期应接入后续身份管理流程。
- 增量 change feed 已按保留窗口判断失效，但历史清理需要由部署方配置周期任务。
