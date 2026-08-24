# Ubuntu 单服务器自动部署

仓库提供 `scripts/deploy-backend-ubuntu.sh`，用于在一台 Ubuntu 服务器上部署一个后端实例。脚本会检测并安装缺失依赖、构建后端镜像、配置 Nginx、申请可信 HTTPS 证书，并保留首次 Web 数据库配置向导。

该脚本按单服务器设计，不配置负载均衡或多副本。支持 Docker 官方当前支持的 Ubuntu 22.04、24.04 和 26.04；Docker 使用官方 apt 仓库，Certbot 按官方建议通过 snap 安装。

## 前置条件

运行前需要准备：

- 一个已解析到服务器公网 IP 的完整域名，例如 `fitness.example.com`。
- 一个用于 Let's Encrypt 通知的真实邮箱。
- 公网允许访问 TCP 80 和 443。
- 后端容器可以访问的 MySQL 8 地址。MySQL 应位于内网、VPC 或 VPN，不应向公网开放 3306。
- 能通过 HTTPS 访问公开 GitHub 仓库；只读克隆不需要 GitHub SSH Key。

如果 MySQL 与 Docker 位于同一台服务器，首次向导中不能填写 `127.0.0.1`，因为该地址在容器中指向容器自身。应使用容器能够访问的宿主机地址，并让 MySQL 仅监听受控接口、通过防火墙限制来源。

## 首次部署

先通过 HTTPS 拉取公开仓库：

```bash
git clone https://github.com/e4je/PersonalFitnessPlannerCloud.git
cd PersonalFitnessPlannerCloud
git switch main
```

执行部署脚本：

```bash
sudo bash scripts/deploy-backend-ubuntu.sh \
  --domain fitness.example.com \
  --email admin@example.com
```

参数完整时仍会显示一次确认；自动化环境可增加 `--yes`：

```bash
sudo bash scripts/deploy-backend-ubuntu.sh \
  --domain fitness.example.com \
  --email admin@example.com \
  --yes
```

脚本仅在 Docker 不存在时使用 Docker 官方仓库安装 Engine、Buildx 和 Compose 插件，不会升级已经可用的 Docker。若检测到损坏或存在冲突的容器运行时，脚本会停止并要求人工确认，不会自动卸载可能承载其他服务的软件。

## 脚本创建的资源

- `/etc/personal-fitness-planner/backend.env`：权限为 `0600` 的生产 Compose 环境文件。首次部署不在其中保存 MySQL 密码或 JWT 密钥。
- `/etc/nginx/sites-available/personal-fitness-planner.conf`：反向代理配置。
- `personal_fitness_planner_backend_config`：保存数据库连接、自动生成 JWT 密钥和首次设置状态的私有 Docker volume。
- `personal-fitness-planner-backend` 镜像和单个 `backend` 容器。
- 由 Certbot 管理的 Let's Encrypt 证书及自动续期任务。

后端端口固定绑定宿主机 `127.0.0.1:8000`，公网只经过 Nginx 访问。脚本读取 backend 容器的 Docker 网关，并把 Gunicorn/Uvicorn 的代理信任范围限制为 loopback 和该网关，避免信任任意来源伪造的转发头。

## 首次数据库配置

部署结束时脚本会显示一次性 `setup_token`。打开：

```text
https://fitness.example.com/web/
```

填写 MySQL 地址、端口、账号、密码和 `setup_token`。数据库名由代码固定为 `fitness`：不存在时自动创建，已经存在时读取库信息并执行 Alembic 迁移与幂等 seed。

初始化账号需要：

- `fitness` 不存在时具有创建数据库权限。
- 对 `fitness` 具有建表、修改表和索引等 DDL 权限。
- 对业务表具有读写权限。

完成前 `/health/live` 返回 200，而 `/health/ready` 返回 503 `setup_required`；完成后 readiness 变为 200。

## 创建管理员

数据库初始化完成后，在服务器进入 root shell，临时读取管理员凭据：

```bash
sudo -i
cd /path/to/PersonalFitnessPlannerCloud
read -r -p "Admin email: " ADMIN_EMAIL
read -r -s -p "Admin password: " ADMIN_PASSWORD && echo
export ADMIN_EMAIL ADMIN_PASSWORD
docker compose \
  --env-file /etc/personal-fitness-planner/backend.env \
  -f infra/docker-compose.yml \
  exec -e ADMIN_EMAIL -e ADMIN_PASSWORD backend \
  python -m scripts.create_admin
unset ADMIN_EMAIL ADMIN_PASSWORD
exit
```

管理员密码不要写入仓库、长期 Compose 环境或 shell 历史。登录 Web 控制台后，个人使用场景建议关闭公开注册。

## 更新后端

先备份数据库，再在同一个仓库目录拉取并重新运行脚本：

```bash
git pull --ff-only origin main
sudo bash scripts/deploy-backend-ubuntu.sh \
  --domain fitness.example.com \
  --email admin@example.com \
  --yes
```

脚本会复用现有域名配置、证书和 Docker volume，重新构建当前提交。单实例入口会等待数据库、执行 Alembic upgrade 和幂等 seed，再启动应用。

不要执行 `docker compose down -v`。`-v` 会删除保存数据库密码和 JWT 密钥的运行配置 volume；若使用内置 MySQL，还会删除数据库数据卷。

## 状态和日志

```bash
sudo docker compose \
  --env-file /etc/personal-fitness-planner/backend.env \
  -f infra/docker-compose.yml ps

sudo docker compose \
  --env-file /etc/personal-fitness-planner/backend.env \
  -f infra/docker-compose.yml logs --tail 200 backend

curl -fsS https://fitness.example.com/health/live
curl -fsS https://fitness.example.com/health/ready
```

常见失败原因：

- Certbot 失败：检查域名 A/AAAA 记录、云安全组和 TCP 80/443。
- 首次数据库连接失败：检查填写的地址是否能从容器访问、MySQL 来源白名单和账号权限。
- HTTPS 重定向循环：重新运行部署脚本，让它重新识别 Docker 网关并重建 backend 容器。
- Docker 安装被阻止：服务器已有 `docker.io`、Podman、containerd 或其他可能承载现有服务的运行时；确认用途后再人工处理。

Docker 安装步骤以 [Docker Engine on Ubuntu](https://docs.docker.com/engine/install/ubuntu/) 为准；HTTPS 安装方式以 [Certbot Nginx instructions](https://certbot.eff.org/instructions?ws=nginx&os=ubuntufocal) 为准。
