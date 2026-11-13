#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DIST="${ROOT}/dist"
STAGE="${DIST}/adaptive-ipsec-overlay"

rm -rf "${STAGE}"
mkdir -p "${STAGE}/platforms"
cp -r "${ROOT}/core" "${ROOT}/scripts" "${ROOT}/examples" "${ROOT}/docs" "${STAGE}/"
cp -r "${ROOT}/platforms/linux" "${ROOT}/platforms/openwrt" "${ROOT}/platforms/routeros7" "${STAGE}/platforms/"
cp "${ROOT}/README.md" "${ROOT}/README.ru.md" "${STAGE}/"

mkdir -p "${DIST}"
tar -C "${DIST}" -czf "${DIST}/adaptive-ipsec-overlay.tar.gz" adaptive-ipsec-overlay
echo "[OK] ${DIST}/adaptive-ipsec-overlay.tar.gz"
