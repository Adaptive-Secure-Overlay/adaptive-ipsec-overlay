#!/usr/bin/env bash
set -euo pipefail

NODE_NAME="${1:-${NODE_NAME:-}}"
PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
INSTALL_DIR="${INSTALL_DIR:-/opt/adaptive-ipsec-overlay}"
CONFIG_DIR="${CONFIG_DIR:-/etc/adaptive-ipsec-overlay}"
CONFIG_SOURCE="${CONFIG_SOURCE:-}"
START_NOW="${START_NOW:-1}"

if [[ "${EUID}" -ne 0 ]]; then
  echo "Run as root" >&2
  exit 1
fi

if [[ -z "${NODE_NAME}" ]]; then
  echo "Usage: NODE_NAME=User1 $0" >&2
  exit 1
fi

if command -v apt-get >/dev/null 2>&1; then
  apt-get update
  DEBIAN_FRONTEND=noninteractive apt-get install -y \
    bash python3 python3-cryptography nftables strongswan tcpdump iproute2
else
  echo "apt-get not found; install bash python3 cryptography nftables strongSwan tcpdump iproute2 manually" >&2
fi

install -d "${INSTALL_DIR}" "${INSTALL_DIR}/scripts" "${CONFIG_DIR}"
install -m 0644 "${PROJECT_DIR}/core/"*.py "${INSTALL_DIR}/"
install -m 0755 "${PROJECT_DIR}/scripts/"*.sh "${INSTALL_DIR}/scripts/"

if [[ -n "${CONFIG_SOURCE}" ]]; then
  install -m 0600 "${CONFIG_SOURCE}" "${CONFIG_DIR}/overlay.json"
elif [[ ! -f "${CONFIG_DIR}/overlay.json" ]]; then
  install -m 0600 "${PROJECT_DIR}/examples/overlay.sample.json" "${CONFIG_DIR}/overlay.json"
  echo "[WARN] Installed sample config to ${CONFIG_DIR}/overlay.json; edit it before starting real sessions."
fi

cat > "${CONFIG_DIR}/env" <<EOF
BASE_DIR=${INSTALL_DIR}
OVERLAY_CONFIG=${CONFIG_DIR}/overlay.json
NODE_NAME=${NODE_NAME}
IKE_PRIVACY_OVERLAY=1
IKE_PRIVACY_INLINE=1
PRECONNECT_ENABLED=1
EOF
chmod 0644 "${CONFIG_DIR}/env"

cat > /etc/systemd/system/adaptive-ipsec-overlay@.service <<EOF
[Unit]
Description=Adaptive IPsec overlay node %i
After=network-online.target strongswan-starter.service
Wants=network-online.target

[Service]
Type=forking
EnvironmentFile=${CONFIG_DIR}/env
PIDFile=/var/run/hybrid-overlay-%i.pid
ExecStart=${INSTALL_DIR}/scripts/debian_hybrid_start.sh %i
ExecStop=${INSTALL_DIR}/scripts/debian_hybrid_stop.sh %i
Restart=on-failure
RestartSec=3

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable "adaptive-ipsec-overlay@${NODE_NAME}.service"

if [[ "${START_NOW}" == "1" ]]; then
  systemctl restart "adaptive-ipsec-overlay@${NODE_NAME}.service"
fi

echo "[OK] Linux node ${NODE_NAME} installed in ${INSTALL_DIR}"
