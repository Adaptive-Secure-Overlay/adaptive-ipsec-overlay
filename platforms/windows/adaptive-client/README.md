# Adaptive Windows Client Skeleton

This folder is the Windows EXE/service skeleton for the adaptive IPsec client.
It is intentionally separate from `windows-agent/experiments/manual-sa` and from
the existing `AdaptiveIpsec.Agent` code so workers can develop it without
colliding with the EVE-NG lab work.

## Components

- `AdaptiveClient.Service` reads `config.json`, listens on local TCP port 9000,
  logs structured JSON events, and exposes the service-side control-plane
  commands `status`, `connect`, `rekey`, `cleanup`, `manual-sa-plan`,
  `peer-install`, `endpoint-status`, `endpoint-update`, and `self-test`
  over loopback TCP IPC.
- `AdaptiveClient.Cli` sends those commands to the service.
  It also has a local `aike-key-export` helper for lab live-gates where the
  AIKE-derived ESP keys must be fed into `manual-sa.exe` without going through
  the service IPC yet.
  The local `aike-live-proof` command wraps that lab bridge end-to-end and
  returns the redacted proof report as CLI JSON.
- `ISessionOrchestrator` tracks in-memory sessions and serves `status`, `connect`,
  `rekey`, `cleanup`, `manual-sa-plan`, `peer-install`, `endpoint-status`,
  `endpoint-update`, and `self-test` responses.
- `PersistentIdentityStore` creates or loads a stable local lab identity from
  `identity_store_path`. The local private seed is protected with Windows DPAPI
  (`dpapi-current-user`), the public identity key is derived as `ed25519:<key>`,
  and old placeholder identity files are migrated on service start. This keeps the service-side identity stable across
  `connect`/`rekey` and service restarts without exposing raw private material
  in status or regression reports.
- `EndpointRegistry` is a service-local dynamic endpoint DTO for this dry-run phase.
  It keeps current `EndpointRecord` values in memory, persists runtime
  `endpoint-update` records to `endpoint_registry_path`, prunes expired records,
  and falls back to peer config when the registry has no fresh record.
- `IManualSaRunner` is the service-side abstraction for `manual-sa.exe` calls.
  In `manual_sa_install_mode=validate_only`, `connect`/`rekey` validate plans
  without touching WFP. In `manual_sa_install_mode=local_install`, the service
  writes a temporary plan file, calls `manual-sa.exe install --state <file>
  --plan-file <file>` after validation succeeds, then deletes the temporary
  plan file immediately. Cleanup first tries state-based
  `manual-sa.exe delete --state <file>` for installed sessions, then falls back
  to selector cleanup when needed.
  `manual-sa-plan` now validates via `manual-sa.exe validate-plan` as a dry-run.
- The manual-SA contract is now split:
  - `ipsec_protocol` remains the IPsec layer (`ESP`/`50`),
  - `traffic_selector_protocol` is used for selector matching in `--protocol` (for example `icmp`).
- AIKE Core now uses `X25519HkdfLabAikeCrypto` for the Windows EXE dry-run path:
  - real ephemeral X25519 key exchange;
  - HKDF-SHA256 directional ESP key derivation;
  - DPAPI-backed persistent local identity seed in the service;
  - Ed25519 envelope signing/verification when a real identity key provider and
    pinned `ed25519:<key>` peer descriptor are supplied;
  - lab-only transcript signatures remain only as fallback for old dry-run helpers.
- Service-side `connect`/`rekey` now pass the local DPAPI-backed identity into
  the AIKE export path. The current dry-run can therefore prove local Windows
  Ed25519 signing even when the remote peer still uses the lab fallback until
  peer-side orchestration is wired in.
- `peer-install` is the first peer-side orchestration boundary: it accepts a
  sensitive peer-view install payload over service IPC, builds the peer-side
  `ManualSaInstallPlan`, runs `manual-sa.exe validate-plan` or `local_install`
  according to service config, stores only redacted session state, and returns a
  `peer_install_ack`. The raw key payload is not returned, logged, or checkpointed.
- `PeerInstallEnvelope` wraps that payload with AES-256-GCM. The normal path
  now uses an ephemeral X25519 sender key and the recipient service's
  DPAPI-derived `x25519:<key>` envelope public key, then derives the AES key via
  HKDF-SHA256 over sender id, recipient id, session id, route id, and the
  ephemeral public key. `peer_install_envelope_secret` remains only as a
  compatibility fallback for older PSK-style lab envelopes. The service now
  rejects envelopes addressed to a different local node, rejects unknown senders,
  checks command `session_id`/`route_id` against the envelope, and caches
  accepted envelope nonces to reject replayed `peer-install` payloads. The
  replay cache is persistent (`peer_install_replay_cache_path`), stores only
  SHA-256 replay hashes plus timestamps, and therefore survives service restart.
- `peer_install_delivery_mode: "service_to_service"` turns `connect`/`rekey`
  into a two-service dry-run exchange: the initiator asks the peer service for
  its `peer_install_envelope_public_key`, encrypts the peer-view install payload
  to that key, sends `peer-install` over the overlay control port, and records a
  redacted `peer_install_delivery` ack. When a real pinned Ed25519 key exists in
  `identity_public_keys`, the initiator verifies the peer service's signed
  `identity_proof` before trusting the advertised X25519 envelope key. The
  default remains `"disabled"` so old offline bundle workflows still behave the
  same.
- `config.example.json` defines node identity, peers, public identity keys,
  route policy, retry policy, manual-SA executable path, identity-store path,
  install mode/state directory, validation toggle, and an empty `endpoint_registry`
  section.

## Build

From the repository root:

```powershell
dotnet build .\windows-agent\adaptive-client\AdaptiveClient.Core\AdaptiveClient.Core.csproj --no-restore
dotnet build .\windows-agent\adaptive-client\AdaptiveClient.Service\AdaptiveClient.Service.csproj --no-restore
dotnet build .\windows-agent\adaptive-client\AdaptiveClient.Cli\AdaptiveClient.Cli.csproj --no-restore
```

The Ed25519 implementation currently uses the locally cached
`Portable.BouncyCastle 1.9.0` assembly through a direct project reference, so the
normal `--no-restore` build path does not need NuGet network access.

On environments where global `NuGet.Config` is not readable, build with explicit local values:

```powershell
$env:NUGET_PACKAGES="$PWD\.nuget-packages"
$env:NUGET_CONFIG_FILE=".\windows-agent\NuGet.Config"
dotnet build .\windows-agent\adaptive-client\AdaptiveClient.Core\AdaptiveClient.Core.csproj --no-restore
dotnet build .\windows-agent\adaptive-client\AdaptiveClient.Service\AdaptiveClient.Service.csproj --no-restore
dotnet build .\windows-agent\adaptive-client\AdaptiveClient.Cli\AdaptiveClient.Cli.csproj --no-restore
```

## Regression Automation

Use the regression wrapper after every meaningful Windows EXE change:

```powershell
.\windows-agent\adaptive-client\run-windows-exe-regression.ps1 -Quick
```

`-Quick` does not touch EVE-NG or WFP live state. It builds Core/CLI/Service,
parses the PowerShell proof scripts, checks AIKE directional key agreement in
memory, runs manual-SA validate/cleanup regressions, and scans AIKE reports for
raw-key markers.

Live lab modes require elevated Administrator PowerShell because they use WFP
manual-SA and `pktmon`:

```powershell
.\windows-agent\adaptive-client\run-windows-exe-regression.ps1 -Quick -Live -KeepSa
.\windows-agent\adaptive-client\run-windows-exe-regression.ps1 -Soak -SoakCount 10 -KeepSa
.\windows-agent\adaptive-client\run-windows-exe-regression.ps1 -Checkpoint
```

Additional automation modes:

```powershell
.\windows-agent\adaptive-client\run-windows-exe-regression.ps1 -LivePreflight
.\windows-agent\adaptive-client\run-windows-exe-regression.ps1 -Recover
.\windows-agent\adaptive-client\run-windows-exe-regression.ps1 -ServiceSmoke
.\windows-agent\adaptive-client\run-windows-exe-regression.ps1 -Quick -LocalInstallSmoke -Checkpoint
.\windows-agent\adaptive-client\run-windows-exe-regression.ps1 -DissertationReport
.\windows-agent\adaptive-client\run-windows-exe-regression.ps1 -LiveFull -KeepSa
.\windows-agent\adaptive-client\run-windows-exe-regression.ps1 -Quick -LivePreflight -ServiceSmoke -Checkpoint
```

- `-LivePreflight` checks admin state, required binaries/tools, helper port,
  stale PID files, remote reachability, and optional manual-SA stats.
- `-Recover` stops stale helper processes and runs the Protocol=0 recovery
  cleanup path. It requires elevated Administrator PowerShell.
- `-ServiceSmoke` starts `AdaptiveClient.Service` on an isolated test port,
  runs CLI JSON commands against it in `validate_only`, validates
  `connect -> active manual-sa-plan -> rekey`, verifies `peer_install_bundle`,
  feeds a temporary sensitive payload through `peer-install-envelope`,
  sends only the encrypted envelope to `peer-install`, verifies
  `peer_install_ack`, verifies replay rejection and recipient-mismatch
  rejection, restarts the service and verifies the same envelope is still
  rejected from the persistent replay cache, deletes temporary payload files,
  then starts a second
  service instance and verifies service-to-service encrypted peer delivery
  (`connect -> remote status -> signed identity proof -> x25519 envelope ->
  remote peer-install ack`), then runs `rekey` through the same delivery path
  and `cleanup` through `peer-cleanup`, verifying both service instances
  converge to zero active sessions.
  It also exercises the `maintenance` command so automatic lifecycle scanning
  stays wired into the CLI/service contract.
  It does not require elevated Administrator PowerShell.
- `-LocalInstallSmoke` starts `AdaptiveClient.Service` in `local_install` mode,
  runs `connect`, verifies real local `manual-sa.exe install`, then runs cleanup
  through `delete --state`. It requires elevated Administrator PowerShell and
  uses `-Local`, `-Remote`, `-LocalPeer`, and `-RemotePeer`.
- `-Soak` automatically writes a CSV/Markdown timing summary with min/avg/p50/p95/max.
- `-DissertationReport` turns the latest AIKE Protocol=0 proof into a compact
  Markdown/CSV evidence artifact for the dissertation.
- `-LiveFull` expands to `-Quick -Recover -LivePreflight -Live -DissertationReport -Checkpoint`.
  It is the main full lab proof button and requires elevated Administrator PowerShell.

Reports are written to:

- `windows-agent\adaptive-client\bin\regression\windows-exe-regression-latest.json`
- `windows-agent\adaptive-client\bin\regression\windows-exe-regression-latest.md`
- `windows-agent\adaptive-client\bin\regression\dissertation-evidence-latest.csv`
- `windows-agent\adaptive-client\bin\regression\dissertation-evidence-latest.md`

## Dev Loop Automation

For day-to-day development, use the dev-loop wrapper. It keeps the machine awake
while the run is active, calls the regression wrapper with a named preset, then
writes a compact summary with the failed step, checkpoint hash, service identity
proof status, and the next suggested gate:

```powershell
.\windows-agent\adaptive-client\run-windows-exe-dev-loop.ps1 -Mode Fast
.\windows-agent\adaptive-client\run-windows-exe-dev-loop.ps1 -Mode Service
.\windows-agent\adaptive-client\run-windows-exe-dev-loop.ps1 -Mode Checkpoint
.\windows-agent\adaptive-client\run-windows-exe-dev-loop.ps1 -Mode LiveGate
```

Presets:

- `Fast` runs `-Quick`.
- `Service` runs `-Quick -ServiceSmoke -DissertationReport`.
- `Checkpoint` runs `-Quick -ServiceSmoke -DissertationReport -Checkpoint`.
- `LiveGate` runs `-LivePreflight`.
- `LocalInstall` runs `-Quick -LocalInstallSmoke -Checkpoint` and requires an
  elevated Administrator PowerShell.
- `LiveFull` runs `-LiveFull`; use it only when lab access is intentionally
  enabled.

If `LiveGate` fails on Windows11-U13 reachability, run the no-write diagnostic
before spending time on the full proof:

```powershell
.\windows-agent\adaptive-client\diagnose-u13-live-gate.ps1
.\windows-agent\adaptive-client\diagnose-u13-live-gate.ps1 -Json
```

The diagnostic records ICMP reachability, route lookup, route print output,
candidate lab gateways, and helper-port state under
`windows-agent\adaptive-client\bin\regression\u13-live-gate-diagnostic-*.json`.

Dev-loop summaries are written to:

- `windows-agent\adaptive-client\bin\regression\windows-exe-dev-loop-latest.json`
- `windows-agent\adaptive-client\bin\regression\windows-exe-dev-loop-latest.md`

## Live Service Lifecycle Automation

For the host `<->` Windows11-U13 service path, use the lifecycle orchestrator
instead of manually repeating build, package, U13 upload, service start,
connect, ping, cleanup, and log collection:

```powershell
.\windows-agent\adaptive-client\run-live-service-lifecycle.ps1 -PrepareOnly
.\windows-agent\adaptive-client\run-live-service-lifecycle-elevated.ps1
```

Before a full live run, the preferred one-button lab preparation command is:

```powershell
.\windows-agent\adaptive-client\ensure-u13-live-automation.ps1 -AutoEve -RestartU13OnAgentFailure
```

It stops stale helper servers, checks the U13 path, repairs/replaces the
Windows11-U13 polling agent through the already installed pull-agent, restarts
U13 through the EVE API if the agent is wedged, and runs a debug job to prove
that the repaired agent is polling again. Use `-RunLiveLifecycle` when the same
command should immediately continue into the elevated live lifecycle.

`-PrepareOnly` never touches U13/EVE live state. It builds Service/CLI, creates
the portable service package with the bundled .NET runtime, checks local network
preflight, and writes a report.

The elevated wrapper is the normal full lab button. It starts the U13 job through
the existing polling agent, opens the host service, runs `connect`, verifies ping
through the installed SA, and runs cleanup unless `-KeepSa` is set. If U13
deployment or port readiness fails, the orchestrator automatically runs
`u13-collect-adaptive-service-status.ps1` and attaches the returned log tail to
the report.

Lifecycle reports are written to:

- `windows-agent\adaptive-client\bin\live-service-lifecycle\live-service-lifecycle-latest.json`
- `windows-agent\adaptive-client\bin\live-service-lifecycle\live-service-lifecycle-latest.md`

U13 lab automation reports are written to:

- `windows-agent\adaptive-client\bin\live-automation\ensure-u13-live-automation-latest.json`
- `windows-agent\adaptive-client\bin\live-automation\ensure-u13-live-automation-latest.md`

When only status is needed, avoid rerunning tests:

```powershell
.\windows-agent\adaptive-client\show-windows-exe-dev-status.ps1
.\windows-agent\adaptive-client\show-windows-exe-dev-status.ps1 -Json
```

## Run

Copy or adapt `config.example.json` to `config.json`, then start the service:

```powershell
dotnet run --project .\windows-agent\adaptive-client\AdaptiveClient.Service -- --config .\windows-agent\adaptive-client\config.json --log .\windows-agent\adaptive-client\service-events.jsonl
```

Manual-SA install modes:

- `manual_sa_install_mode: "validate_only"` is the default. The service runs
  `manual-sa.exe validate-plan` and stores only a redacted in-memory session.
  This is the normal smoke/regression mode and does not require Administrator.
- `manual_sa_install_mode: "local_install"` runs the same validation first, then
  writes a temporary `<peer>-<session>.plan`, writes `<peer>-<session>.state`
  under `manual_sa_state_dir`, and executes the Windows-safe two-step WFP flow:
  `manual-sa.exe create --state ...` lets Windows reserve the local inbound SPI,
  then `manual-sa.exe complete --peer-spi ...` adds the outbound SA. The
  temporary plan file is deleted immediately after the install attempt. This
  changes Windows WFP/manual-SA state and requires an elevated Administrator
  PowerShell.
- `manual_sa_exempt_tcp_ports` is a comma-separated bypass list passed to
  `manual-sa.exe create --exempt-tcp-ports ...`. It is normally empty for ICMP
  tests, but should include the service ports for `Protocol=0` live lifecycle
  runs so the control-plane remains clear while the ESP data-plane is installed.
- `manual_sa_session_index_path` stores the durable local-install session index.
  On service startup in `local_install` mode, stale indexed state is cleaned with
  `delete --state` first and selector cleanup as fallback. Rekey also removes the
  previous installed session after the replacement session is ready.
- `peer_lifecycle_delivery_required` is automatically `true` when
  `peer_install_delivery_mode` is `service_to_service` unless the config
  explicitly disables it. In required mode, `connect`/`rekey` are end-to-end
  lifecycle operations: local validation/install plus peer-side encrypted
  `peer-install` ack. A missing peer ack fails the command and rolls back the new
  local install instead of leaving a half-built session.
- `listen_host` defaults to `127.0.0.1`. Keep that for local CLI/dev smoke.
  For real host-to-host service lifecycle tests, set it to the node IP or
  `0.0.0.0` and allow the selected control-plane port in Windows Firewall.
- `maintenance` controls the background lifecycle tick. When enabled, the
  service periodically scans active sessions, triggers rekey for sessions past
  `rekey_at`, and cleans expired sessions past `not_after`. The same scan is
  available through `AdaptiveClient.Cli maintenance --json` for diagnostics and
  regression tests.
- Relative `manual_sa_path`, `manual_sa_state_dir`,
  `manual_sa_session_index_path`, `identity_store_path`, and
  `endpoint_registry_path` values are resolved from the config file directory.

In another terminal:

```powershell
dotnet run --project .\windows-agent\adaptive-client\AdaptiveClient.Cli -- status --config .\windows-agent\adaptive-client\config.example.json
dotnet run --project .\windows-agent\adaptive-client\AdaptiveClient.Cli -- connect User1 --config .\windows-agent\adaptive-client\config.example.json
dotnet run --project .\windows-agent\adaptive-client\AdaptiveClient.Cli -- rekey User1 --config .\windows-agent\adaptive-client\config.example.json
dotnet run --project .\windows-agent\adaptive-client\AdaptiveClient.Cli -- cleanup User1 --config .\windows-agent\adaptive-client\config.example.json
dotnet run --project .\windows-agent\adaptive-client\AdaptiveClient.Cli -- maintenance --config .\windows-agent\adaptive-client\config.example.json
dotnet run --project .\windows-agent\adaptive-client\AdaptiveClient.Cli -- manual-sa-plan User1 --config .\windows-agent\adaptive-client\config.example.json
dotnet run --project .\windows-agent\adaptive-client\AdaptiveClient.Cli -- self-test --config .\windows-agent\adaptive-client\config.example.json
dotnet run --project .\windows-agent\adaptive-client\AdaptiveClient.Cli -- self-test --json --config .\windows-agent\adaptive-client\config.example.json
dotnet run --project .\windows-agent\adaptive-client\AdaptiveClient.Cli -- manual-sa-plan User1 --json --config .\windows-agent\adaptive-client\config.example.json
dotnet run --project .\windows-agent\adaptive-client\AdaptiveClient.Cli -- endpoint-status --json --config .\windows-agent\adaptive-client\config.example.json
dotnet run --project .\windows-agent\adaptive-client\AdaptiveClient.Cli -- endpoint-update User1 192.168.101.20 --selector icmp --json --config .\windows-agent\adaptive-client\config.example.json
dotnet run --project .\windows-agent\adaptive-client\AdaptiveClient.Cli -- aike-key-export --local 192.168.95.1 --remote 192.168.113.199 --local-peer HostWindows --remote-peer Windows11-U13 --json
dotnet run --project .\windows-agent\adaptive-client\AdaptiveClient.Cli -- aike-live-proof --config .\windows-agent\adaptive-client\config.example.json --local 192.168.95.1 --remote 192.168.113.199 --local-peer HostWindows --remote-peer Windows11-U13 --helper-listen-host 192.168.95.1 --helper-port 18091 --agent-node-id Windows11-U13 --control-plane-ports 18091,9000 --tcp-port 18101 --udp-port 18102 --timeout-seconds 180 --keep-sa --json

# invalid usage or service/network errors with --json

# explicit override takes precedence over config
dotnet run --project .\windows-agent\adaptive-client\AdaptiveClient.Cli -- status --port 9000
```

## Orchestration (Core + manual-sa)

Current flow in this task:

1. CLI sends line-oriented or JSON commands over loopback TCP (`status`,
   `connect <peer>`, `rekey <peer>`, `cleanup <peer>`, `manual-sa-plan <peer>`,
   `peer-install <peer> --payload-file <payload.json>`, `endpoint-status`,
   `endpoint-update <node_id> <ip>`, `self-test`) to
   `127.0.0.1:PORT` (9000 by default).
2. Service parses the command in `ControlPlaneServer`.
3. `connect <peer>` resolves current local/remote endpoints, loads or creates
   persistent local identity, runs the AIKE X25519/HKDF key export path with the
   local DPAPI-backed Ed25519 identity provider, builds a `ManualSaInstallPlan`,
   validates it through `manual-sa.exe validate-plan`, then either stops at
   validation (`validate_only`) or runs local
   `manual-sa.exe install --state <state-file> --plan-file <plan-file>`
   (`local_install`).
4. `rekey <peer>` repeats the same lifecycle with new session id, route id, SPI,
   X25519 material, transcript hash, and ESP keys. In `local_install` mode the
   old installed state is removed only after the replacement session reaches the
   installed lifecycle state.
5. Session state is kept in memory (`peer_id -> session_id`), and `local_install`
   sessions are mirrored to `manual_sa_session_index_path` so service restart can
   clean stale manual-SA state. `status` returns lifecycle state, `not_after`,
   `rekey_at`, endpoint sources, identity signature mode, SPI summary, and
   redacted key lengths. When `local_install` is used it also returns state path,
   plan-file deletion status, install exit code, install error, and redacted
   stdout summary, never raw ESP keys.
6. `manual-sa-plan <peer>` returns the active validated service plan when one
   exists; otherwise it falls back to a dry-run plan for inspection.
7. `CleanupPeer(peerId)` uses the active session state path first when present:
   `manual-sa.exe delete --state <state-file>`. If state cleanup is not available
   or fails, it falls back to selector cleanup:
   `manual-sa.exe cleanup --local <local_ip> --remote <remote_ip> --protocol <selector>`.
   The temporary plan-file is deleted best-effort, and the in-memory session is
   removed after cleanup has been launched.
   If the service restarted and no in-memory session exists, cleanup still runs
   against the configured/resolved peer endpoints.
8. `self-test` runs `AikeMessageCodecSmoke.RunEstablishedAndDeriveEspMaterial()` first.
   This path now uses X25519/HKDF-derived ESP material, not deterministic placeholder keys.
   On success it also runs dual-endpoint Ed25519 manual-SA export smoke, where
   KE1, KE2, and InstallAck must all report pure `Ed25519` signature mode.
   On fallback it switches to `RunKe1Ke2EstablishedRoundTrip()` and reports a warning.
9. `endpoint-update <node_id> <ip> [--selector icmp]` writes an in-memory
   `EndpointRecord` with `node_id`, `ip`, optional `port`, `selector_protocol`,
   `source`, `sequence`, `valid_from`, `expires_at`, and placeholder `signature`.
   Runtime updates are persisted to `endpoint_registry_path`, so dynamic peer IP
   changes survive service restart until TTL expiry.
10. Fallback `manual-sa-plan <peer>` resolves local and peer endpoints from the registry
   first, then falls back to `peers.*.overlay_endpoint`/`endpoint` from config.
   The response includes endpoint source/sequence/expiry for both local and remote.
11. Fallback `manual-sa-plan <peer>` builds a dry-run Core-plan-informed contract, then by default
   runs `manual-sa.exe validate-plan ...` (validate-only) and returns:
   `dry_run=true`, `validate_only=true`, `not_executed_install=true`,
   `ipsec_protocol` and `traffic_selector_protocol`, plus `manual_sa_validate` with
   `success/exit_code/duration_ms/stdout_summary` in response.
   Relative path config values are resolved from the config file directory, so
   `config.example.json` can be launched from the repository root or from
   `windows-agent/adaptive-client`.
12. `peer-install <peer> --payload-file <payload.json>` accepts the peer-view
   side of the AIKE-derived material. In the normal dry-run path this file is an
   encrypted `PeerInstallEnvelope` produced by `peer-install-envelope`; the
   transient plaintext payload is deleted immediately after encryption. The
   preferred envelope mode is `x25519`: the CLI encrypts to the recipient
   service's `peer_install_envelope_public_key`, and the service decrypts with
   its DPAPI-derived local private key. The service then validates or locally
   installs according to `manual_sa_install_mode` and returns `peer_install_ack`
   with status, SPI summary, endpoint summary, validation/install result,
   `payload_transport=encrypted_envelope`, `payload_envelope_mode=x25519`, and
   `raw_keys_logged=false`. Source guards reject recipient mismatch, route/session
   mismatch, unknown sender, and replayed accepted envelope nonces. The replay
   cache is written to `peer_install_replay_cache_path`; entries expire after
   two hours and are stored as hashes, not raw envelope/session strings.
13. `peer_install_delivery_mode: "service_to_service"` makes `connect` and
   `rekey` deliver that encrypted peer-view payload directly to the peer service
   through `peers.<peer>.overlay_endpoint`. Before sending the sensitive bundle,
   the initiator requests `status`, verifies the peer's signed `identity_proof`
   when a real pinned Ed25519 key is configured, and only then accepts the
   advertised `peer_install_envelope_public_key`. In this mode
   `peer_lifecycle_delivery_required` defaults to `true`: the command succeeds
   only after the peer returns an accepted `peer_install_ack`. If peer delivery
   fails, the new local install is rolled back and no new session is stored. The
   command response includes `end_to_end_lifecycle_state`,
   `peer_install_delivery.attempted`, `success`, `ack_status`,
   `payload_transport`, `envelope_mode`, and `identity_proof_status`; raw keys
   and plaintext payloads are not returned.
14. In `service_to_service` mode, `cleanup <peer>` also sends an internal
   `peer-cleanup` command to the peer service after local cleanup succeeds. With
   required peer lifecycle delivery, cleanup reports failure if that peer-side
   cleanup ack is not received. In `validate_only`, cleanup does not touch WFP
   because no local manual-SA was installed; it removes service session state and
   reports `cleanup_method=validate_only_session_cleanup`.
15. `validate_manual_sa_plan` in config (default `true`) can disable validate-only.

Endpoint registry config is intentionally empty by default so the fallback path is
exercised until runtime updates arrive:

```json
{
  "endpoint_registry": {
    "default_ttl_seconds": 300,
    "records": {}
  }
}
```

Migration note: there are no Core dynamic endpoint models in this branch yet, so
`EndpointRecord` is service-local for now and shaped to match the future Core
contract.

Service responses are machine-readable JSON for all commands. CLI `--json` wraps
responses into a stable contract with `success`, `command`, `error`, and `data`:

```json
{"success": true, "command": "status", "error": null, "data": {...}}
{"success": false, "command": "unknown_command", "error": {"code":"cli.unknown_command","message":"Unknown command: unknown"}, "data": null}
{"success": true, "command": "self-test", "error": null, "data": {"smoke_method":"RunEstablishedAndDeriveEspMaterial","smoke_status":"pass","identity_signature_smoke":"ed25519_pass","dual_identity_export_smoke":"Ed25519","final_state":"Established","material_summary":{"protocol":"50","inbound_spi":41216,"outbound_spi":45568}}}
{"success": true, "command": "endpoint-update", "error": null, "data": {"updated":{"node_id":"User1","ip":"192.168.101.20","port":null,"selector_protocol":"icmp","endpoint_source":"cli_runtime","endpoint_sequence":1,"endpoint_expires_at":"2026-05-24T...Z","signature":"placeholder","is_fallback":false,"is_expired":false}}}
{"success": true, "command": "connect", "error": null, "data": {"action":"connect_validated","peer_id":"User1","session_id":"...","lifecycle_state":"install_validated","install_mode":"validate_only","manual_sa_validate":{"success":true,"exit_code":0},"peer_install_bundle":{"peer_id":"User1","session_id":"...","route_id":"...","local_ip":"192.168.101.20","remote_ip":"192.168.113.199","inbound_spi":45568,"outbound_spi":41216,"traffic_selector_protocol":"icmp","install_mode":"peer_external_orchestration","plan_transfer":"encrypted_control_plane_required","key_lengths":{"in_cipher_key_length":16,"in_auth_key_length":32,"out_cipher_key_length":16,"out_auth_key_length":32}}}}
{"success": true, "command": "connect", "error": null, "data": {"end_to_end_lifecycle_state":"end_to_end_install_validated","peer_lifecycle_delivery_required":true,"peer_install_delivery":{"mode":"service_to_service","attempted":true,"success":true,"endpoint":"127.0.0.1:19021","ack_status":"accepted","payload_transport":"encrypted_envelope","envelope_mode":"x25519"}}}
{"success": true, "command": "rekey", "error": null, "data": {"action":"rekey_validated","peer_id":"User1","previous_session_id":"...","session_id":"...","lifecycle_state":"install_validated","install_mode":"validate_only"}}
{"success": true, "command": "cleanup", "error": null, "data": {"action":"cleanup_skipped","cleanup_method":"validate_only_session_cleanup","peer_cleanup_delivery":{"mode":"service_to_service","attempted":true,"success":true,"action":"cleanup_skipped"}}}
{"success": true, "command": "peer-install", "error": null, "data": {"action":"peer_install_validated","peer_id":"User1","remote_peer_id":"Windows11-U13","session_id":"...","lifecycle_state":"peer_install_validated","payload_transport":"encrypted_envelope","payload_envelope_mode":"x25519","manual_sa_validate":{"success":true,"exit_code":0},"peer_install_ack":{"ack_type":"peer_install_ack","status":"accepted","validated":true,"installed":false,"raw_keys_logged":false,"payload_transport":"encrypted_envelope","envelope_mode":"x25519"}}}
{"success": true, "command": "manual-sa-plan", "error": null, "data": {"peer_id":"User1","dry_run":true,"validate_only":true,"not_executed_install":true,"session_id":"dry-run-...","manual_sa_path":"..\\experiments\\manual-sa\\bin\\manual-sa.exe","ipsec_protocol":"50","traffic_selector_protocol":"icmp","endpoints":{"local":{"node_id":"Windows11-U13","ip":"192.168.113.199","endpoint_source":"config_peer_endpoint","endpoint_sequence":0,"endpoint_expires_at":null,"is_fallback":true},"remote":{"node_id":"User1","ip":"192.168.101.20","endpoint_source":"cli_runtime","endpoint_sequence":1,"endpoint_expires_at":"2026-05-24T...Z","is_fallback":false}},"material_summary":{"local_ip":"192.168.113.199","remote_ip":"192.168.101.20","ipsec_protocol":"50","traffic_selector_protocol":"icmp","inbound_spi":41216,"outbound_spi":41217,"inbound_cipher_key_length":16,"inbound_auth_key_length":32,"outbound_cipher_key_length":16,"outbound_auth_key_length":32},"install_command":{"executable":"..\\experiments\\manual-sa\\bin\\manual-sa.exe","arguments":["--peer","User1","--session-id","dry-run-...","--direction","install","--protocol","icmp","--ipsec-protocol","50","--local-ip","192.168.113.199","--remote-ip","192.168.101.20","--inbound-spi","41216","--outbound-spi","41217"],"validate_only":true,"not_executed":true,"not_executed_install":true},"manual_sa_validate":{"success":true,"exit_code":0,"duration_ms":10,"stdout_summary":"..."}}}
{"success": false, "command": "self-test", "error": {"code":"cli.service_unreachable","message":"Service is not reachable on 127.0.0.1:9000: ..."}, "data": null}
```

`connect`, `rekey`, and `manual-sa-plan` include `peer_install_bundle`. It is a
peer-side orchestration contract only: peer id, session id, route id, peer-view
local/remote IPs, peer-view inbound/outbound SPIs, traffic selector protocol,
`install_mode=peer_external_orchestration`, `plan_transfer=encrypted_control_plane_required`,
and redacted key lengths. It does not include raw peer keys; any future peer key
transfer must happen over an encrypted control plane. The current `peer-install`
command is the receiving-side boundary for that future transfer: it accepts an
X25519/HKDF/AES-GCM encrypted envelope, performs validate/install, and emits only
a redacted ack.

The service-side install lifecycle now has two modes. The default
`validate_only` boundary is: `connect/rekey command -> persistent identity ->
AIKE KE1/KE2 -> X25519/HKDF ESP keys -> ManualSaInstallPlan -> manual-sa
validate-plan -> active redacted session in status`. `local_install` extends that
boundary with `manual-sa install --state <state-file> --plan-file <plan-file>` on
the local Windows host after validation succeeds. The raw plan file is transient
and deleted after the install attempt; the `manual-sa` state file remains
sensitive and is used for `delete --state` until the future DPAPI-backed state
store replaces the lab state format.

For the live Windows host/U13 lab, the temporary bridge is:
`AIKE KE1/KE2 -> X25519/HKDF directional keys -> run-aike-protocol0-proof-suite.ps1 -> manual-SA Protocol=0 -> ESP pktmon proof`.
The bridge writes only redacted AIKE proof reports by default; temporary raw key
files are removed after the run.

The current EXE/CLI live entry point is:

```powershell
.\windows-agent\adaptive-client\AdaptiveClient.Cli\bin\Debug\net9.0\AdaptiveClient.Cli.exe aike-live-proof `
  --config .\windows-agent\adaptive-client\config.example.json `
  --local 192.168.95.1 --remote 192.168.113.199 `
  --local-peer HostWindows --remote-peer Windows11-U13 `
  --helper-listen-host 192.168.95.1 --helper-port 18091 `
  --agent-node-id Windows11-U13 --control-plane-ports 18091,9000 `
  --tcp-port 18101 --udp-port 18102 --timeout-seconds 180 --keep-sa --json
```

It shells into the proof-suite for now, but its output is already a stable
machine-readable CLI response with the redacted AIKE session, transcript hash,
SPI values, ESP counters, and control-plane bypass counters.
The wrapper enforces a timeout and fails closed if the final report/stdout
contains forbidden raw-key markers such as `in_cipher_key` or `out_auth_key`.

## TODO

- Wire peer-side orchestration into the same Ed25519 identity model so both
  sides sign the live exchange with pinned identities instead of using the
  current remote lab fallback in dry-run.
- Expand registry persistence and cleanup timeouts.
- Add tests for command dispatch and session lifecycle.
- Add Windows Service install/uninstall scripts after the core flow is stable.
- Keep this phase CLI + service-only: no GUI and no installer included.
