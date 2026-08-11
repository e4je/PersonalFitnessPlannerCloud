# 部署说明

## 环境分层

开发、测试、生产必须使用不同数据库与 JWT 密钥。生产设置 `ENVIRONMENT=production` 后，应用会拒绝默认 JWT secret 和通配 CORS，并强制 HTTPS 重定向。若 TLS 在反向代理终止，代理必须正确传递协议头并只允许可信来源访问应用端口。

## 发布流程

1. 验证 `requirements.lock`、`contracts/openapi.yaml` 和 Alembic head 已进入同一个构建产物。
2. 构建不可变镜像并执行镜像漏洞扫描。
3. 创建数据库快照或运行一致性备份。
4. 用单独的一次性任务执行 `alembic upgrade head`。
5. 执行 `python -m scripts.seed_default_plan`；脚本幂等，不修改已发布版本。
6. 启动应用实例，等待 `/health/ready`；再逐步切换流量。
7. 执行 `python -m scripts.smoke_test`，核对登录、bootstrap、同步和注销。

Compose 的入口脚本适合单机：它会自动迁移和 seed。多副本生产环境应将 `RUN_MIGRATIONS=0`、`RUN_SEED=0`，改由唯一的发布任务完成，避免多个副本争用迁移锁。

管理员不由应用入口自动创建。服务健康后，通过一次性 `docker compose exec -e ADMIN_EMAIL -e ADMIN_PASSWORD backend python -m scripts.create_admin` 运维命令创建或提升管理员；只把变量注入该进程，完成后立即从运维终端清除，禁止把管理员密码保存在 Compose 服务环境或 `.env` 中。

普通用户同样由一次性 `python -m scripts.create_user` 运维命令创建。先完成 canonical seed；命令会在用户没有 active/scheduled assignment 时分配默认发布计划，不覆盖已有有效 assignment，也不会在未显式 `--update-password` 时轮换密码。

## HTTPS 与代理

- 只向反向代理暴露应用端口，公网仅开放 443。
- 使用现代 TLS、HSTS 与自动证书轮换。
- 若由代理传递真实客户端 IP，必须配置可信代理边界；应用默认不会信任任意 `X-Forwarded-For`。
- MySQL 3306 不映射到公网或宿主机。

## 数据库权限

迁移账号可以执行 DDL；运行账号仅需业务表 DML。若平台允许，迁移后把后端切换到独立的最小权限账号。MySQL session 会设置 `time_zone='+00:00'`，库采用 `utf8mb4_0900_ai_ci`。

## 回滚

优先回滚应用镜像，同时保持向后兼容的数据库迁移。只有确认 downgrade 不会丢数据时才执行 `alembic downgrade -1`。首迁移 downgrade 会删除全部业务表，仅可用于空测试环境，不可用于生产事故回滚。

## 监控

采集容器重启、HTTP 5xx/409/429、数据库连接、迁移版本、refresh 重放、登录失败、同步滞后和 change feed 保留窗口。健康检查只返回状态，不包含连接串、版本密钥或内部异常。
