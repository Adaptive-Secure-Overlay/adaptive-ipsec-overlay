# Adaptive IPsec overlay intermediate node for RouterOS v7 container package.
# Edit variables below before import.
#
# Requirements:
# - RouterOS v7 with container package enabled.
# - A working veth with L3 reachability to other overlay nodes.
# - This mode is an intermediate overlay node only, not a strongSwan endpoint.

:global overlayName "User12"
:global overlayContainerName "adaptive-overlay"
:global overlayImage "python:3.12-alpine"
:global overlayVeth "veth-hybrid"
:global overlayRootDir "/containers/adaptive-overlay"
:global overlayLayerDir "/layers/adaptive-overlay"
:global overlayPackageUrl "https://github.com/OWNER/adaptive-ipsec-overlay/releases/latest/download/adaptive-ipsec-overlay.tar.gz"
:global overlayConfigUrl "https://raw.githubusercontent.com/OWNER/adaptive-ipsec-overlay/main/examples/overlay.12node-lab.example.json"
:global overlayActiveUsers "User1,User2,User3,User4,User5,User6,User7,User8,User9,User10,User11,User12"

/container/stop [find name=$overlayContainerName]
/container/remove [find name=$overlayContainerName]

:local cmd ("sh -c 'python3 -m pip install --no-cache-dir cryptography && " . \
  "mkdir -p /opt/adaptive-ipsec-overlay /etc/adaptive-ipsec-overlay && " . \
  "wget -O /tmp/adaptive-ipsec-overlay.tgz " . $overlayPackageUrl . " && " . \
  "tar xzf /tmp/adaptive-ipsec-overlay.tgz -C /tmp && " . \
  "cp /tmp/adaptive-ipsec-overlay/core/*.py /opt/adaptive-ipsec-overlay/ && " . \
  "mkdir -p /opt/adaptive-ipsec-overlay/scripts && cp /tmp/adaptive-ipsec-overlay/scripts/*.sh /opt/adaptive-ipsec-overlay/scripts/ && " . \
  "wget -O /etc/adaptive-ipsec-overlay/overlay.json " . $overlayConfigUrl . " && " . \
  "cd /opt/adaptive-ipsec-overlay && " . \
  "OVERLAY_CONFIG=/etc/adaptive-ipsec-overlay/overlay.json " . \
  "OVERLAY_ACTIVE_USERS=" . $overlayActiveUsers . " " . \
  "IKE_PRIVACY_OVERLAY=1 IKE_PRIVACY_INLINE=1 PRECONNECT_ENABLED=1 " . \
  "python3 node_daemon.py --name " . $overlayName . "'")

/container/add remote-image=$overlayImage name=$overlayContainerName interface=$overlayVeth \
  root-dir=$overlayRootDir layer-dir=$overlayLayerDir start-on-boot=yes logging=yes cmd=$cmd

/container/start [find name=$overlayContainerName]
:put ("[OK] started " . $overlayContainerName . " as " . $overlayName)
