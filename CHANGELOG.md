# Changelog

## Unreleased - Security hardening

- Bound Windows DPAPI credentials to the normalized API origin and made legacy/cross-origin tokens fail closed; portable JSON imports can no longer replace API or local storage paths.
- Upgraded the Windows native SQLite bundle beyond the SQLite 3.50.2 security baseline and added transitive NuGet audit enforcement.
- Restricted private published-plan reads to system plans, owners, valid assignees, and current database administrators; new assignments re-emit the full plan tree before their reference.
- Enforced the backend request limit on actual streamed ASGI bytes and serialized version-checked workout/readiness/cardio mutations with database row locks.
- Kept `expected_version` optional for legacy-client compatibility while documenting that only supplied versions receive stale-copy rejection; real MySQL concurrency verification remains pending.
- Neutralized spreadsheet formulas in Android and Windows CSV exports, bounded Windows JSON imports, redacted Windows API error bodies, and added 14-day log retention.
- Switched the default backend image to one Gunicorn worker for consistent process-local login limiting, and added Dependabot plus Python/NuGet dependency gates.

## 1.0.0 - 2026-08-09

- 将 Android、Windows 和 FastAPI/MySQL 三套独立源码整合进统一仓库，同时保留各模块独立构建入口。
- 以后端运行时 OpenAPI 为 API 权威契约，建立根 `contracts/openapi.yaml` 与差异校验流程。
- 建立经过 JSON Schema 校验的默认 A/B 计划：2 个训练日、16 个位置、79 个选项、66 个动作、52 个器械身份。
- 统一默认计划代码、版本、UUID、适应期、休息时间、RIR 和首选/替代动作语义，并向三端分发同一字节快照。
- 增加推荐与双重渐进共享测试向量、根级同步/校验/测试/构建/打包脚本和 SHA-256 清单。
- 增加根 Docker Compose、MySQL 持久卷、安全的本地随机密钥 bootstrap、CI、源码交接、安全与端到端状态文档。
- 修复 retention cursor 断档：后端提供完整 workout/readiness/cardio bootstrap，两端识别 `full_resync_required` 并在保护 pending Outbox 的前提下重建服务器镜像。
- 修复旧随机 UUID seed 升级、普通用户默认计划分配、草稿同步泄露、workout 计划引用越权/错树及客户端 token origin/refresh 边界。
- Android/Windows 运行时消费 canonical 推荐规则与共享 recommendation/progression 向量；Windows SQLite 升至 v8 并补齐主要云端实体同步字段。
- 清除统一源码树中的 APK、EXE、`build/bin/obj`、Gradle/NuGet/Python 缓存和本机配置；旧产物仍保留在原始交接目录。
