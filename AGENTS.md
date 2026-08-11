# PersonalFitnessPlannerCloud 维护规范

## 仓库结构与职责

- `apps/android/`：Kotlin、Jetpack Compose、Room 客户端。负责移动训练、离线记录、Outbox 和恢复联网同步。
- `apps/windows/`：.NET 8、WPF、SQLite 客户端。负责桌面训练、计划编辑/发布/分配和管理操作。
- `services/backend/`：FastAPI、SQLAlchemy、Alembic、MySQL 8。负责认证、RBAC、云端权威目录/计划、训练、同步、审计和 OpenAPI。
- `contracts/`：跨端唯一权威契约。默认计划只能从这里修改。
- `infra/`：本地/部署基础设施。MySQL 不得发布宿主端口。
- `scripts/`：根级契约同步、测试、构建、打包和校验入口。

三个应用必须继续保持独立可构建。不要把客户端领域模型、Room/SQLite 模型和 OpenAPI 生成 wire DTO 混为一层。

## 不可破坏的 API

最低普通 API：

```text
POST   /api/v1/auth/login
POST   /api/v1/auth/refresh
POST   /api/v1/auth/logout
GET    /api/v1/me
GET    /api/v1/bootstrap
GET    /api/v1/plans/current
GET    /api/v1/plans/{plan_version_id}
GET    /api/v1/exercises
GET    /api/v1/equipment
GET    /api/v1/workout-sessions
POST   /api/v1/workout-sessions
PATCH  /api/v1/workout-sessions/{id}
POST   /api/v1/readiness
GET    /api/v1/sync/changes
POST   /api/v1/sync/batch
```

最低管理 API：动作/器械 POST+PATCH、逻辑计划 POST、新版本 POST、草稿版本 PATCH、发布、分配、审计日志和同步状态。路径以 `contracts/openapi.yaml` 为准。破坏性变更必须新建 API 版本并先提供客户端迁移期。

## 数据权威、ID、时间与版本

- 云端是动作、器械、训练计划、已发布版本和用户分配的权威来源；客户端本地草稿和未上传训练除外。
- 云端业务 ID 使用 UUID。客户端若有本地主键，必须另存服务器 UUID；不得用固定本地用户 UUID覆盖服务器用户 UUID。
- 服务端按 UTC 存储，API 时间使用带时区 ISO 8601；训练归属日另存 `local_date`；用户时区用 IANA 名称。
- 可同步实体必须保留 `id/version/created_at/updated_at/deleted_at`，删除走软删除。
- 已发布计划和完整子树不可原地修改。修改计划时创建草稿新版本，校验、发布、再分配。
- 历史训练必须保留训练开始时的 `plan_version_id`、计划快照、动作快照和处方快照；新分配不得重写旧训练。

## 同步不可变量

- 客户端本地写入与 Outbox 入队必须在同一事务。
- 每次操作和批次使用稳定幂等键；重试不得换键；服务端只对明确 accepted/duplicate 的操作确认完成。
- 增量同步使用服务端单调游标。游标只能在本地完整应用一页后推进；含 `full_resync_required` 的断档页不得应用、不得推进其 cursor，必须改走 bootstrap。
- full resync 用 bootstrap 替换服务器权威缓存并清理服务端已不存在的缓存项，同时保护 pending Outbox、本地草稿和未上传训练。
- 计划冲突服务器优先；本地未上传训练不得被 bootstrap 覆盖。
- 401 只刷新一次；刷新失败或重试仍 401 时清令牌并回登录态。
- 普通用户不得通过 change feed 看到草稿、他人私有计划或他人健康数据。
- 本地游标、用户缓存和健康数据必须绑定认证主体；切换账号时不得展示旧主体缓存，也不得把旧主体 pending Outbox 上传到新主体。存在待同步数据时应阻断切换或使用明确的分区存储。
- workout 的 assignment/version/day/slot 引用必须属于当前用户且来自同一计划树。

## 修改默认训练计划

1. 只编辑 `contracts/default-training-plan.json` 和必要的 Schema/版本说明。
2. 运行 `scripts/sync-contracts.ps1`；不要直接编辑三端随包快照。
3. 运行 `scripts/validate-contracts.ps1`，确认 A/B 各 8 个位置、唯一首选、UUID 和共享计数。
4. 计划内容变化必须提升版本，创建后端新发布版本；绝不覆盖数据库中已发布版本。
5. 推荐/渐进规则变化同步更新 `contracts/examples/` 并让三端测试消费同一向量。

## OpenAPI 与生成客户端

- FastAPI Pydantic 模型和显式响应是运行时来源；固定快照为 `contracts/openapi.yaml`。
- 修改 API 后运行 `scripts/generate-clients.ps1`，审阅 OpenAPI diff，再生成客户端。
- 生成文件只放 `contracts/generated/` 或模块明确的 generated 目录，禁止直接手改。
- 手写适配层负责 wire DTO 与 Room/SQLite/领域模型之间的转换。

## 数据库迁移

- MySQL：新增 Alembic revision；必须验证空库 upgrade、现有数据 upgrade、seed 重入和 downgrade 可行性（不可安全 downgrade 时明确阻断）。
- Android：Room schema 版本递增，提交导出 schema 和逐版本 Migration 测试；禁止 destructive migration。
- Windows：SQLite schema 版本递增，升级前备份并写旧版本 fixture 测试。
- 不直接修改生产数据库，不把 SQL 放进 UI，不重写历史训练。

## 密钥与隐私

- 不提交 `.env`、JWT/MySQL 密码、管理员密码、签名密钥、真实令牌、数据库/备份或用户导出。
- Android 令牌使用 Keystore；Windows 使用 DPAPI；服务端只存 Refresh Token 摘要。
- 不信任所有证书，不记录 Authorization、密码、令牌或完整敏感 API 错误体。
- 生产配置遇到占位符/弱密钥必须 fail closed；MySQL 不得公网暴露。
- 导出训练/健康数据必须由用户明确触发并显示隐私提示。

## 修改后必须执行

最低门禁：

```powershell
.\scripts\validate-contracts.ps1
.\scripts\test-all.ps1
```

按改动范围追加：

- 后端：`pytest`；涉及数据库时跑真实 MySQL marker、Alembic 空库升级和幂等 seed；API 改动导出 OpenAPI。
- Android：`gradlew test`、`gradlew lint --max-workers=1`；Room 改动跑 migration 测试。
- Windows：`dotnet test -c Release`；SQLite 改动跑旧 schema migration 测试。
- 发布前：`scripts/build-all.ps1`，随后核对 `artifacts/checksums/SHA256SUMS.txt`。

不得以 mock 成功替代真实构建/E2E；无法执行真机、Windows EXE 或 MySQL 验证时必须在交付报告中列为“未验证”。
