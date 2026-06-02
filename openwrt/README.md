# OpenWRT profile

OpenWRT profile runs the same endpoint/intermediate overlay logic on an
OpenWRT router with strongSwan.

## What it installs

- Python overlay daemon.
- nftables IKE capture rules.
- OpenWRT init.d service.
- strongSwan restart/start integration.

## Install

```bash
NODE_NAME=User11 CONFIG_SOURCE=../examples/overlay.sample.json ./install.sh
```

## Check

```bash
/etc/init.d/adaptive-ipsec-overlay status
tail -f /var/log/hybrid-overlay-User11.log
```
