#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_NAME="${0##*/}"
SCRIPT_DIR="$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
REPO_ROOT="$(CDPATH= cd -- "$SCRIPT_DIR/.." && pwd -P)"
COMPOSE_FILE="$REPO_ROOT/infra/docker-compose.yml"
CONFIG_DIR="/etc/personal-fitness-planner"
ENV_FILE="$CONFIG_DIR/backend.env"
NGINX_SITE_NAME="personal-fitness-planner.conf"
NGINX_SITE_AVAILABLE="/etc/nginx/sites-available/$NGINX_SITE_NAME"
NGINX_SITE_ENABLED="/etc/nginx/sites-enabled/$NGINX_SITE_NAME"

DOMAIN=""
LETSENCRYPT_EMAIL=""
ASSUME_YES=0
APT_UPDATED=0
CERTBOT_BIN=""
TEMP_DIR=""

usage() {
  cat <<EOF
用法：
  sudo bash scripts/$SCRIPT_NAME --domain fitness.example.com --email admin@example.com [--yes]

选项：
  --domain DOMAIN   后端使用的公网域名；必须已解析到当前服务器
  --email EMAIL     Let's Encrypt 到期与安全通知邮箱
  --yes             不再询问部署确认，适合自动化执行
  -h, --help        显示帮助

脚本仅支持 Ubuntu 22.04、24.04 和 26.04。它会安装缺失的 Docker
Engine/Compose、Nginx 和 Certbot，启动单个 backend 容器并申请 HTTPS
证书。SQLite 数据库、JWT 密钥、表结构和默认训练计划会在私有数据卷中
自动初始化，不需要安装或配置 MySQL。
EOF
}

log() {
  printf '[deploy] %s\n' "$*"
}

warn() {
  printf '[deploy] 警告：%s\n' "$*" >&2
}

die() {
  printf '[deploy] 错误：%s\n' "$*" >&2
  exit 1
}

cleanup() {
  if [[ -n "$TEMP_DIR" && -d "$TEMP_DIR" ]]; then
    rm -rf -- "$TEMP_DIR"
  fi
}

on_error() {
  local exit_code=$?
  printf '[deploy] 部署在第 %s 行失败（退出码 %s）。请检查上方输出。\n' \
    "${BASH_LINENO[0]:-unknown}" "$exit_code" >&2
  exit "$exit_code"
}

trap cleanup EXIT
trap on_error ERR

while (($# > 0)); do
  case "$1" in
    --domain)
      (($# >= 2)) || die "--domain 缺少参数"
      DOMAIN="$2"
      shift 2
      ;;
    --domain=*)
      DOMAIN="${1#*=}"
      shift
      ;;
    --email)
      (($# >= 2)) || die "--email 缺少参数"
      LETSENCRYPT_EMAIL="$2"
      shift 2
      ;;
    --email=*)
      LETSENCRYPT_EMAIL="${1#*=}"
      shift
      ;;
    --yes)
      ASSUME_YES=1
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      die "未知参数：$1"
      ;;
  esac
done

if [[ -z "$DOMAIN" && -t 0 ]]; then
  read -r -p "后端域名（例如 fitness.example.com）: " DOMAIN
fi
if [[ -z "$LETSENCRYPT_EMAIL" && -t 0 ]]; then
  read -r -p "Let's Encrypt 通知邮箱: " LETSENCRYPT_EMAIL
fi

DOMAIN="${DOMAIN,,}"
[[ -n "$DOMAIN" ]] || die "必须提供 --domain"
[[ ${#DOMAIN} -le 253 ]] || die "域名过长"
[[ "$DOMAIN" == *.* ]] || die "必须使用可申请可信证书的完整域名，不能使用 localhost 或 IP"
[[ "$DOMAIN" != *..* ]] || die "域名不能包含连续的点"
[[ "$DOMAIN" =~ ^[a-z0-9]([a-z0-9.-]*[a-z0-9])$ ]] || die "域名格式无效"
IFS='.' read -r -a domain_labels <<< "$DOMAIN"
for domain_label in "${domain_labels[@]}"; do
  [[ ${#domain_label} -le 63 ]] || die "域名标签过长：$domain_label"
  [[ "$domain_label" =~ ^[a-z0-9]([a-z0-9-]*[a-z0-9])?$ ]] \
    || die "域名标签格式无效：$domain_label"
done
[[ "$LETSENCRYPT_EMAIL" =~ ^[a-zA-Z0-9][^[:space:]@]*@[^[:space:]@]+\.[^[:space:]@]+$ ]] \
  || die "Let's Encrypt 邮箱格式无效"

((EUID == 0)) || die "请使用 sudo 运行此脚本"
[[ -r /etc/os-release ]] || die "无法读取 /etc/os-release"
# shellcheck disable=SC1091
source /etc/os-release
[[ "${ID:-}" == "ubuntu" ]] || die "当前系统不是 Ubuntu"
case "${VERSION_ID:-}" in
  22.04|24.04|26.04) ;;
  *) die "仅支持 Ubuntu 22.04、24.04 和 26.04；当前版本为 ${VERSION_ID:-unknown}" ;;
esac
[[ -f "$COMPOSE_FILE" ]] || die "找不到 $COMPOSE_FILE，请在完整仓库中运行脚本"
[[ -f "$REPO_ROOT/services/backend/Dockerfile" ]] || die "后端 Dockerfile 不完整"

if ((ASSUME_YES == 0)); then
  [[ -t 0 ]] || die "非交互执行必须增加 --yes"
  printf '\n将执行以下操作：\n'
  printf '  - 安装缺失的系统依赖\n'
  printf '  - 构建并启动一个 backend 容器\n'
  printf '  - 配置 Nginx 与 HTTPS：%s\n' "$DOMAIN"
  printf '  - 配置文件写入：%s\n\n' "$CONFIG_DIR"
  read -r -p "继续？[y/N] " confirmation
  [[ "$confirmation" =~ ^[Yy]$ ]] || die "已取消"
fi

TEMP_DIR="$(mktemp -d)"

apt_update_once() {
  if ((APT_UPDATED == 0)); then
    log "更新 apt 软件索引"
    DEBIAN_FRONTEND=noninteractive apt-get update
    APT_UPDATED=1
  fi
}

package_installed() {
  dpkg-query -W -f='${db:Status-Abbrev}' "$1" 2>/dev/null | grep -q '^ii'
}

install_base_dependencies() {
  local -a packages=()
  package_installed ca-certificates || packages+=(ca-certificates)
  command -v curl >/dev/null 2>&1 || packages+=(curl)
  command -v git >/dev/null 2>&1 || packages+=(git)
  command -v nginx >/dev/null 2>&1 || packages+=(nginx)
  command -v snap >/dev/null 2>&1 || packages+=(snapd)

  if ((${#packages[@]} > 0)); then
    apt_update_once
    log "安装缺失依赖：${packages[*]}"
    DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends "${packages[@]}"
  else
    log "基础依赖已安装"
  fi

  systemctl enable --now nginx
  systemctl enable --now snapd.socket
}

docker_stack_usable() {
  command -v docker >/dev/null 2>&1 \
    && docker info >/dev/null 2>&1 \
    && docker compose version >/dev/null 2>&1
}

configure_docker_repository() {
  local codename architecture
  codename="${UBUNTU_CODENAME:-${VERSION_CODENAME:-}}"
  [[ -n "$codename" ]] || die "无法识别 Ubuntu 发行代号"
  architecture="$(dpkg --print-architecture)"

  install -m 0755 -d /etc/apt/keyrings
  curl -fsSL https://download.docker.com/linux/ubuntu/gpg \
    -o "$TEMP_DIR/docker.asc"
  install -m 0644 "$TEMP_DIR/docker.asc" /etc/apt/keyrings/docker.asc
  cat > "$TEMP_DIR/docker.sources" <<EOF
Types: deb
URIs: https://download.docker.com/linux/ubuntu
Suites: $codename
Components: stable
Architectures: $architecture
Signed-By: /etc/apt/keyrings/docker.asc
EOF
  install -m 0644 "$TEMP_DIR/docker.sources" /etc/apt/sources.list.d/docker.sources
  DEBIAN_FRONTEND=noninteractive apt-get update
  APT_UPDATED=1
}

install_docker_if_needed() {
  if command -v docker >/dev/null 2>&1; then
    systemctl enable --now docker 2>/dev/null || true
    if docker_stack_usable; then
      log "Docker Engine 与 Compose 已可用"
      return
    fi

    if docker info >/dev/null 2>&1 && ! docker compose version >/dev/null 2>&1; then
      log "检测到 Docker Engine，但缺少 Compose 插件"
      apt_update_once
      if DEBIAN_FRONTEND=noninteractive apt-get install -y docker-compose-v2; then
        docker compose version >/dev/null 2>&1 \
          || die "docker-compose-v2 已安装，但 docker compose 仍不可用"
        return
      fi
    fi
    die "检测到现有 Docker，但服务或 Compose 不可用；为避免破坏已有容器，脚本不会替换它"
  fi

  local -a conflicts=()
  local package
  for package in docker.io docker-compose docker-compose-v2 docker-doc docker-buildx \
    podman-docker containerd runc; do
    package_installed "$package" && conflicts+=("$package")
  done
  if ((${#conflicts[@]} > 0)); then
    die "发现可能冲突的容器软件包：${conflicts[*]}。请先确认其用途并手动处理"
  fi

  log "从 Docker 官方 apt 仓库安装 Docker Engine 与 Compose"
  configure_docker_repository
  DEBIAN_FRONTEND=noninteractive apt-get install -y \
    docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
  systemctl enable --now docker
  docker_stack_usable || die "Docker 安装后验证失败"
}

install_certbot_if_needed() {
  if command -v certbot >/dev/null 2>&1; then
    CERTBOT_BIN="$(command -v certbot)"
    log "Certbot 已安装"
    return
  fi

  log "通过 snap 安装 Certbot"
  timeout 120 snap wait system seed.loaded \
    || die "snapd 在 120 秒内没有准备就绪"
  snap install --classic certbot
  [[ -x /snap/bin/certbot ]] || die "Certbot 安装后验证失败"
  if [[ ! -e /usr/local/bin/certbot && ! -L /usr/local/bin/certbot ]]; then
    ln -s /snap/bin/certbot /usr/local/bin/certbot
  elif [[ -L /usr/local/bin/certbot \
    && "$(readlink /usr/local/bin/certbot)" != "/snap/bin/certbot" ]]; then
    warn "/usr/local/bin/certbot 已指向其他位置；部署将直接使用 /snap/bin/certbot"
  fi
  CERTBOT_BIN="/snap/bin/certbot"
}

set_env_value() {
  local file=$1 key=$2 value=$3 output
  output="$(mktemp "$CONFIG_DIR/backend.env.XXXXXX")"
  awk -v target_key="$key" -v target_value="$value" '
    BEGIN { replaced = 0 }
    index($0, target_key "=") == 1 {
      if (!replaced) {
        print target_key "=" target_value
        replaced = 1
      }
      next
    }
    { print }
    END {
      if (!replaced) print target_key "=" target_value
    }
  ' "$file" > "$output"
  chmod 0600 "$output"
  mv -f -- "$output" "$file"
}

prepare_environment() {
  if [[ -L "$CONFIG_DIR" || (-e "$CONFIG_DIR" && ! -d "$CONFIG_DIR") ]]; then
    die "$CONFIG_DIR 必须是普通目录且不能是符号链接"
  fi
  install -d -m 0700 "$CONFIG_DIR"
  [[ ! -L "$ENV_FILE" ]] || die "$ENV_FILE 不能是符号链接"
  if [[ ! -f "$ENV_FILE" ]]; then
    umask 077
    cat > "$ENV_FILE" <<EOF
# Managed by $SCRIPT_NAME. Keep this file private.
PFP_DEPLOY_DOMAIN=$DOMAIN
ENVIRONMENT=production
BACKEND_BIND_ADDRESS=127.0.0.1
BACKEND_PORT=8000

# SQLite data and the generated JWT key live in the backend_config volume.
DATABASE_BACKEND=sqlite
DATABASE_URL=
MYSQL_PASSWORD=
JWT_SECRET=

CORS_ORIGINS=["https://$DOMAIN"]
FORWARDED_ALLOW_IPS=127.0.0.1,::1
LOG_LEVEL=INFO
SQL_ECHO=false
EOF
    chmod 0600 "$ENV_FILE"
    log "已创建生产环境文件 $ENV_FILE"
  else
    local existing_domain
    existing_domain="$(sed -n 's/^PFP_DEPLOY_DOMAIN=//p' "$ENV_FILE" | head -n 1)"
    if [[ -n "$existing_domain" && "$existing_domain" != "$DOMAIN" ]]; then
      die "$ENV_FILE 已属于域名 $existing_domain；脚本不会自动改绑到 $DOMAIN"
    fi
    set_env_value "$ENV_FILE" PFP_DEPLOY_DOMAIN "$DOMAIN"
    set_env_value "$ENV_FILE" ENVIRONMENT production
    set_env_value "$ENV_FILE" BACKEND_BIND_ADDRESS 127.0.0.1
    set_env_value "$ENV_FILE" BACKEND_PORT 8000
    set_env_value "$ENV_FILE" CORS_ORIGINS "[\"https://$DOMAIN\"]"
    log "复用现有生产环境文件 $ENV_FILE"
  fi
}

compose() {
  docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" "$@"
}

wait_for_backend() {
  local attempt
  for attempt in {1..30}; do
    if curl -fsS --max-time 3 http://127.0.0.1:8000/health/live >/dev/null; then
      return
    fi
    sleep 2
  done
  compose logs --no-color --tail 100 backend >&2 || true
  die "后端在 60 秒内没有通过 liveness 检查"
}

configure_proxy_trust() {
  local container_id gateway trusted_proxies current_trusted_proxies
  container_id="$(compose ps -q backend)"
  [[ -n "$container_id" ]] || die "无法取得 backend 容器 ID"
  gateway="$(
    docker inspect --format '{{range .NetworkSettings.Networks}}{{println .Gateway}}{{end}}' \
      "$container_id" | awk 'NF {print; exit}'
  )"
  [[ -n "$gateway" ]] || die "无法识别 Docker 网关，不能安全信任反向代理协议头"
  trusted_proxies="127.0.0.1,::1,$gateway"
  current_trusted_proxies="$(
    sed -n 's/^FORWARDED_ALLOW_IPS=//p' "$ENV_FILE" | head -n 1
  )"
  if [[ "$current_trusted_proxies" != "$trusted_proxies" ]]; then
    set_env_value "$ENV_FILE" FORWARDED_ALLOW_IPS "$trusted_proxies"
    compose up -d --no-deps --force-recreate backend
    wait_for_backend
  fi
  log "已将反向代理信任范围限制为 $trusted_proxies"
}

configure_nginx() {
  [[ ! -L "$NGINX_SITE_AVAILABLE" ]] \
    || die "$NGINX_SITE_AVAILABLE 不能是符号链接"
  if [[ -e "$NGINX_SITE_AVAILABLE" ]]; then
    grep -Fq '# Managed by PersonalFitnessPlannerCloud deploy script' "$NGINX_SITE_AVAILABLE" \
      || die "$NGINX_SITE_AVAILABLE 已存在且不受本脚本管理"
    grep -Fq "server_name $DOMAIN;" "$NGINX_SITE_AVAILABLE" \
      || die "$NGINX_SITE_AVAILABLE 已绑定其他域名"
    log "复用现有 Nginx 站点配置"
  else
    cat > "$TEMP_DIR/$NGINX_SITE_NAME" <<EOF
# Managed by PersonalFitnessPlannerCloud deploy script.
server {
    listen 80;
    listen [::]:80;
    server_name $DOMAIN;

    client_max_body_size 2m;

    location / {
        proxy_pass http://127.0.0.1:8000;
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_set_header X-Forwarded-Host \$host;
        proxy_set_header X-Forwarded-Port \$server_port;
        proxy_read_timeout 60s;
    }
}
EOF
    install -m 0644 "$TEMP_DIR/$NGINX_SITE_NAME" "$NGINX_SITE_AVAILABLE"
  fi

  if [[ -L "$NGINX_SITE_ENABLED" ]]; then
    [[ "$(readlink -f "$NGINX_SITE_ENABLED")" == "$NGINX_SITE_AVAILABLE" ]] \
      || die "$NGINX_SITE_ENABLED 指向了其他配置"
  elif [[ -e "$NGINX_SITE_ENABLED" ]]; then
    die "$NGINX_SITE_ENABLED 已存在且不是符号链接"
  else
    ln -s "$NGINX_SITE_AVAILABLE" "$NGINX_SITE_ENABLED"
  fi

  nginx -t
  systemctl reload nginx

  if command -v ufw >/dev/null 2>&1 && ufw status | grep -q '^Status: active'; then
    ufw allow 'Nginx Full'
  fi
}

request_certificate() {
  getent ahosts "$DOMAIN" >/dev/null 2>&1 \
    || die "域名 $DOMAIN 尚未解析；请先添加 DNS A/AAAA 记录"
  log "向 Let's Encrypt 申请或复用 HTTPS 证书"
  "$CERTBOT_BIN" --nginx --non-interactive --agree-tos --redirect --hsts \
    --keep-until-expiring --no-eff-email --email "$LETSENCRYPT_EMAIL" -d "$DOMAIN"
  nginx -t
  systemctl reload nginx
}

print_result() {
  local ready_code
  ready_code="$(
    curl -sS --max-time 5 -o /dev/null -w '%{http_code}' \
      http://127.0.0.1:8000/health/ready || true
  )"

  printf '\n============================================================\n'
  printf '后端部署完成：%s\n' "https://$DOMAIN"
  printf 'Web 控制台：%s\n' "https://$DOMAIN/web/"
  printf '环境配置：%s\n' "$ENV_FILE"
  printf '本地数据库：Docker volume personal_fitness_planner_backend_config 中的 fitness.db\n'
  if [[ "$ready_code" == "200" ]]; then
    printf '数据库状态：SQLite 已自动初始化，readiness 检查通过。\n'
  else
    printf '数据库状态：异常（readiness 返回 %s），请检查 backend 容器日志。\n' \
      "${ready_code:-unknown}"
  fi
  printf '数据库不监听网络端口；只需开放 Web 的 80/443。\n'
  printf '============================================================\n'

  if ! curl -fsS --max-time 15 "https://$DOMAIN/health/live" >/dev/null; then
    warn "服务器本机无法回环访问公网域名，但证书申请已成功；请从外部浏览器验证"
  fi
}

install_base_dependencies
install_docker_if_needed
install_certbot_if_needed
prepare_environment

log "校验 Docker Compose 配置"
compose config --quiet
log "构建并启动单实例 backend"
compose up -d --build backend
wait_for_backend
configure_proxy_trust
configure_nginx
request_certificate
print_result
