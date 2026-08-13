# 三端源码交接盘点

盘点日期：2026-08-11（Asia/Shanghai）。原始 `01_Android_APK`、`02_Windows_EXE`、`03_MySQL_Backend` 未移动、未删除；统一副本只包含源码、契约、迁移、测试、脚本和文档。

## 输入与统一位置

| 模块 | 原始源码根 | 统一源码根 | 清理策略 |
|---|---|---|---|
| Android | `01_Android_APK/android` | `apps/android` | 排除 APK/AAB、截图、`build/.gradle/.kotlin`、`local.properties` |
| Windows | `02_Windows_EXE/windows` | `apps/windows` | 排除 EXE、`.packages`、`bin/obj/TestResults/.vs` |
| Backend | `03_MySQL_Backend/backend` | `services/backend` | 排除 `.venv/__pycache__/.pytest_cache/backups` |

总控和三个原始 Agent 说明归档在 `docs/handoff-specs/`。最终清洁扫描为 0 个禁入生成目录、0 个 APK/EXE/本机配置。

## 统一契约

- `contracts/openapi.yaml` 来自 FastAPI 运行时导出，并与 backend 快照逐字一致；门禁校验最低 26 个 method+path 操作。
- `contracts/schema-version.json` 是 schema/API/最低客户端版本来源，backend 运行时拒绝环境变量漂移。
- `contracts/default-training-plan.json` 是默认计划唯一来源：`beginner_recomp_ab_v1`、A/B 各 8 槽、79 options、66 exercises、52 equipment；三端随包文件逐字一致。
- recommendation 8 cases 与 progression 5 cases 分发到三端测试资源并由三端测试代码消费。
- wire JSON 使用 snake_case；领域模型仍由各客户端 adapter 隔离，不直接让生成 DTO 承担 Room/SQLite 职责。

## Android 当前统一状态

- canonical 计划解析并保留 plan/day/slot/option/exercise/equipment UUID、处方、规则与原始快照；旧 built-in assignment 可迁移。
- server user/assignment/workout/readiness/cardio UUID 不再被固定本地 UUID 覆盖；无 assignment 的首次用户使用稳定、仅本地且不上传的 fallback assignment。
- bootstrap 与增量同步覆盖 user、catalog、plan tree、assignment、workout/set、readiness、cardio；`full_resync_required` 会执行权威 bootstrap 重建，保护 pending Outbox。
- API origin 改变、refresh 失败或刷新后再次 401 都会清令牌；logout 携 refresh token 撤销服务端 token family。
- A→B 登录先用临时 token bootstrap 预检，再原子切换 Room 身份；旧账号有 pending Outbox 时阻断切换。本地模式也先释放服务器身份，且内存 UI 会同步清空旧账号历史。
- 推荐运行时读取计划的 weekly target、minimum rest days、fatigue threshold 与 adaptation 规则；共享向量已参数化。
- Room v2、WorkManager、Keystore AES-GCM、HTTPS/Release 日志边界保持不变。

验证边界：本轮 JDK 21 下 main/test 源码编译阶段通过，但 Gradle test worker 未成功启动，因此 Android 最终 JVM/lint/assemble 必须在后续统一构建重跑。

## Windows 当前统一状态

- canonical 计划 UUID/处方及 weekly target、minimum rest days、fatigue threshold 已贯通 loader、远端 DTO、SQLite snapshot、Dashboard、推荐和管理发布。
- SQLite 升至 v8，补充 equipment、session 关联/来源/时区/版本、readiness/cardio 版本及 user cache。
- bootstrap/增量支持 user、readiness、cardio、workout session、独立 workout set、plan/assignment；处理 deleted/version/source。
- `full_resync_required` 触发全量 bootstrap，再从 bootstrap cursor 续拉；清理缺失的服务器镜像并保护未处理 Outbox。
- Outbox 使用 backend snake_case；只有明确 accepted/success/duplicate 才确认，冲突副本和错误会持久化并显示最近状态。
- refresh 失败、无 refresh token 或刷新后再次 401 会清 DPAPI；数字/字符串 epoch token 到期时间均兼容。
- 无 assignment 的首次用户使用稳定本地 fallback assignment，不向服务端伪造 assignment。
- JWT subject 与本地 `account_subject` 绑定；A→B 若有 pending Outbox/本地草稿会 fail-closed，无 pending 时事务清旧健康缓存和全部 cursor。未登录或认证变化会同步清空/重载五个业务 ViewModel，避免内存 UI 泄露。

验证：当前统一源码通过 xUnit 89/89 和 WPF build 0 warning/0 error；Release publish/EXE smoke 仍留待统一构建。

## Backend 当前统一状态

- Python 3.12 / FastAPI / Pydantic v2 / SQLAlchemy 2 / Alembic / MySQL 8.4；25 张同步业务表和 UTC/UUID/version/soft-delete 基线保留。
- canonical JSON deterministic seed 使用稳定 UUID；旧 UUID4 seed 会保留历史计划和目录引用，同时创建 canonical 身份，不再误报 `already_seeded`。
- 普通 change feed 不下发 draft 或无权 private plan；workout create/patch/sync 共用 assignment/version/day/slot/option/exercise/equipment 授权与同树校验。
- bootstrap 返回全部 active workout/readiness/cardio，不再截断 20/14 条；retention gap 的 `full_resync_required` 可由新版两端正确消费。
- `scripts/create_user.py` 幂等创建普通用户并自动分配 canonical published plan；不会静默轮换已有密码。
- 默认/占位生产密钥已移除并 fail-closed；根 `.env` 使用与当前工作目录无关的安全探测，浅层 `/app` 容器路径不会越界。
- recommendation/progression 纯规则服务和共享向量测试已接入；无 assignment fallback 的 adaptation week 从首次持久训练推导。

统一副本快速 pytest：72 passed / 1 real-MySQL test deselected。真实 MySQL、Docker 与完整 E2E 留待统一构建。

## 已知但不阻断源码整合的限制

- OpenAPI 的统一错误响应及少量 admin/recommendation 宽 schema 仍可继续收紧。
- workout/readiness/cardio 的部分 `expected_version` 仍可选；原子 CAS/行锁需在真实 MySQL 并发测试验证。
- Windows assignment 本地表未持久化全部 server 字段，跨页先到的 orphan workout set 没有暂存队列；冲突没有专门解决界面。
- Android/Windows 仍使用手写 wire adapter；根脚本已提供可选 OpenAPI client generation，但生成客户端尚未纳入本轮源码。

## 后续入口

- 契约：`scripts/validate-contracts.ps1`
- 全部测试：`scripts/test-all.ps1`
- 全部构建：`scripts/build-all.ps1`
- OpenAPI/可选客户端生成：`scripts/generate-clients.ps1`
- 发布汇总与 SHA-256：`scripts/package-release.ps1`

全量统一构建与真实跨端验收按项目安排在下一阶段执行。
