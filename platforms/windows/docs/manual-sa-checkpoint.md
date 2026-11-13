# Manual-SA Checkpoint

This note captures the key point already reached in the Windows branch:

- the no-custom-driver path was validated at the local API layer;
- Windows accepted manually installed transport-mode ESP Security Associations through WFP/IPsec APIs;
- the branch already moved beyond a purely theoretical Windows track.

## Why it matters

The Windows repository is not published as a finished product, but it is also not just a placeholder anymore. The manual-SA checkpoint is the first hard proof that the Windows platform can participate in the project through a native IPsec/WFP path without inventing a fake overlay tunnel.

## Scope of the checkpoint

The checkpoint proves viability of the manual-SA layer and the service/control-plane branch around it. It does not claim that the final end-user host-to-host Windows flow is already complete.
