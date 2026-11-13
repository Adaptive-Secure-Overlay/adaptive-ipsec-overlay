# WireGuard Compatibility Track (Research Notes)

Architectural notes on porting the routed control-plane separation model beyond strongSwan/IPsec into WireGuard-oriented endpoint setups:

- Overlay-assisted session setup flows with role-limited path visibility
- Control-plane/data-plane separation: indirect signaling via `X1`/`X2` relays followed by direct encrypted data channel
- Shared peer policy and configuration envelope model compatible with `core/`
