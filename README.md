# Adaptive Secure Overlay - IPsec Track Hub

![Adaptive Secure Overlay](assets/banner.svg)

[![Track](https://img.shields.io/badge/track-ipsec%20core-0f172a?style=for-the-badge)](https://github.com/Adaptive-Secure-Overlay/adaptive-ipsec-overlay)
[![Status](https://img.shields.io/badge/status-research%20prototype-15803d?style=for-the-badge)](https://github.com/Adaptive-Secure-Overlay/adaptive-ipsec-overlay)
[![Scope](https://img.shields.io/badge/architecture-routed%20control-plane-0369a1?style=for-the-badge)](https://github.com/Adaptive-Secure-Overlay/adaptive-ipsec-overlay)

Adaptive Secure Overlay is a lab research track for adaptive secure channel startup:
routed control-plane setup across selected intermediate nodes with direct protected
data-plane continuation.

## Family

| Repository | Role | Status |
| --- | --- | --- |
| [adaptive-ipsec-overlay](https://github.com/Adaptive-Secure-Overlay/adaptive-ipsec-overlay) | Hub and shared docs | Active |
| [adaptive-ipsec-overlay-linux](https://github.com/Adaptive-Secure-Overlay/adaptive-ipsec-overlay-linux) | Linux endpoint + intermediate package | Active |
| [adaptive-ipsec-overlay-openwrt](https://github.com/Adaptive-Secure-Overlay/adaptive-ipsec-overlay-openwrt) | OpenWRT endpoint + intermediate package | Active |
| [adaptive-ipsec-overlay-routeros7](https://github.com/Adaptive-Secure-Overlay/adaptive-ipsec-overlay-routeros7) | MikroTik relay (container) | Active |
| [adaptive-secure-overlay-windows](https://github.com/Adaptive-Secure-Overlay/adaptive-secure-overlay-windows) | Windows client track | Planned |
| [adaptive-secure-overlay-wireguard](https://github.com/Adaptive-Secure-Overlay/adaptive-secure-overlay-wireguard) | WireGuard compatibility track | Planned |

## Core idea

- **Control-plane is routed.** IKE/IKE-like exchange can be relayed through X1/X2.
- **Data-plane is optimized.** Protected packets are forwarded directly once a session
  is agreed.
- **Route knowledge is partitioned** between relay roles for controlled exposure.
- **Cross-platform path.** Current research baseline supports Linux/OpenWRT and
  RouterOS relay mode in EVE-NG lab setups.

## Quick start

1. Pick a platform package from the family table.
2. Copy `examples/overlay.sample.json` as `overlay.json` and set node parameters.
3. Install with package-specific script.
4. Start service/daemon and check logs with platform tools.

## Security note

Current sample configuration uses shared lab secrets. Never publish real network
credentials.
