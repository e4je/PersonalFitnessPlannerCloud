#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_NAME="${0##*/}"
SCRIPT_DIR="$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
REPO_ROOT="$(CDPATH= cd -- "$SCRIPT_DIR/.." && pwd -P)"
BACKEND_SOURCE="$REPO_ROOT/services/backend"

SERVICE_NAME="personal-fitness-planner-backend"
SERVICE_USER="pfp-backend"
INSTALL_DIR="/opt/personal-fitness-planner"
APP_DIR="$INSTALL_DIR/app"
APP_NEW_DIR="$INSTALL_DIR/app.new"
APP_PREVIOUS_DIR="$INSTALL_DIR/app.previous"
VENV_DIR="$INSTALL_DIR/venv"
VENV_NEW_DIR="$INSTALL_DIR/venv.new"
VENV_PREVIOUS_DIR="$INSTALL_DIR/venv.previous"
DATA_DIR="/var/lib/personal-fitness-planner"
CONFIG_DIR="/etc/personal-fitness-planner"
ENV_FILE="$CONFIG_DIR/backend-native.env"
SYSTEMD_UNIT="/etc/systemd/system/$SERVICE_NAME.service"
NGINX_SITE_NAME="personal-fitness-planner.conf"
NGINX_SITE_AVAILABLE="/etc/nginx/sites-available/$NGINX_SITE_NAME"
NGINX_SITE_ENABLED="/etc/nginx/sites-enabled/$NGINX_SITE_NAME"

DOMAIN=""
LETSENCRYPT_EMAIL=""
PYTHON_BIN=""
PIP_INDEX_URL="https://pypi.org/simple"
ASSUME_YES=0
APT_UPDATED=0
CERTBOT_BIN=""
TEMP_DIR=""
SERVICE_GROUP=""
RELEASE_SWAPPED=0
RELEASE_COMMITTED=0
OLD_RELEASE_AVAILABLE=0
STAGING_OWNED=0

usage() {
  cat <<EOF
用法：
  sudo bash scripts/$SCRIPT_NAME \\
    --domain fitness.example.com \\
    --email admin@example.com [选项]

选项：
  --domain DOMAIN         后端公网域名；必须已解析到当前服务器
  --email EMAIL           Let's Encrypt 证书账户邮箱
  --python-bin PATH       自定义 Python 3.12 可执行文件
  --pip-index-url URL     Python 包索引，默认 https://pypi.org/simple
  --yes                   不再询问部署确认
  -h, --help              显示帮助

本脚本不安装或使用 Docker。它以 Python 3.12 虚拟环境、systemd、Nginx 和
Certbot 部署单个后端进程。本地 SQLite 数据库、表结构、JWT 密钥和默认训练
计划会自动初始化，不需要安装或配置 MySQL。
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

rollback_release() {
  ((RELEASE_SWAPPED == 1 && RELEASE_COMMITTED == 0)) || return 0
  warn "新版本尚未通过健康检查，正在恢复旧的原生部署"
  trap - ERR
  set +e
  systemctl stop "$SERVICE_NAME" >/dev/null 2>&1
  if ((OLD_RELEASE_AVAILABLE == 1)); then
    rm -rf -- "$APP_DIR" "$VENV_DIR"
    mv -- "$APP_PREVIOUS_DIR" "$APP_DIR"
    mv -- "$VENV_PREVIOUS_DIR" "$VENV_DIR"
    systemctl daemon-reload
    systemctl start "$SERVICE_NAME"
  else
    rm -rf -- "$APP_DIR" "$VENV_DIR"
  fi
  set -e
  RELEASE_SWAPPED=0
  trap on_error ERR
}

cleanup() {
  rollback_release
  trap - ERR
  set +e
  if [[ -n "$TEMP_DIR" && -d "$TEMP_DIR" ]]; then
    rm -rf -- "$TEMP_DIR"
  fi
  if ((STAGING_OWNED == 1)); then
    rm -rf -- "$APP_NEW_DIR" "$VENV_NEW_DIR"
  fi
}

on_error() {
  local exit_code=$?
  rollback_release
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
    --python-bin)
      (($# >= 2)) || die "--python-bin 缺少参数"
      PYTHON_BIN="$2"
      shift 2
      ;;
    --python-bin=*)
      PYTHON_BIN="${1#*=}"
      shift
      ;;
    --pip-index-url)
      (($# >= 2)) || die "--pip-index-url 缺少参数"
      PIP_INDEX_URL="$2"
      shift 2
      ;;
    --pip-index-url=*)
      PIP_INDEX_URL="${1#*=}"
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
  read -r -p "Let's Encrypt 证书账户邮箱: " LETSENCRYPT_EMAIL
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
[[ "$PIP_INDEX_URL" =~ ^https://[^[:space:]]+$ ]] \
  || die "--pip-index-url 必须是 HTTPS URL"
[[ "$PIP_INDEX_URL" != *@* ]] \
  || die "--pip-index-url 不能内嵌账号或令牌"

((EUID == 0)) || die "请使用 sudo 运行此脚本"
[[ -r /etc/os-release ]] || die "无法读取 /etc/os-release"
# shellcheck disable=SC1091
source /etc/os-release
[[ "${ID:-}" == "ubuntu" ]] || die "当前系统不是 Ubuntu"
case "${VERSION_ID:-}" in
  22.04|24.04|26.04) ;;
  *) die "仅支持 Ubuntu 22.04、24.04 和 26.04；当前版本为 ${VERSION_ID:-unknown}" ;;
esac
[[ -f "$BACKEND_SOURCE/app/main.py" ]] || die "仓库缺少后端 app/main.py"
[[ -f "$BACKEND_SOURCE/requirements.lock" ]] || die "仓库缺少 requirements.lock"
[[ -f "$BACKEND_SOURCE/alembic.ini" ]] || die "仓库缺少 alembic.ini"

exec 9>/run/lock/personal-fitness-planner-native-deploy.lock
flock -n 9 || die "另一个原生部署进程正在运行"
STAGING_OWNED=1

if ((ASSUME_YES == 0)); then
  [[ -t 0 ]] || die "非交互执行必须增加 --yes"
  printf '\n将执行以下操作：\n'
  printf '  - 安装缺失的 Python 3.12、Nginx 和 Certbot 依赖\n'
  printf '  - 安装锁定的 Python 包并注册 systemd 单实例服务\n'
  printf '  - 配置 Nginx 与 HTTPS：%s\n' "$DOMAIN"
  printf '  - 私有运行配置保存到：%s\n\n' "$DATA_DIR"
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

python_is_312() {
  [[ -x "$1" ]] \
    && "$1" -c 'import sys; raise SystemExit(sys.version_info[:2] != (3, 12))' \
      >/dev/null 2>&1
}

resolve_python() {
  local candidate_path candidate_version
  if [[ -n "$PYTHON_BIN" ]]; then
    candidate_path="$(command -v -- "$PYTHON_BIN" 2>/dev/null || true)"
    [[ -n "$candidate_path" ]] || die "找不到 --python-bin 指定的程序：$PYTHON_BIN"
    PYTHON_BIN="$(readlink -f -- "$candidate_path")"
    python_is_312 "$PYTHON_BIN" \
      || die "--python-bin 必须指向可用的 Python 3.12：$PYTHON_BIN"
    log "使用指定的 Python：$PYTHON_BIN"
    return
  fi

  if command -v python3.12 >/dev/null 2>&1; then
    PYTHON_BIN="$(readlink -f -- "$(command -v python3.12)")"
    python_is_312 "$PYTHON_BIN" || die "python3.12 命令版本检查失败"
    log "使用系统 Python：$PYTHON_BIN"
    return
  fi

  apt_update_once
  candidate_version="$(apt-cache policy python3.12 | sed -n 's/^[[:space:]]*Candidate:[[:space:]]*//p' | head -n 1)"
  if [[ -z "$candidate_version" || "$candidate_version" == "(none)" ]]; then
    die "系统软件源没有 Python 3.12。请先安装 Python 3.12，再用 --python-bin 指定路径"
  fi

  log "从 Ubuntu 软件源安装 Python 3.12"
  DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
    python3.12 python3.12-venv
  PYTHON_BIN="$(readlink -f -- "$(command -v python3.12)")"
  python_is_312 "$PYTHON_BIN" || die "Python 3.12 安装后验证失败"
}

validate_python_location() {
  case "$PYTHON_BIN" in
    /root/*|/home/*)
      die "Python 位于受 systemd ProtectHome 隔离的目录：$PYTHON_BIN；请安装到 /usr 或 /opt"
      ;;
  esac
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
  fi
  CERTBOT_BIN="/snap/bin/certbot"
}

prepare_service_account() {
  local account_shell account_uid
  if ! id "$SERVICE_USER" >/dev/null 2>&1; then
    if getent group "$SERVICE_USER" >/dev/null 2>&1; then
      useradd --system --gid "$SERVICE_USER" --home-dir "$DATA_DIR" \
        --shell /usr/sbin/nologin "$SERVICE_USER"
    else
      useradd --system --user-group --home-dir "$DATA_DIR" \
        --shell /usr/sbin/nologin "$SERVICE_USER"
    fi
    log "已创建受限服务账号 $SERVICE_USER"
  fi
  account_uid="$(id -u "$SERVICE_USER")"
  account_shell="$(getent passwd "$SERVICE_USER" | cut -d: -f7)"
  [[ "$account_uid" =~ ^[0-9]+$ && "$account_uid" -lt 1000 ]] \
    || die "既有 $SERVICE_USER 不是系统账号，脚本不会使用它"
  [[ "$account_shell" == "/usr/sbin/nologin" || "$account_shell" == "/bin/false" ]] \
    || die "既有 $SERVICE_USER 不是禁止登录的服务账号，脚本不会使用它"
  SERVICE_GROUP="$(id -gn "$SERVICE_USER")"

  install -d -m 0755 -o root -g root "$INSTALL_DIR" "$CONFIG_DIR"
  install -d -m 0700 -o "$SERVICE_USER" -g "$SERVICE_GROUP" "$DATA_DIR"
}

prepare_environment_file() {
  if [[ -L "$ENV_FILE" ]]; then
    die "$ENV_FILE 不能是符号链接"
  fi
  if [[ -e "$ENV_FILE" ]]; then
    grep -Fq '# Managed by PersonalFitnessPlannerCloud native deploy script' "$ENV_FILE" \
      || die "$ENV_FILE 已存在且不受本脚本管理"
    local existing_domain
    existing_domain="$(sed -n 's/^PFP_NATIVE_DEPLOY_DOMAIN=//p' "$ENV_FILE" | head -n 1)"
    [[ -z "$existing_domain" || "$existing_domain" == "$DOMAIN" ]] \
      || die "$ENV_FILE 已属于域名 $existing_domain；脚本不会自动改绑到 $DOMAIN"
  fi

  cat > "$TEMP_DIR/backend-native.env" <<EOF
# Managed by PersonalFitnessPlannerCloud native deploy script.
PFP_NATIVE_DEPLOY_DOMAIN=$DOMAIN
ENVIRONMENT=production
DATABASE_BACKEND=sqlite
SQLITE_DATABASE_PATH=$DATA_DIR/fitness.db
JWT_SECRET=
RUNTIME_CONFIG_PATH=$DATA_DIR/backend-config.json
CORS_ORIGINS='["https://$DOMAIN"]'
FORWARDED_ALLOW_IPS=127.0.0.1,::1
LOG_LEVEL=INFO
PYTHONDONTWRITEBYTECODE=1
PYTHONUNBUFFERED=1
EOF
  install -m 0600 -o root -g root "$TEMP_DIR/backend-native.env" "$ENV_FILE"
}

prepare_release() {
  local -a pip_arguments
  local venv_candidate
  rm -rf -- "$APP_NEW_DIR" "$VENV_NEW_DIR"
  install -d -m 0755 -o root -g root "$APP_NEW_DIR"

  log "复制后端程序到临时发布目录"
  cp -a -- "$BACKEND_SOURCE/app" "$APP_NEW_DIR/app"
  cp -a -- "$BACKEND_SOURCE/alembic" "$APP_NEW_DIR/alembic"
  cp -a -- "$BACKEND_SOURCE/scripts" "$APP_NEW_DIR/scripts"
  cp -a -- "$BACKEND_SOURCE/contracts" "$APP_NEW_DIR/contracts"
  install -m 0644 "$BACKEND_SOURCE/alembic.ini" "$APP_NEW_DIR/alembic.ini"
  install -m 0644 "$BACKEND_SOURCE/pyproject.toml" "$APP_NEW_DIR/pyproject.toml"
  install -m 0644 "$BACKEND_SOURCE/requirements.lock" "$APP_NEW_DIR/requirements.lock"
  chown -R root:root "$APP_NEW_DIR"
  chmod -R go-w "$APP_NEW_DIR"

  log "创建全新的 Python 3.12 虚拟环境"
  if ! "$PYTHON_BIN" -m venv "$VENV_NEW_DIR"; then
    rm -rf -- "$VENV_NEW_DIR"
    venv_candidate="$(
      apt-cache policy python3.12-venv 2>/dev/null \
        | sed -n 's/^[[:space:]]*Candidate:[[:space:]]*//p' \
        | head -n 1
    )"
    if [[ -n "$venv_candidate" && "$venv_candidate" != "(none)" ]]; then
      apt_update_once
      DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends python3.12-venv
      "$PYTHON_BIN" -m venv "$VENV_NEW_DIR"
    else
      die "当前 Python 缺少 venv/ensurepip；请为 Python 3.12 安装 venv 支持"
    fi
  fi

  pip_arguments=(
    -m pip install
    --disable-pip-version-check
    --require-hashes
    --no-deps
    --index-url "$PIP_INDEX_URL"
    -r "$APP_NEW_DIR/requirements.lock"
  )
  log "从锁定清单安装 Python 依赖"
  "$VENV_NEW_DIR/bin/python" "${pip_arguments[@]}"

  log "校验后端可导入"
  (
    cd "$APP_NEW_DIR"
    runuser -u "$SERVICE_USER" -- env \
      ENVIRONMENT=production \
      RUNTIME_CONFIG_PATH="$DATA_DIR/backend-config.json" \
      JWT_SECRET= \
      CORS_ORIGINS="[\"https://$DOMAIN\"]" \
      PYTHONDONTWRITEBYTECODE=1 \
      "$VENV_NEW_DIR/bin/python" -c 'from app.main import app; assert app.title'
  )
}

write_systemd_unit() {
  cat > "$TEMP_DIR/$SERVICE_NAME.service" <<EOF
[Unit]
Description=Personal Fitness Planner Cloud backend (native Python)
Wants=network-online.target
After=network-online.target

[Service]
Type=simple
User=$SERVICE_USER
Group=$SERVICE_GROUP
WorkingDirectory=$APP_DIR
EnvironmentFile=$ENV_FILE
ExecStartPre=$VENV_DIR/bin/python -m alembic upgrade head
ExecStartPre=$VENV_DIR/bin/python -m scripts.seed_default_plan
ExecStart=$VENV_DIR/bin/python -m uvicorn app.main:app --host 127.0.0.1 --port 8000 --workers 1 --proxy-headers --forwarded-allow-ips 127.0.0.1,::1
Restart=on-failure
RestartSec=5s
TimeoutStopSec=30s
KillSignal=SIGINT
UMask=0077
NoNewPrivileges=true
PrivateTmp=true
PrivateDevices=true
ProtectSystem=strict
ProtectHome=true
ProtectKernelTunables=true
ProtectKernelModules=true
ProtectKernelLogs=true
ProtectControlGroups=true
ProtectClock=true
RestrictSUIDSGID=true
RestrictRealtime=true
RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6
CapabilityBoundingSet=
AmbientCapabilities=
ReadWritePaths=$DATA_DIR

[Install]
WantedBy=multi-user.target
EOF
  install -m 0644 -o root -g root "$TEMP_DIR/$SERVICE_NAME.service" "$SYSTEMD_UNIT"
}

wait_for_backend() {
  local attempt
  for attempt in {1..30}; do
    if systemctl is-active --quiet "$SERVICE_NAME" \
      && curl -fsS --max-time 3 http://127.0.0.1:8000/health/live >/dev/null; then
      return 0
    fi
    sleep 1
  done
  journalctl -u "$SERVICE_NAME" --no-pager -n 100 >&2 || true
  return 1
}

activate_release() {
  if [[ -e "$APP_DIR" || -e "$VENV_DIR" ]]; then
    [[ -d "$APP_DIR" && -x "$VENV_DIR/bin/python" ]] \
      || die "$INSTALL_DIR 中的既有原生部署不完整，脚本不会自动覆盖"
    OLD_RELEASE_AVAILABLE=1
  fi

  systemctl stop "$SERVICE_NAME" >/dev/null 2>&1 || true
  rm -rf -- "$APP_PREVIOUS_DIR" "$VENV_PREVIOUS_DIR"
  if ((OLD_RELEASE_AVAILABLE == 1)); then
    mv -- "$APP_DIR" "$APP_PREVIOUS_DIR"
    mv -- "$VENV_DIR" "$VENV_PREVIOUS_DIR"
  fi
  mv -- "$APP_NEW_DIR" "$APP_DIR"
  mv -- "$VENV_NEW_DIR" "$VENV_DIR"
  RELEASE_SWAPPED=1

  systemctl daemon-reload
  systemctl enable "$SERVICE_NAME" >/dev/null
  systemctl start "$SERVICE_NAME"
  if ! wait_for_backend; then
    rollback_release
    die "原生后端在 30 秒内没有通过 liveness 检查"
  fi
  RELEASE_COMMITTED=1
  log "systemd 后端已启动并通过 liveness 检查"
}

configure_nginx() {
  [[ ! -L "$NGINX_SITE_AVAILABLE" ]] \
    || die "$NGINX_SITE_AVAILABLE 不能是符号链接"
  if [[ -e "$NGINX_SITE_AVAILABLE" ]]; then
    grep -Fq '# Managed by PersonalFitnessPlannerCloud deploy script' "$NGINX_SITE_AVAILABLE" \
      || die "$NGINX_SITE_AVAILABLE 已存在且不受项目部署脚本管理"
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
  printf 'Ubuntu 原生后端部署完成：%s\n' "https://$DOMAIN"
  printf 'Web 控制台：%s\n' "https://$DOMAIN/web/"
  printf 'systemd 服务：%s\n' "$SERVICE_NAME"
  printf '本地数据库：%s\n' "$DATA_DIR/fitness.db"
  printf 'JWT 密钥文件：%s\n' "$DATA_DIR/jwt-secret"
  if [[ "$ready_code" == "200" ]]; then
    printf '数据库状态：SQLite 已自动初始化，readiness 检查通过。\n'
  else
    printf '数据库状态：异常（readiness 返回 %s），请检查 systemd 日志。\n' \
      "${ready_code:-unknown}"
  fi
  printf '请只开放 Web 的 80/443；无需开放数据库端口。\n'
  printf '============================================================\n'

  if ! curl -fsS --max-time 15 "https://$DOMAIN/health/live" >/dev/null; then
    warn "服务器本机无法回环访问公网域名，但证书申请已成功；请从外部浏览器验证"
  fi
}

install_base_dependencies
resolve_python
validate_python_location
install_certbot_if_needed
prepare_service_account
prepare_environment_file
prepare_release
write_systemd_unit
activate_release
configure_nginx
request_certificate
print_result
