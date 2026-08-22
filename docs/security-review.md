# 安全审计

审计日期：2026-08-13。范围为三端统一源码、配置、迁移、构建脚本和旧交付报告；没有对生产环境、真实用户数据或生产 TLS/WAF 做渗透测试。

## 结论

统一仓库没有迁入 APK/EXE、数据库、备份、`.env`、签名材料或真实令牌。客户端没有 MySQL 直连能力；云端目录和数据只通过 REST API。根 Compose 不发布 MySQL 端口，且要求外部注入 MySQL/JWT 密钥；本地 bootstrap 用密码学随机数生成未提交的 `.env`。

本轮已修复主要的授权、同步、请求边界与客户端数据处理问题：未发布/无权计划不再进入普通 change feed，workout 引用必须属于当前用户和同一计划树；backend 按实际 ASGI 字节限制请求体，并用数据库行锁串行化健康记录的版本检查与写入；客户端换 API origin 或刷新失败后不再继续持有令牌，CSV 公式与 Windows 导入边界也已收紧。

相关快速回归已在本地执行，Windows 测试与 Release 编译也已验证；Android 本轮只确认到源码编译阶段，Gradle test worker 受中文工作路径影响未完成。真实 MySQL 并发、实际 APK/EXE、生产 TLS 和三端 E2E 仍须由后续 CI/统一构建验证，不能据此视为生产渗透测试已通过。

## 规范检查

| 检查项 | 结果与处理 | 验证状态 |
|---|---|---|
| APK/EXE 内 MySQL 密码 | Android/Windows 源码不含 MySQL 驱动、连接串或密码；旧二进制不进入统一源码 | 源码扫描通过；新二进制待构建扫描 |
| 硬编码 JWT 密钥 | 根 Compose 使用 `${JWT_SECRET:?...}`；bootstrap 随机生成；backend 所有环境都要求显式密钥，production 额外拒绝弱值/占位符 | secret scan 与 pytest 通过；生产部署待验证 |
| `.env` 提交 | 根 `.gitignore` 忽略所有 `.env*`，只放行空值 `.env.example` | 通过 |
| 信任所有证书 | Android 使用平台 TLS 且禁止明文；Windows 仅 loopback 可 HTTP，未发现自定义 trust-all | 通过 |
| 敏感日志 | backend 有敏感 key 脱敏；Android Release 无 HTTP logging；Windows 不再回显任意 API 错误正文，只提取限长、过滤后的直接错误字段，文件日志按 14 天尽力清理 | 源码与 Windows 回归通过；日志文件本身仍为明文 |
| 明文 Refresh Token | Android Keystore AES-GCM；Windows DPAPI CurrentUser；backend 只存 HMAC 摘要并轮换/撤销 | 通过（源码） |
| 未授权管理 API | backend 逐请求读取数据库 RBAC，不只信客户端/JWT UI；Windows 本地 admin UI 不是安全边界 | 后端测试已有；真实 E2E 待验证 |
| IDOR | workout assignment/version/day/slot/option/exercise/equipment 校验当前用户授权和同一计划树；同步按 user/计划可见性过滤 | 快速回归通过；真实 MySQL/E2E 待验证 |
| SQL 注入 | SQLAlchemy 和 SQLite 命令使用参数化；未发现 UI 拼接 SQL | 通过（静态） |
| CORS | production 禁止 wildcard，来源由环境变量白名单注入 | 通过（源码） |
| MySQL 公网暴露 | Compose 仅 `expose: 3306` 到内部网络，无 host `ports` | 通过 |
| 弱管理员密码 | 没有默认管理员；创建脚本交互/环境注入密码，不写长期容器配置 | 流程通过；组织密码策略待部署 |
| Release 调试日志 | Android Release `isDebuggable=false`；Windows Release 不提升权限 | 新产物待扫描/签名 |
| 导出隐私 | 导出必须由用户显式触发；文件为明文，应继续显示提示并由用户保护 | 已知风险/用户控制 |
| 请求体上限 | backend 同时快速检查 `Content-Length` 并按实际 ASGI 流累计字节，chunked/HTTP 2 无长度请求和未读取正文的端点同样受限 | 快速回归通过；生产网关仍应设置独立上限 |
| 依赖漏洞 | CI 对锁定的 Python 运行依赖执行 `pip-audit`，NuGet 对直接和传递依赖阻断 high/critical 告警；Dependabot 每周检查 Gradle、NuGet、pip 和 Actions | 本地 Python/NuGet 审计通过；新 CI 尚待执行 |

## 重点发现与统一处理

### 云端草稿可见性

基线 change feed 将大多数非个人实体作为全局变化下发，而 admin 在草稿 create/patch 时已写入完整 payload。普通用户可能绕过 `/plans/{id}` 的 draft 404，从 `/sync/changes` 看到未发布或私有草稿。

统一实现：只有 published 且用户可见的系统、自有或已分配计划可以进入普通用户 feed；草稿只对有权限的管理端点可见，并保留跨用户回归测试。

### Workout 计划引用 IDOR/数据完整性

基线接受 assignment/version/day/slot UUID，但没有完整确认 assignment 属于当前用户、所有引用属于同一计划树。

统一实现：create、patch 和 sync batch 共用同一引用校验；无权或不一致引用返回不暴露他人对象正文的结构化错误。

### 客户端令牌边界

- Android 更换 API origin 时必须清除现有令牌并重新登录，防止令牌发往新主机。
- 两端 401 只刷新一次；刷新失败或重试仍 401 后删除本地令牌并回到登录态。
- logout 在存在 pending Outbox/本地草稿时 fail-closed；可退出时发送当前 refresh token 撤销对应 token family，并清本地令牌与账号作用域缓存。

### 客户端账号隔离

- Android 以未落盘的新 token 先拉 bootstrap，Room 验证 server user 后才切换全局 TokenStore；有 pending Outbox 时保留旧身份并阻断 A→B 或本地模式切换，无 pending 时事务清旧账号健康缓存，同时清空旧内存 UI。
- Windows 把 JWT subject 绑定到持久化 `account_subject`；切换账号前检查 pending Outbox/本地草稿，安全时事务清旧 server cache 与全部 cursor，bootstrap 再校验 `user.id`。未登录不加载健康 ViewModel，认证变化会先清屏再重载。

### 同步确认

客户端只有收到 `accepted_outbox_ids` 或 operation result 的明确成功/duplicate 才删除 Outbox。204、空 body 或未知 response shape 不能被解释为整批成功。

### Retention 断档恢复

`/sync/changes` 返回 `full_resync_required` 时，两端不会应用该页或推进其 gap cursor，而是重新拉取 bootstrap。新版 bootstrap 返回本人全部未软删 workout/readiness/cardio、assignments、active catalog、当前计划、所有 assignment 引用的计划版本及 user/cursor；客户端以此替换服务器权威缓存，同时保护 pending Outbox、本地草稿和 canonical built-in 数据。

### 请求大小与并发写入

- backend 不再只信 `Content-Length`，而是在进入路由前读取并累计实际 ASGI 请求片段；超过配置上限立即返回稳定的 413，chunked 请求和不主动读取正文的端点也不能绕过。
- workout、workout set、readiness 与 cardio 的可变写入先锁定父级/目标数据库行，再在同一事务中完成版本检查和更新，避免不同幂等键并发写入时发生静默覆盖。
- `expected_version` 在兼容期仍为可选字段：提供时会在锁内原子校验并在过期时返回 409；旧客户端省略它时虽仍受行锁串行化，但不能获得“基于旧副本拒绝覆盖”的完整乐观并发保证。新版客户端应始终发送最近一次服务器副本的版本。

### Windows 导入、错误和日志边界

- DPAPI 令牌绑定规范化 API origin；旧格式或跨源令牌在任何授权请求发出前即被清除，导入文件不能修改 API 地址或数据目录。
- JSON 导入限制文件大小、嵌套深度与计划/训练/组数量；Android 和 Windows CSV 会中和以 `= + - @` 开头（含前导空白）的用户字段，避免电子表格公式执行。
- API 失败不再把完整响应正文放进异常或日志，只从有限大小的 JSON 中读取允许的直接字符串字段，并过滤敏感词、控制字符和超长内容。Windows 日志仅保留严格命名的最近 14 天文件；无法删除被占用旧文件时不阻断启动，并在后续写入重试清理。

### 依赖与默认部署基线

- Windows 统一到 .NET 10，显式固定 `Microsoft.Data.Sqlite` 10.0.11 与原生 bundle 3.0.5，并用运行时测试阻止原生 SQLite 低于 3.50.2 安全基线；NuGet audit 覆盖传递依赖并把 high/critical 告警视为错误。
- backend CI 对带 hash 的生产 `requirements.lock` 运行 `pip-audit`；Dependabot 为 Gradle、NuGet、pip 和 GitHub Actions 建立每周更新检查。
- 官方 backend Dockerfile 默认只启动一个 Gunicorn worker，使当前进程内登录限速在默认单容器部署中一致生效。横向扩容为多个容器/实例时仍必须使用网关或 Redis 等共享限流器。

## 仍需在部署/后续阶段完成

- 根 Compose 只提供内部 HTTP；生产和 Android E2E 必须配置受信任 TLS 终止，不得用 trust-all。
- Android Room、Windows SQLite、备份、日志和用户导出默认是明文；需要设备全盘加密、BitLocker/EFS、受控目录或后续字段级加密策略。
- 新 Windows EXE 未做组织代码签名；Android Release 需私有上传密钥签名。
- backend OpenAPI 的统一错误响应及少量 admin/recommendation schema 仍需继续收紧，避免生成客户端落回 `additionalProperties`；bootstrap 的 workout/readiness/cardio 已改为强类型。
- 健康记录写入已采用行锁，但兼容期 `expected_version` 仍可省略；真实 MySQL 下的并发锁行为还需专项集成测试。
- 默认 Docker 容器为单 worker，登录限速仍是进程内状态；多容器或多实例部署必须由网关/Redis 提供共享限速。
- 应用已按实际流量限制 chunked 请求体；生产 TLS 代理/网关仍应配置独立请求上限，形成外层资源保护。
- bootstrap 当前直接返回全部 active 个人历史且未分页；超长训练历史可能增加内存、响应体和移动网络负载，生产规模扩大前应演进为带一致性快照的分页 bootstrap。
- 明文健康数据导出和备份必须进入组织留存/删除策略。
- 真实 MySQL 迁移/种子、Android 完整单测、实际 APK/EXE 与三端 E2E 尚未在本轮本地环境形成完整验证证据，应以新的 CI 和统一构建结果为准。

## 可重复门禁

```powershell
python scripts/scan-secrets.py
.\scripts\validate-contracts.ps1
cd services/backend
pip-audit --strict --require-hashes --disable-pip -r requirements.lock
cd ../..
dotnet restore apps/windows/PersonalFitnessPlanner.sln -p:NuGetAudit=true -p:NuGetAuditMode=all -p:NuGetAuditLevel=high --force-evaluate
```

secret scan 拒绝提交 `.env`、私钥容器/私钥块、Bearer token、已知开发 JWT/MySQL 默认凭据。CI 已加入 Python 和 NuGet 源码依赖审计，但后续发布仍需对实际 APK/EXE、SBOM、Gradle 解析依赖和恶意软件做二进制级扫描。
