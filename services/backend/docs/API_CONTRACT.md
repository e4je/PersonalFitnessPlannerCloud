# API 契约要点

- Base URL：`/api/v1`；Bearer Access Token 放在 `Authorization` 头。
- 登录体为 JSON `email/password/device_name`。
- `Idempotency-Key` 对训练、readiness、cardio 和 sync batch 必填；同键同载荷回放首次结果，同键异载荷为 409。
- 可同步对象含 `id/version/created_at/updated_at/deleted_at`；删除为软删除。
- 分页统一 `items/cursor/next_cursor/has_more`；同步响应另含 `full_resync_required`。
- `full_resync_required` 断档页不得应用或推进 cursor；客户端改拉 bootstrap，并以其中本人 active workout/readiness/cardio、assignments、catalog 与相关计划版本替换服务器权威缓存，同时保护 pending Outbox。
- 时间使用 ISO 8601 UTC；训练归属日期使用 `local_date`，用户保存 IANA timezone。
- 乐观锁冲突返回 409，错误 detail 含 `code`、`server_version` 和 `server_copy`。
- 已发布计划和完整子树不可 PATCH；创建下一版本后再发布、分配。
- `GET /auth/registration-status` 只返回公开注册开关；`POST /auth/register` 只创建普通 `user` 账号并返回令牌，管理员可通过 `/admin/settings/registration` 开关控制。
- `/admin/users` 与 `/admin/users/{id}/overview` 仅管理员可访问；超级管理员才可授予/撤销管理员角色，最后一个 active privileged account 不能被停用或降权。
- Web 控制台挂载在同源 `/web/`，不绕过 API 鉴权；浏览器操作计划时仍必须创建草稿、校验、发布后再分配。

机器可读的完整请求/响应模型、状态码和鉴权要求见 `contracts/openapi.yaml`。
