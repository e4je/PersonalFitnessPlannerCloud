# 部署说明

## Ubuntu 单服务器自动部署

仓库根提供 `scripts/deploy-backend-ubuntu.sh`，面向 Ubuntu 22.04、24.04 和 26.04 的单后端部署：

```bash
sudo bash scripts/deploy-backend-ubuntu.sh \
  --domain fitness.example.com \
  --email admin@example.com
```

脚本检测并安装缺失的 Docker Engine/Compose、Nginx 和 Certbot，只启动一个 backend，不启用内置 MySQL。它创建权限为 `0600` 的 `/etc/personal-fitness-planner/backend.env`，将应用端口限制在 `127.0.0.1:8000`，自动识别 Docker 网关作为可信代理，申请 HTTPS 证书并显示首次 `setup_token`。MySQL 凭据仍由用户在同源 HTTPS Web 向导中填写。

重复运行会保留 `personal_fitness_planner_backend_config` volume 并重新构建当前代码。更新前必须备份数据库；不得运行 `docker compose down -v`。完整前置条件、管理员创建、更新和排错步骤见仓库根的 `docs/ubuntu-backend-deployment.md`。

## 环境分层

开发、测试、生产必须使用不同数据库与 JWT 密钥。生产设置 `ENVIRONMENT=production` 后，应用会拒绝默认 JWT secret 和通配 CORS，并强制 HTTPS 重定向。若 TLS 在反向代理终止，代理必须正确传递协议头并只允许可信来源访问应用端口。

## 首次数据库配置

后端没有 `DATABASE_URL`/`MYSQL_PASSWORD` 且没有 `/app-data/backend-config.json` 时会进入 setup mode，而不是退出：

- `/health/live` 返回 200；`/health/ready` 返回 503 `setup_required`。
- `/web/` 与 `GET/POST /api/v1/setup/*` 可访问，其他业务 API 返回 503。
- 启动日志会打印一次性 `setup_token`；令牌保存在私有运行目录，重启后仍一致，初始化完成后失效。
- Web 只收 MySQL host、port、username、password；数据库名由代码固定为 `fitness`。
- 后端识别或创建库，执行 Alembic 与幂等 seed，最后原子写入 `/app-data/backend-config.json` 并切换数据库会话。

首次配置必须只运行一个 backend 容器，并先完成可信 HTTPS/TLS 终止；不要通过公网 HTTP 发送数据库凭据。初始化账号需要创建库、DDL 和业务 DML 权限。完成后可按平台能力切换到单独的最小权限运行账号，但必须保证后续发布迁移仍由受控迁移任务执行。

运行配置文件包含数据库密码和自动生成的 JWT 密钥。官方 Compose 通过 `backend_config` 命名 volume 持久化；该 volume 只能由 backend 服务账号访问，不应进入代码仓库、普通日志或公开备份。

## 发布流程

1. 验证 `requirements.lock`、`contracts/openapi.yaml` 和 Alembic head 已进入同一个构建产物。
2. 构建不可变镜像并执行镜像漏洞扫描。
3. 创建数据库快照或运行一致性备份。
4. 用单独的一次性任务执行 `alembic upgrade head`。
5. 执行 `python -m scripts.seed_default_plan`；脚本幂等，不修改已发布版本。
6. 启动应用实例，等待 `/health/ready`；再逐步切换流量。
7. 执行 `python -m scripts.smoke_test`，核对登录、bootstrap、同步和注销。
8. 在受控浏览器访问 `/web/`，用超级管理员验证账号管理、注册开关和计划草稿流程；不要把该地址暴露给不受信任的访客。

已有数据库配置时，Compose 入口脚本适合单机：它会自动迁移和 seed；未配置时则直接启动首次向导。多副本生产环境应先用单实例完成初始化，再将 `RUN_MIGRATIONS=0`、`RUN_SEED=0`，改由唯一发布任务完成，避免多个副本争用迁移锁。

管理员不由应用入口自动创建。服务健康后，通过一次性 `docker compose exec -e ADMIN_EMAIL -e ADMIN_PASSWORD backend python -m scripts.create_admin` 运维命令创建或提升管理员；只把变量注入该进程，完成后立即从运维终端清除，禁止把管理员密码保存在 Compose 服务环境或 `.env` 中。

普通用户同样由一次性 `python -m scripts.create_user` 运维命令创建。先完成 canonical seed；命令会在用户没有 active/scheduled assignment 时分配默认发布计划，不覆盖已有有效 assignment，也不会在未显式 `--update-password` 时轮换密码。

## Web 控制台与注册

FastAPI 会将 `services/backend/app/web/` 挂载为同源静态页面 `/web/`。首次向导中的数据库凭据仅通过同源 HTTPS 发送且不写浏览器存储；初始化后页面只保存浏览器会话令牌。公开注册默认开启，管理员可通过 Web 的“系统设置”或 `PATCH /api/v1/admin/settings/registration` 关闭；公开接口始终只创建 `user` 角色，管理员角色只能由超级管理员授予。

管理员账号页面可以创建、停用、重置密码和维护普通用户，查看用户的计划分配、训练、准备度和有氧概览。计划编辑遵循“新建草稿 → 校验保存 → 发布 → 分配”，已发布计划版本不会原地改写。审计日志记录账号和注册策略变更。

## HTTPS 与代理

- 只向反向代理暴露应用端口，公网仅开放 443。
- 使用现代 TLS、HSTS 与自动证书轮换。
- Nginx 必须传递 `X-Forwarded-Proto`；Gunicorn/Uvicorn 的 `FORWARDED_ALLOW_IPS` 只配置反向代理的实际来源 IP/网段，不得无条件设为 `*`。自动部署脚本会识别宿主机进入容器时使用的 Docker 网关。
- 若由代理传递真实客户端 IP，必须配置可信代理边界；应用默认不会信任任意 `X-Forwarded-For`。
- MySQL 3306 不映射到公网或宿主机。

## 数据库权限

迁移账号可以执行 DDL；运行账号仅需业务表 DML。若平台允许，迁移后把后端切换到独立的最小权限账号。MySQL session 会设置 `time_zone='+00:00'`，库采用 `utf8mb4_0900_ai_ci`。

## 回滚

优先回滚应用镜像，同时保持向后兼容的数据库迁移。只有确认 downgrade 不会丢数据时才执行 `alembic downgrade -1`。首迁移 downgrade 会删除全部业务表，仅可用于空测试环境，不可用于生产事故回滚。

## 监控

采集容器重启、HTTP 5xx/409/429、数据库连接、迁移版本、refresh 重放、登录失败、同步滞后和 change feed 保留窗口。健康检查只返回状态，不包含连接串、版本密钥或内部异常。
