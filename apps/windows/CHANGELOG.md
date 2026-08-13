# 更新日志

本项目遵循“已发布计划版本不可原地修改”的数据规则；这里记录客户端交付变化。

## Unreleased - 统一仓库整合

- 默认计划改用根 canonical JSON/UUID，并贯通周目标、最少休息日、疲劳阈值及共享 recommendation/progression 向量。
- SQLite 升至 v8，补齐主要 session/set/readiness/cardio/user 云端字段与迁移测试。
- 增量 pull 覆盖 user、workout set、readiness、cardio、删除/version/source；retention gap 会触发全量 bootstrap 并保护 pending Outbox。
- Outbox 改为 backend snake_case 和逐项明确确认，冲突/错误不再误删；刷新失败会清 DPAPI token。
- 无服务端 assignment 的首次用户使用稳定、仅本地且不上传的 fallback assignment。
- 将 SQLite cache/cursor 绑定 JWT subject；账号切换在 pending Outbox/本地草稿时阻断，安全切换时事务清旧账号缓存，并同步清空旧业务 ViewModel。
- 将 DPAPI token 绑定到规范化 API 源站；旧 token、跨源切换和导入备份修改源站均 fail closed。
- 将 `Microsoft.Data.Sqlite` 固定至 8.0.29、原生 bundle 固定至 3.0.5，并增加 SQLite 3.50.2 最低运行时门禁；CSV 阻断表格公式执行，JSON 导入增加大小/结构上限且只恢复可移植偏好。
- API 失败只保留限长且过滤后的安全错误字段，不再把任意响应正文写入异常；应用日志按 14 天尽力清理。
- 固定更新后的 SQLite 托管/原生依赖，并对直接和传递 NuGet high/critical 漏洞启用还原门禁。

## 1.0.0 - 2026-08-09

- 初始 Windows 10/11 x64 WPF 客户端。
- 加入首页、A/B 训练执行、离线自动保存、中断恢复、疼痛反馈与组间计时。
- 加入历史筛选、软删除、计划/动作快照及 CSV/JSON 导出。
- 加入动作库、计划草稿、发布新版本、分配与回滚管理流程。
- 加入 SQLite 自动 migration、Outbox 幂等同步、增量游标和本地备份轮换。
- 加入首次 bootstrap、保留本地草稿/Outbox 的完整重新同步，以及服务器明确 accepted 才确认 Outbox 的语义。
- 将精确动作 UUID、计划选项 UUID 与器械隔离的重量建议接入训练界面；疼痛记录会阻止加重。
- 加入角色声明驱动的管理权限检查。
- 加入 self-contained、single-file、win-x64 发布与中文路径 EXE smoke test。
