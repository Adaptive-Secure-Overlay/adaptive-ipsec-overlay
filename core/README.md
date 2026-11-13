# Adaptive Control-Plane Core

Platform-independent Python core for the routed IKE control-plane and role-separated overlay daemon.

## Modules

- `protocol.py` — overlay framing and routed message envelopes
- `crypto_util.py` — cryptographic helpers for hop/session protection
- `config.py` — node/lab configuration loader (`examples/overlay.sample.json`)
- `ike_proxy.py` — local IKE UDP/500 & NAT-T UDP/4500 capture/injection bridge
- `node_daemon.py` — endpoint and intermediate relay daemon (`X1`/`X2` hop forwarding)
- `client.py` — CLI/control entrypoint for overlay session setup
- `logging_util.py` — structured diagnostic logging
