# Adaptive IPsec Overlay

![Adaptive IPsec Overlay](assets/banner.svg)

Adaptive IPsec Overlay is an experimental control-plane overlay for IPsec/IKE
research. It routes IKE control traffic through selected intermediate nodes and
lets ESP data traffic continue directly between endpoints after the IPsec SA is
established.

This repository contains three deployment profiles:

- `install/linux`: Linux endpoint/intermediate node with strongSwan integration.
- `install/openwrt`: OpenWRT endpoint/intermediate node with strongSwan integration.
- `install/mikrotik`: MikroTik RouterOS v7 container profile for intermediate
  overlay operation.

## Platform profiles

| Platform | Role | Installer | Notes |
| --- | --- | --- | --- |
| Linux | Endpoint and intermediate | `install/linux/install.sh` | strongSwan, nftables, XFRM bypass, systemd |
| OpenWRT | Endpoint and intermediate | `install/openwrt/install.sh` | strongSwan, nftables, init.d |
| RouterOS 7 | Intermediate only | `install/mikrotik/install-container.rsc` | RouterOS container package, overlay relay daemon |

## Current status

This is a research prototype, not a production VPN client. Use it in a lab first.

The Linux/OpenWRT path intercepts local IKE UDP/500 and IKE-over-UDP/4500,
forwards the IKE datagrams through the overlay, then leaves ESP (`IP proto 50`)
to flow directly between endpoints.

The MikroTik path is intermediate-only. It runs the overlay daemon in a RouterOS
container and does not make RouterOS a strongSwan ESP endpoint.

## Configuration

Copy and edit a config:

```bash
cp examples/overlay.sample.json /etc/adaptive-ipsec-overlay/overlay.json
```

Each user entry needs:

- `ip`: node underlay IP address.
- `port`: overlay UDP port, usually `9000`.
- `password`: shared lab secret for the current prototype.

For real deployments, replace all sample passwords.

## Linux install

On a Debian/Ubuntu endpoint:

```bash
sudo NODE_NAME=User1 CONFIG_SOURCE=examples/overlay.sample.json ./install/linux/install.sh
```

The installer copies code to `/opt/adaptive-ipsec-overlay`, writes
`/etc/adaptive-ipsec-overlay/env`, installs a systemd template service and starts:

```bash
systemctl status adaptive-ipsec-overlay@User1
journalctl -u adaptive-ipsec-overlay@User1
tail -f /var/log/hybrid-overlay-User1.log
```

## OpenWRT install

Copy this repository or release tarball to the OpenWRT router, then run:

```bash
NODE_NAME=User11 CONFIG_SOURCE=examples/overlay.sample.json ./install/openwrt/install.sh
```

Check service state:

```bash
/etc/init.d/adaptive-ipsec-overlay status
tail -f /var/log/hybrid-overlay-User11.log
```

## MikroTik intermediate install

Edit and import:

```routeros
/import file-name=install-container.rsc
```

See `install/mikrotik/README.md`.

## Build release tarball

```bash
./scripts/build-release-tar.sh
```

The tarball is written to:

```text
dist/adaptive-ipsec-overlay.tar.gz
```

## Security note

The current prototype still uses shared per-node passwords in `overlay.json`.
Do not publish your real lab config. The example configs contain placeholders.
