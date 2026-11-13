# Windows Status

This repository is the current public Windows implementation branch for the Adaptive Secure Overlay IPsec track.

## Milestones

| Area | State | Notes |
| --- | --- | --- |
| Service/CLI skeleton | done | `AdaptiveClient.Core`, `AdaptiveClient.Service` and `AdaptiveClient.Cli` are published in this repository. |
| Local identity persistence | done | DPAPI-backed identity storage exists in the current prototype path. |
| AIKE-style dry-run exchange | partial | X25519/HKDF and signed-envelope plumbing are present in the research path. |
| Peer orchestration | partial | Encrypted peer-install envelope flow exists for the service-to-service dry-run path. |
| manual-SA planning | done | Planning and validation are wired into the current branch. |
| manual-SA install path | partial | Local install path exists; public branch is still not framed as a one-click end-user flow. |
| Stable host-to-host Windows proof | partial | Windows manual-SA viability is proven locally; final polished end-to-end product flow is still pending. |
| Release packaging | not yet | No finished installer, tray UX or public release-grade packaging yet. |

## What is already useful

- this is a real implementation branch, not just an idea page;
- the Windows service path has enough substance to show architecture, code structure and current milestones;
- the branch already demonstrates that Windows can participate in the project through a native service plus manual-SA path.

## What comes next

1. tighten the peer-side lifecycle into a cleaner connect/rekey/cleanup story;
2. improve public proof around stable end-to-end Windows flow;
3. package the branch into a more user-facing client form.
