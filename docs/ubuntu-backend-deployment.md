# Ubuntu Docker 单服务器部署

仓库保留 Docker 自动部署脚本。新部署默认使用 backend 容器私有数据卷中的 SQLite，不再依赖 MySQL 容器或外部数据库。

## 部署

要求 Ubuntu 22.04、24.04 或 26.04，域名已解析到服务器，并开放 TCP 80/443。

```bash
git clone https://github.com/e4je/PersonalFitnessPlannerCloud.git
cd PersonalFitnessPlannerCloud
sudo bash scripts/deploy-backend-ubuntu.sh \
  --domain fitness.example.com \
  --email admin@example.com
```

脚本会检测并安装缺少的 Docker Engine/Compose、Nginx 和 Certbot，只启动一个 backend 容器，并申请 HTTPS 证书。后端只绑定宿主机 `127.0.0.1:8000`，公网流量经 Nginx 进入。

持久数据位于命名卷 `personal_fitness_planner_backend_config`：

- `/app-data/fitness.db`：SQLite 数据库；
- `/app-data/jwt-secret`：自动生成的 JWT 签名密钥。

容器入口会在启动 API 前自动运行 Alembic 和幂等 seed。打开 `https://fitness.example.com/web/` 后直接注册或登录，不需要数据库地址或 `setup_token`。

验证：

```bash
sudo docker compose \
  --env-file /etc/personal-fitness-planner/backend.env \
  -f infra/docker-compose.yml ps
curl -fsS https://fitness.example.com/health/live
curl -fsS https://fitness.example.com/health/ready
```

## 更新

先备份，再更新代码并重跑相同部署命令：

```bash
git pull --ff-only origin main
sudo bash scripts/deploy-backend-ubuntu.sh \
  --domain fitness.example.com \
  --email admin@example.com \
  --yes
```

不要运行 `docker compose down -v`，否则会删除 SQLite 数据库和 JWT 密钥所在的数据卷。

## 备份

使用应用自带的 SQLite 在线备份，再从容器复制出来：

```bash
sudo docker compose \
  --env-file /etc/personal-fitness-planner/backend.env \
  -f infra/docker-compose.yml exec -T backend \
  python -m scripts.backup_sqlite --output /app-data/fitness-backup.db

container_id="$(sudo docker compose \
  --env-file /etc/personal-fitness-planner/backend.env \
  -f infra/docker-compose.yml ps -q backend)"
sudo docker cp "$container_id:/app-data/fitness-backup.db" ./fitness-backup.db
```

复制完成后可删除卷内的临时备份。恢复时先停止 backend，保留当前 `fitness.db`，再把备份复制进数据卷并重新启动。若还迁移 `jwt-secret`，现有设备无需重新登录。

## 可选 MySQL

`bundled-db` profile 和 MySQL 8 兼容仍保留。只有明确需要 MySQL 时才设置 `DATABASE_BACKEND=mysql`、强随机 `MYSQL_PASSWORD`/`MYSQL_ROOT_PASSWORD`/`JWT_SECRET`，并使用 `--profile bundled-db`。MySQL 3306 不应映射到公网。
