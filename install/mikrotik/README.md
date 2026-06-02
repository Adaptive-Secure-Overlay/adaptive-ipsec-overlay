# MikroTik intermediate node

RouterOS cannot run the Linux endpoint path directly. This package supports
MikroTik as an intermediate overlay node through RouterOS v7 containers.

It does not install strongSwan inside RouterOS and does not make MikroTik a full
ESP endpoint. The container runs the Python overlay daemon and forwards overlay
control blobs for other nodes.

## Install

1. Install and enable the RouterOS container package.
2. Create/configure a `veth` interface with L3 reachability to the overlay
   network.
3. Edit `install-container.rsc`:
   - `overlayName`
   - `overlayVeth`
   - `overlayPackageUrl`
   - `overlayConfigUrl`
   - `overlayActiveUsers`
4. Import it:

```routeros
/import file-name=install-container.rsc
```

Check logs:

```routeros
/container/print detail
/log/print where topics~"container"
```
