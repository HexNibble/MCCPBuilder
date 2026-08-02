#!/usr/bin/env bash
set -Eeuo pipefail

DOMAIN=""
EMAIL=""
INSTALL_NGINX=1
ENABLE_TLS=1
SOURCE_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

usage() {
  cat <<'EOF'
MCCP 更新服务器安装脚本（Debian/Ubuntu）

用法：
  sudo ./install.sh [--domain updates.example.com --email admin@example.com]
                    [--no-nginx] [--no-tls]

选项：
  --domain DOMAIN  配置 Nginx 使用的公网域名。
  --email EMAIL    Let's Encrypt 通知邮箱；启用 TLS 时必填。
  --no-nginx       只安装 Python 服务，不安装或配置 Nginx。
  --no-tls         配置 HTTP 反向代理，不申请证书；仅建议内网测试。
  -h, --help       显示帮助。

脚本不会开放防火墙、关闭证书验证或上传发布密钥。
EOF
}

while (($#)); do
  case "$1" in
    --domain)
      DOMAIN="${2:-}"
      shift 2
      ;;
    --email)
      EMAIL="${2:-}"
      shift 2
      ;;
    --no-nginx)
      INSTALL_NGINX=0
      shift
      ;;
    --no-tls)
      ENABLE_TLS=0
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "未知参数：$1" >&2
      usage >&2
      exit 2
      ;;
  esac
done

if [[ ${EUID} -ne 0 ]]; then
  echo "请使用 sudo 或 root 运行此脚本。" >&2
  exit 1
fi
if [[ ! -f "${SOURCE_DIR}/mccp_update_server.py" ||
      ! -f "${SOURCE_DIR}/mccp_update_key.py" ||
      ! -f "${SOURCE_DIR}/mccp-update.service" ]]; then
  echo "安装包不完整：脚本必须与 server 目录中的服务文件放在一起。" >&2
  exit 1
fi
if [[ -n "${DOMAIN}" && ! "${DOMAIN}" =~ ^[A-Za-z0-9][A-Za-z0-9.-]*[A-Za-z0-9]$ ]]; then
  echo "域名格式无效：${DOMAIN}" >&2
  exit 1
fi
if [[ ${INSTALL_NGINX} -eq 1 && -z "${DOMAIN}" ]]; then
  echo "未提供 --domain，将只安装本机服务，不修改 Nginx。"
  INSTALL_NGINX=0
fi
if [[ ${INSTALL_NGINX} -eq 1 && ${ENABLE_TLS} -eq 1 && -z "${EMAIL}" ]]; then
  echo "启用 TLS 时必须提供 --email。" >&2
  exit 1
fi
if ! command -v apt-get >/dev/null 2>&1; then
  echo "当前脚本只支持带 apt-get 的 Debian/Ubuntu。" >&2
  exit 1
fi

export DEBIAN_FRONTEND=noninteractive
apt-get update
packages=(python3 ca-certificates)
if [[ ${INSTALL_NGINX} -eq 1 ]]; then
  packages+=(nginx)
  if [[ ${ENABLE_TLS} -eq 1 ]]; then
    packages+=(certbot python3-certbot-nginx)
  fi
fi
apt-get install -y --no-install-recommends "${packages[@]}"

if ! getent group mccp-update >/dev/null; then
  groupadd --system mccp-update
fi
if ! id -u mccp-update >/dev/null 2>&1; then
  useradd --system --gid mccp-update --home-dir /var/lib/mccp-update \
    --shell /usr/sbin/nologin mccp-update
fi

install -d -o root -g root -m 0755 /opt/mccp-update-server
install -d -o mccp-update -g mccp-update -m 0750 /var/lib/mccp-update
install -d -o root -g mccp-update -m 0750 /etc/mccp-update
install -o root -g root -m 0755 \
  "${SOURCE_DIR}/mccp_update_server.py" \
  /opt/mccp-update-server/mccp_update_server.py
install -o root -g root -m 0755 \
  "${SOURCE_DIR}/mccp_update_key.py" \
  /usr/local/sbin/mccp-update-key
install -o root -g root -m 0644 \
  "${SOURCE_DIR}/mccp-update.service" \
  /etc/systemd/system/mccp-update.service

if [[ ! -f /etc/mccp-update/publisher.key ]]; then
  /usr/local/sbin/mccp-update-key rotate
else
  chown root:mccp-update /etc/mccp-update/publisher.key
  chmod 0640 /etc/mccp-update/publisher.key
  echo "保留现有发布密钥：/etc/mccp-update/publisher.key"
fi

systemctl daemon-reload
systemctl enable --now mccp-update.service

if [[ ${INSTALL_NGINX} -eq 1 ]]; then
  nginx_config="/etc/nginx/sites-available/mccp-update"
  cat >"${nginx_config}" <<EOF
server {
    listen 80;
    listen [::]:80;
    server_name ${DOMAIN};
    client_max_body_size 16g;
    client_body_timeout 3600s;

    location / {
        proxy_pass http://127.0.0.1:18080;
        proxy_http_version 1.1;
        proxy_request_buffering off;
        proxy_buffering off;
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_read_timeout 3600s;
        proxy_send_timeout 3600s;
    }
}
EOF
  ln -sfn "${nginx_config}" /etc/nginx/sites-enabled/mccp-update
  nginx -t
  systemctl enable --now nginx
  systemctl reload nginx
  if [[ ${ENABLE_TLS} -eq 1 ]]; then
    certbot --nginx --non-interactive --agree-tos --redirect \
      --email "${EMAIL}" -d "${DOMAIN}"
  fi
fi

echo
echo "MCCP 更新服务安装完成。"
echo "服务状态：systemctl status mccp-update --no-pager"
echo "健康检查：curl http://127.0.0.1:18080/v1/health"
echo "查看密钥指纹：sudo mccp-update-key show"
echo "导出发布密钥：sudo mccp-update-key export /root/mccp-publisher.key"
if [[ ${INSTALL_NGINX} -eq 1 && ${ENABLE_TLS} -eq 1 ]]; then
  echo "公网地址：https://${DOMAIN}/"
elif [[ ${INSTALL_NGINX} -eq 1 ]]; then
  echo "HTTP 地址：http://${DOMAIN}/（仅建议内网测试）"
fi
