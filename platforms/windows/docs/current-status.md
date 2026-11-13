# Windows Track Status

This repository contains the current public Windows research branch for Adaptive Secure Overlay IPsec.

## Confirmed milestones

- Windows service and CLI skeleton exist and build as a separate branch of the project.
- The dry-run exchange path already includes X25519/HKDF-based session derivation and signed-envelope plumbing.
- The service layer already handles persistent local identity with DPAPI-backed storage.
- manual-SA planning, validation and local install flows are wired into the current prototype path.
- Service-to-service peer-install exchange is already modeled through encrypted envelopes.
- Regression wrappers, checkpoint generation and live-gate preparation are already part of the Windows development loop.

## Important limitation

This is still a research prototype. The repository does not yet claim a finished install-and-use Windows VPN client.

## Data-plane note

The current public evidence for Windows focuses on viability of the manual-SA path and the service/control-plane branch around it. That is enough to justify the repository as an implementation track, but not enough to call it production-ready.
