# API 与配置

## 配置 API

在“设置”页填写 API 根地址并保存，例如：

```text
https://fitness.example.com/
```

地址必须是绝对 URI。生产环境应使用有效证书的 HTTPS；开发时若使用 HTTP，应仅绑定可信本机或隔离网络。客户端绝不需要 MySQL 连接字符串。

登录后，访问令牌用于调用 `/api/v1` 资源；刷新令牌受 Windows 当前用户 DPAPI 保护。退出登录会清除本地令牌。管理模式只有在令牌角色声明为管理员时才有效。

## 后端契约与当前调用

Windows 客户端当前直接调用认证端点 `POST /api/v1/auth/login`、`POST /api/v1/auth/refresh`、`POST /api/v1/auth/logout`，并通过 `GET /api/v1/bootstrap` 获取启动数据。

后端最低互操作契约还定义 `GET /api/v1/me`、`GET /api/v1/plans/current`、`GET /api/v1/plans/{plan_version_id}`、`GET /api/v1/exercises`、`GET /api/v1/equipment`、`GET/POST/PATCH /api/v1/workout-sessions` 和 `POST /api/v1/readiness`。Windows 客户端当前不逐项直连这些读取/训练端点，而是把离线写入放入 Outbox，并通过 bootstrap 与增量同步交换相同数据。

同步：`GET /api/v1/sync/changes` 与 `POST /api/v1/sync/batch`。请求携带稳定幂等键；拉取游标只在批次成功提交到 SQLite 后推进。

管理员端点位于 `/api/v1/admin/*`，覆盖动作、器械、计划版本、发布、分配、审计与同步状态。后端仍须逐请求鉴权。

## 其他设置

- 时区使用 IANA 名称；服务器时间为 UTC，训练日期另存 `local_date`。
- 单位可选公斤或磅；数据库与同步契约以公斤为基准，界面负责换算。
- 训练日影响建议排程，不改变已经保存的历史日期。
- 数据目录支持绝对中文路径和空格；修改后应重启应用并确认迁移/导入结果。
- `--data-dir <路径>` 可在启动时覆盖默认目录；`--smoke-test` 用于无交互启动验收并自动退出。

设置页的“完整重新同步/云端覆盖”会重新获取 bootstrap 并事务性重建云端权威缓存；本地计划草稿和设置会保留。为避免覆盖离线记录，只要存在未发送训练 Outbox，该操作就会被拒绝，请先使用“上传本地”或导出备份。离线模式下该操作会被拒绝。

## 故障排查

“立即同步”失败时先确认 API 地址、系统时间、证书和登录状态。离线记录会保留在 Outbox，恢复网络后可重试；不要手工删除 `fitness.db` 来消除同步错误。若增量缓存明显不一致，可在完成上传或备份后使用“云端覆盖”。诊断时请保留 `logs/` 和最近备份，但分享前检查其中是否含个人训练信息。
