# Backend deployment

## 默认数据库

新部署默认使用 SQLite：

- 数据库文件名为 `fitness.db`；
- 未提供 `JWT_SECRET` 时，会在同一私有数据目录生成稳定的 `jwt-secret`；
- 容器入口、Ubuntu systemd 和 Windows 计划任务都会先运行 `alembic upgrade head` 与幂等 seed；
- 不安装数据库服务，也不开放数据库网络端口。

数据目录必须只允许后端服务账号和管理员访问。生产环境还必须配置明确的 CORS 白名单，并由 Nginx、Caddy 或 IIS 提供可信 HTTPS。

## 推荐入口

Ubuntu 原生部署：

```bash
sudo bash scripts/deploy-backend-ubuntu-native.sh \
  --domain fitness.example.com \
  --email admin@example.com
```

Windows 原生部署：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\deploy-backend-windows.ps1 `
  -Domain fitness.example.com `
  -Port 18000
```

Ubuntu Docker 部署：

```bash
sudo bash scripts/deploy-backend-ubuntu.sh \
  --domain fitness.example.com \
  --email admin@example.com
```

平台路径、HTTPS、管理员创建、更新和恢复步骤见仓库根目录：

- `docs/native-backend-deployment.md`
- `docs/ubuntu-backend-deployment.md`

## 备份与恢复

运行中的 SQLite 应使用内置在线备份，避免直接复制可能同时存在 WAL 的实时文件：

```bash
python -m scripts.backup_sqlite --output /secure/backup/fitness.db
```

命令拒绝覆盖已有文件，并对新副本执行 SQLite 完整性检查。恢复时停止唯一后端实例，保留当前数据库，替换 `fitness.db` 后重新启动并检查 `/health/ready`。迁移 `jwt-secret` 可以保留现有登录；只迁移数据库则所有设备重新登录，但账号和业务数据不会丢失。

## 可选 MySQL 8

旧部署仍兼容 MySQL。设置 `DATABASE_BACKEND=mysql` 后，可以使用离散 `MYSQL_*` 字段、完整 `DATABASE_URL`，或在没有凭据时进入受一次性令牌保护的 Web 配置向导。旧版 `backend-config.json` 优先于新的 SQLite 默认值，因此更新不会静默脱离现有 MySQL 数据。

MySQL 账号需要迁移所需的 DDL/DML 权限，3306 不得暴露到公网。真实 MySQL 集成测试继续由 CI 执行。

## 发布检查

1. 备份数据库和 JWT 密钥。
2. 验证锁定依赖、Alembic head、OpenAPI 与共享契约属于同一提交。
3. 仅运行一个迁移实例，再启动单 worker 后端。
4. 检查 `/health/live`、`/health/ready`、登录、bootstrap 与同步。
5. 确认公网只经 HTTPS 代理进入，后端监听地址仍为 loopback。

管理员不由应用自动创建。服务就绪后使用一次性的 `python -m scripts.create_admin` 运维命令，且不要把管理员密码写入长期环境文件。
