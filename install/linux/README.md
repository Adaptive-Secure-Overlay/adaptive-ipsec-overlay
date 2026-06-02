# Linux profile

Linux profile installs the overlay as a strongSwan-aware endpoint and
intermediate node.

## What it installs

- Python overlay daemon.
- IKE UDP/500 and IKE-over-UDP/4500 capture through nftables.
- XFRM bypass rules for overlay UDP/9000 traffic.
- systemd template service.

## Install

```bash
sudo NODE_NAME=User1 CONFIG_SOURCE=../../examples/overlay.sample.json ./install.sh
```

## Check

```bash
systemctl status adaptive-ipsec-overlay@User1
tail -f /var/log/hybrid-overlay-User1.log
```
