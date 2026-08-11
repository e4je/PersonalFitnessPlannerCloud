# 安全审计

审计日期：2026-08-11。范围为三端统一源码、配置、迁移、构建脚本和旧交付报告；没有对生产环境、真实用户数据或生产 TLS/WAF 做渗透测试。

## 结论

统一仓库没有迁入 APK/EXE、数据库、备份、`.env`、签名材料或真实令牌。客户端没有 MySQL 直连能力；云端目录和数据只通过 REST API。根 Compose 不发布 MySQL 端口，且要求外部注入 MySQL/JWT 密钥；本地 bootstrap 用密码学随机数生成未提交的 `.env`。

审计发现的三类高风险一致性问题已在统一副本修复：未发布/无权计划不再进入普通 change feed，workout 引用必须属于当前用户和同一计划树，客户端换 API origin 或刷新失败后不再继续持有令牌。Backend 相关回归已进入 55 项快速测试；真实 MySQL 与跨端 E2E 仍须在后续统一构建验证。

## 规范检查

| 检查项 | 结果与处理 | 验证状态 |
|---|---|---|
| APK/EXE 内 MySQL 密码 | Android/Windows 源码不含 MySQL 驱动、连接串或密码；旧二进制不进入统一源码 | 源码扫描通过；新二进制待构建扫描 |
| 硬编码 JWT 密钥 | 根 Compose 使用 `${JWT_SECRET:?...}`；bootstrap 随机生成；backend 所有环境都要求显式密钥，production 额外拒绝弱值/占位符 | secret scan 与 pytest 通过；生产部署待验证 |
| `.env` 提交 | 根 `.gitignore` 忽略所有 `.env*`，只放行空值 `.env.example` | 通过 |
| 信任所有证书 | Android 使用平台 TLS 且禁止明文；Windows 仅 loopback 可 HTTP，未发现自定义 trust-all | 通过 |
| 敏感日志 | backend 有敏感 key 脱敏；Android Release 无 HTTP logging；Windows 仍需避免把完整 API 错误正文写入日志 | 部分通过；Windows 日志脱敏待加强 |
| 明文 Refresh Token | Android Keystore AES-GCM；Windows DPAPI CurrentUser；backend 只存 HMAC 摘要并轮换/撤销 | 通过（源码） |
| 未授权管理 API | backend 逐请求读取数据库 RBAC，不只信客户端/JWT UI；Windows 本地 admin UI 不是安全边界 | 后端测试已有；真实 E2E 待验证 |
| IDOR | workout assignment/version/day/slot/option/exercise/equipment 校验当前用户授权和同一计划树；同步按 user/计划可见性过滤 | 快速回归通过；真实 MySQL/E2E 待验证 |
| SQL 注入 | SQLAlchemy 和 SQLite 命令使用参数化；未发现 UI 拼接 SQL | 通过（静态） |
| CORS | production 禁止 wildcard，来源由环境变量白名单注入 | 通过（源码） |
| MySQL 公网暴露 | Compose 仅 `expose: 3306` 到内部网络，无 host `ports` | 通过 |
| 弱管理员密码 | 没有默认管理员；创建脚本交互/环境注入密码，不写长期容器配置 | 流程通过；组织密码策略待部署 |
| Release 调试日志 | Android Release `isDebuggable=false`；Windows Release 不提升权限 | 新产物待扫描/签名 |
| 导出隐私 | 导出必须由用户显式触发；文件为明文，应继续显示提示并由用户保护 | 已知风险/用户控制 |

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

## 仍需在部署/后续阶段完成

- 根 Compose 只提供内部 HTTP；生产和 Android E2E 必须配置受信任 TLS 终止，不得用 trust-all。
- Windows SQLite、备份、日志和用户导出默认是明文；需要 BitLocker/EFS/受控目录或后续字段级加密策略。
- 新 Windows EXE 未做组织代码签名；Android Release 需私有上传密钥签名。
- backend OpenAPI 的统一错误响应及少量 admin/recommendation schema 仍需继续收紧，避免生成客户端落回 `additionalProperties`；bootstrap 的 workout/readiness/cardio 已改为强类型。
- workout/readiness/cardio 的并发更新应完成原子 CAS/行锁验证，避免不同幂等键的 lost update。
- 登录限速为进程内状态；多实例生产必须由网关/Redis 提供全局限速。
- chunked 请求体大小必须由 TLS 代理/网关限流；不能只依赖 `Content-Length`。
- bootstrap 当前直接返回全部 active 个人历史且未分页；超长训练历史可能增加内存、响应体和移动网络负载，生产规模扩大前应演进为带一致性快照的分页 bootstrap。
- 明文健康数据导出和备份必须进入组织留存/删除策略。

## 可重复门禁

```powershell
python scripts/scan-secrets.py
.\scripts\validate-contracts.ps1
```

secret scan 拒绝提交 `.env`、私钥容器/私钥块、Bearer token、已知开发 JWT/MySQL 默认凭据。后续发布还需对实际 APK/EXE、SBOM、依赖漏洞和恶意软件做二进制级扫描。
