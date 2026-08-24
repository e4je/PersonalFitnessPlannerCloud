# Changelog

## Unreleased - Unified repository integration

- Made local SQLite the zero-configuration deployment default, added persistent automatic JWT key generation, native pre-start migrations/seed, and integrity-checked live backups while retaining explicit MySQL compatibility.
- Added an Ubuntu single-server installer with scoped forwarded-header trust, automatic Nginx/Let's Encrypt configuration, and first-run setup preservation.
- Canonical JSON seed now uses stable cross-client UUIDs, preserves legacy UUID4 history, and creates the canonical identity instead of falsely reporting it as already seeded.
- Tightened plan change visibility and workout assignment/tree authorization; removed reusable secrets and bound runtime versions to the vendored contract.
- Added idempotent ordinary-user provisioning with a default canonical assignment.
- Added complete active workout/readiness/cardio bootstrap recovery, typed bootstrap schemas, shared recommendation/progression vectors, and retention-gap client semantics.
- Enforced streamed request-size limits, database-current private-plan authorization, assignment-time plan snapshot replay, and row-locked optimistic-version checks for mutable health entities.
- Kept `expected_version` optional during client compatibility while documenting the weaker stale-copy protection when omitted; changed the default image to one Gunicorn worker so process-local login limiting is consistent in the standard single-container deployment.
- Added locked Python dependency auditing in CI and weekly Dependabot checks; real MySQL concurrency and cross-client E2E remain release-gate work.

## 0.1.0 - 2026-08-09

- 初始 FastAPI + MySQL 8 后端。
- 新增 25 表模型与 Alembic revision `20260809_0001`。
- 新增 Argon2、JWT access/refresh 轮换撤销、RBAC 与登录限速。
- 新增目录、计划草稿/发布/分配、不可变保护与审计。
- 新增训练、readiness、有氧、幂等、乐观锁、软删除和增量同步。
- 新增精确默认 A/B 计划 seed（66 动作、79 选项）。
- 新增 Docker Compose、健康检查、OpenAPI、测试、smoke、备份恢复和部署文档。
- 容器运维脚本统一按 Python 模块执行，避免入口脚本路径影响应用包导入。
- 修复计划分配同步的跨用户可见性，并增加 UPSERT/DELETE 多用户隔离测试。
- Compose 改用离散 MySQL 参数安全处理特殊字符密码，补齐运行参数且不再常驻管理员凭据。
- 生产 HTTPS 强制保留内部健康探针；备份采用最小权限兼容的 `--no-tablespaces`。
