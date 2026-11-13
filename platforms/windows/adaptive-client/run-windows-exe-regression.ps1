param(
    [switch] $Quick,
    [switch] $Live,
    [switch] $Soak,
    [switch] $Checkpoint,
    [switch] $LivePreflight,
    [switch] $Recover,
    [switch] $ServiceSmoke,
    [switch] $LocalInstallSmoke,
    [switch] $SoakReport,
    [switch] $LiveFull,
    [switch] $DissertationReport,
    [int] $SoakCount = 10,
    [string] $Local = "192.168.95.1",
    [string] $Remote = "192.168.113.199",
    [string] $LocalPeer = "HostWindows",
    [string] $RemotePeer = "Windows11-U13",
    [string] $AgentNodeId = "Windows11-U13",
    [string] $HelperListenHost = "",
    [int] $HelperPort = 18091,
    [string] $ControlPlanePorts = "18091,9000",
    [int] $TcpPort = 18101,
    [int] $UdpPort = 18102,
    [int] $WaitSeconds = 120,
    [int] $TimeoutSeconds = 180,
    [int] $ServicePort = 19000,
    [string] $RecoveryProtocol = "0",
    [switch] $KeepSa,
    [switch] $ContinueOnError
)

$ErrorActionPreference = "Stop"

if (-not $Quick -and -not $Live -and -not $Soak -and -not $Checkpoint -and -not $LivePreflight -and -not $Recover -and -not $ServiceSmoke -and -not $LocalInstallSmoke -and -not $LiveFull -and -not $DissertationReport)
{
    $Quick = $true
}

if ($LiveFull)
{
    $Quick = $true
    $Recover = $true
    $LivePreflight = $true
    $Live = $true
    $DissertationReport = $true
    $Checkpoint = $true
}

if ([string]::IsNullOrWhiteSpace($HelperListenHost))
{
    $HelperListenHost = $Local
}

if ($Soak)
{
    $SoakReport = $true
    $DissertationReport = $true
}

$Script:StartedAt = Get-Date
$RunId = $Script:StartedAt.ToString("yyyyMMdd-HHmmss")
$ClientDir = $PSScriptRoot
$WorkspaceRoot = [System.IO.Path]::GetFullPath((Join-Path $ClientDir "..\.."))
$ManualSaDir = Join-Path $WorkspaceRoot "windows-agent\experiments\manual-sa"
$ReportDir = Join-Path $ClientDir "bin\regression"
$CheckpointsDir = Join-Path $WorkspaceRoot "checkpoints"
$ConfigPath = Join-Path $ClientDir "config.example.json"
$CliProject = Join-Path $ClientDir "AdaptiveClient.Cli\AdaptiveClient.Cli.csproj"
$CoreProject = Join-Path $ClientDir "AdaptiveClient.Core\AdaptiveClient.Core.csproj"
$ServiceProject = Join-Path $ClientDir "AdaptiveClient.Service\AdaptiveClient.Service.csproj"
$CliExe = Join-Path $ClientDir "AdaptiveClient.Cli\bin\Debug\net9.0\AdaptiveClient.Cli.exe"
$ManualSaExe = Join-Path $ManualSaDir "bin\manual-sa.exe"
$Script:Steps = New-Object System.Collections.Generic.List[object]
$Script:Failures = New-Object System.Collections.Generic.List[object]
$Script:SoakResults = New-Object System.Collections.Generic.List[object]
$Script:Artifacts = New-Object System.Collections.Generic.List[object]
$PeerInstallEnvelopeSecret = "service-smoke-peer-install-envelope-secret-v1"

$ForbiddenSecretMarkers = @(
    '"in_cipher_key"',
    '"in_auth_key"',
    '"out_cipher_key"',
    '"out_auth_key"',
    '"protected_private_seed"',
    'in_cipher_key=',
    'in_auth_key=',
    'out_cipher_key=',
    'out_auth_key='
)

New-Item -ItemType Directory -Force -Path $ReportDir | Out-Null
New-Item -ItemType Directory -Force -Path $CheckpointsDir | Out-Null

function Test-IsAdmin
{
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-TextTail
{
    param(
        [AllowNull()] [object] $Text,
        [int] $Lines = 40
    )

    if ($null -eq $Text)
    {
        return ""
    }

    $joined = if ($Text -is [array]) { ($Text -join "`n") } else { [string] $Text }
    if ([string]::IsNullOrWhiteSpace($joined))
    {
        return ""
    }

    $all = $joined -replace "`r`n", "`n" -split "`n"
    return ($all | Select-Object -Last $Lines) -join "`n"
}

function Invoke-RegressionStep
{
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [scriptblock] $Body,
        [switch] $SensitiveOutput
    )

    Write-Host "==> $Name"
    $sw = [System.Diagnostics.Stopwatch]::StartNew()

    try
    {
        $result = & $Body
        $sw.Stop()
        $record = [pscustomobject]@{
            name = $Name
            ok = $true
            duration_ms = $sw.ElapsedMilliseconds
            result = $result
        }
        $Script:Steps.Add($record)
        Write-Host "PASS $Name ($($sw.ElapsedMilliseconds) ms)"
        return $result
    }
    catch
    {
        $sw.Stop()
        $message = $_.Exception.Message
        $record = [pscustomobject]@{
            name = $Name
            ok = $false
            duration_ms = $sw.ElapsedMilliseconds
            error = $message
            result = $null
        }
        $Script:Steps.Add($record)
        $Script:Failures.Add($record)
        Write-Host "FAIL $Name ($($sw.ElapsedMilliseconds) ms): $message"

        if (-not $ContinueOnError)
        {
            throw
        }

        return $null
    }
}

function Invoke-NativeCommand
{
    param(
        [Parameter(Mandatory)] [string] $File,
        [Parameter()] [string[]] $Arguments = @(),
        [switch] $SuppressOutput
    )

    $output = & $File @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0)
    {
        throw "$File exited with $exitCode. Output tail: $(Get-TextTail $output 20)"
    }

    return [pscustomobject]@{
        exit_code = $exitCode
        output_tail = if ($SuppressOutput) { "<suppressed>" } else { Get-TextTail $output 30 }
    }
}

function Test-PowerShellSyntax
{
    param([Parameter(Mandatory)] [string[]] $Paths)

    $items = @()
    foreach ($path in $Paths)
    {
        $tokens = $null
        $errors = $null
        [System.Management.Automation.Language.Parser]::ParseFile($path, [ref] $tokens, [ref] $errors) | Out-Null
        if ($errors.Count -gt 0)
        {
            $details = ($errors | ForEach-Object { "$($_.Extent.StartLineNumber):$($_.Extent.StartColumnNumber) $($_.Message)" }) -join "; "
            throw "PowerShell syntax error in ${path}: $details"
        }

        $items += [pscustomobject]@{
            path = $path
            ok = $true
        }
    }

    return $items
}

function Test-AikeKeyExport
{
    $args = @(
        "aike-key-export",
        "--local", $Local,
        "--remote", $Remote,
        "--local-peer", $LocalPeer,
        "--remote-peer", $RemotePeer,
        "--json"
    )

    $jsonText = & $CliExe @args 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0)
    {
        throw "aike-key-export failed with $exitCode. Output tail: $(Get-TextTail $jsonText 20)"
    }

    $parsed = ($jsonText -join "`n") | ConvertFrom-Json
    if (-not $parsed.success)
    {
        throw "aike-key-export returned success=false"
    }

    $data = $parsed.data
    $errors = @()
    if ($data.host.out_cipher_key -ne $data.peer.in_cipher_key) { $errors += "host.out_cipher != peer.in_cipher" }
    if ($data.host.out_auth_key -ne $data.peer.in_auth_key) { $errors += "host.out_auth != peer.in_auth" }
    if ($data.host.in_cipher_key -ne $data.peer.out_cipher_key) { $errors += "host.in_cipher != peer.out_cipher" }
    if ($data.host.in_auth_key -ne $data.peer.out_auth_key) { $errors += "host.in_auth != peer.out_auth" }
    if ($errors.Count -gt 0)
    {
        throw ($errors -join "; ")
    }

    return [pscustomobject]@{
        session_id = $data.session_id
        route_id = $data.route_id
        transcript_hash = $data.transcript_hash
        esp_profile = $data.esp_profile
        host_peer_id = $data.host_peer_id
        peer_peer_id = $data.peer_peer_id
        host_ip = $data.host_ip
        peer_ip = $data.peer_ip
        host_key_lengths = [pscustomobject]@{
            in_cipher = ($data.host.in_cipher_key.Length / 2)
            in_auth = ($data.host.in_auth_key.Length / 2)
            out_cipher = ($data.host.out_cipher_key.Length / 2)
            out_auth = ($data.host.out_auth_key.Length / 2)
        }
        peer_key_lengths = [pscustomobject]@{
            in_cipher = ($data.peer.in_cipher_key.Length / 2)
            in_auth = ($data.peer.in_auth_key.Length / 2)
            out_cipher = ($data.peer.out_cipher_key.Length / 2)
            out_auth = ($data.peer.out_auth_key.Length / 2)
        }
        directional_keys_match = $true
    }
}

function Test-ReportRedaction
{
    $targets = @()
    $targets += Get-ChildItem -LiteralPath (Join-Path $ManualSaDir "bin") -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match 'aike-.*report\.(json|md)$|aike-key-export-redacted-.*\.json$' }
    $runs = Join-Path $ManualSaDir "bin\runs"
    if (Test-Path -LiteralPath $runs)
    {
        $targets += Get-ChildItem -LiteralPath $runs -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match 'aike-.*report\.(json|md)$|aike-key-export-redacted\.json$' }
    }

    $findings = @()
    foreach ($target in $targets)
    {
        $text = Get-Content -LiteralPath $target.FullName -Raw -ErrorAction SilentlyContinue
        foreach ($marker in $ForbiddenSecretMarkers)
        {
            if ($text -and $text.IndexOf($marker, [StringComparison]::OrdinalIgnoreCase) -ge 0)
            {
                $findings += [pscustomobject]@{
                    path = $target.FullName
                    marker = $marker
                }
            }
        }
    }

    if ($findings.Count -gt 0)
    {
        throw "raw-key markers found in reports: $($findings | ConvertTo-Json -Compress)"
    }

    return [pscustomobject]@{
        scanned_files = $targets.Count
        redaction_ok = $true
    }
}

function Invoke-AikeLiveProof
{
    $args = @(
        "aike-live-proof",
        "--config", $ConfigPath,
        "--local", $Local,
        "--remote", $Remote,
        "--local-peer", $LocalPeer,
        "--remote-peer", $RemotePeer,
        "--helper-listen-host", $HelperListenHost,
        "--helper-port", [string] $HelperPort,
        "--agent-node-id", $AgentNodeId,
        "--control-plane-ports", $ControlPlanePorts,
        "--tcp-port", [string] $TcpPort,
        "--udp-port", [string] $UdpPort,
        "--wait-seconds", [string] $WaitSeconds,
        "--timeout-seconds", [string] $TimeoutSeconds,
        "--json"
    )
    if ($KeepSa)
    {
        $args += "--keep-sa"
    }

    $jsonText = & $CliExe @args 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0)
    {
        throw "aike-live-proof failed with $exitCode. Output tail: $(Get-TextTail $jsonText 30)"
    }

    $parsed = ($jsonText -join "`n") | ConvertFrom-Json
    if (-not $parsed.success)
    {
        throw "aike-live-proof returned success=false"
    }

    $proof = $parsed.data.proof.proof
    return [pscustomobject]@{
        duration_ms = $parsed.data.duration_ms
        timeout_seconds = $parsed.data.timeout_seconds
        timed_out = $parsed.data.timed_out
        report_redaction_ok = $parsed.data.report_redaction_ok
        report_path = $parsed.data.report_path
        markdown_path = $parsed.data.markdown_path
        session_id = $parsed.data.proof.aike.session_id
        transcript_hash = $parsed.data.proof.aike.transcript_hash
        host_spi = $proof.host_spi
        peer_spi = $proof.peer_spi
        icmp_esp_packet_lines = $proof.counters.icmp_esp_packet_lines
        clear_icmp_packet_lines = $proof.counters.clear_icmp_packet_lines
        data_esp_packet_lines = $proof.counters.data_esp_packet_lines
        data_clear_tcp_test_packet_lines = $proof.counters.data_clear_tcp_test_packet_lines
        data_clear_udp_test_packet_lines = $proof.counters.data_clear_udp_test_packet_lines
        control_clear_tcp_packet_lines = $proof.counters.control_clear_tcp_packet_lines
        control_esp_packet_lines = $proof.counters.control_esp_packet_lines
        checks = $proof.checks
    }
}

function Add-Artifact
{
    param(
        [Parameter(Mandatory)] [string] $Kind,
        [Parameter(Mandatory)] [string] $Path
    )

    if (Test-Path -LiteralPath $Path)
    {
        $Script:Artifacts.Add([pscustomobject]@{
            kind = $Kind
            path = [System.IO.Path]::GetFullPath($Path)
        })
    }
}

function Add-IdentitySummaryArtifact
{
    param(
        [Parameter(Mandatory)] [string] $Kind,
        [Parameter(Mandatory)] [string] $IdentityPath
    )

    if (-not (Test-Path -LiteralPath $IdentityPath))
    {
        return
    }

    $identity = Get-Content -LiteralPath $IdentityPath -Raw | ConvertFrom-Json
    $summaryPath = [System.IO.Path]::ChangeExtension($IdentityPath, ".summary.json")
    [pscustomobject]@{
        node_id = $identity.node_id
        key_id = $identity.key_id
        public_identity_key = $identity.public_identity_key
        peer_install_envelope_public_key = $identity.peer_install_envelope_public_key
        algorithm = $identity.algorithm
        created_at = $identity.created_at
        private_seed_protection = $identity.private_seed_protection
        private_seed_fingerprint = $identity.private_seed_fingerprint
        protected_private_seed_redacted = $null -ne $identity.protected_private_seed
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding UTF8

    Add-Artifact $Kind $summaryPath
}

function Test-CommandAvailable
{
    param([Parameter(Mandatory)] [string] $Name)

    return $null -ne (Get-Command $Name -ErrorAction SilentlyContinue)
}

function Test-IcmpReachable
{
    param(
        [Parameter(Mandatory)] [string] $Address,
        [int] $Attempts = 3
    )

    $output = & ping.exe "-n" ([string] $Attempts) "-w" "1000" $Address 2>&1
    if ($LASTEXITCODE -eq 0)
    {
        return $true
    }

    return @($output | Where-Object { $_.ToString() -match 'TTL=' }).Count -gt 0
}

function Get-RoutePrintDiagnostic
{
    param([Parameter(Mandatory)] [string] $Target)

    try
    {
        $output = & route.exe "PRINT" $Target 2>&1
        return [pscustomobject]@{
            exit_code = $LASTEXITCODE
            output_tail = Get-TextTail $output 40
        }
    }
    catch
    {
        return [pscustomobject]@{
            exit_code = -1
            error = $_.Exception.Message
        }
    }
}

function Get-RemoteRouteDiagnostic
{
    param([Parameter(Mandatory)] [string] $Target)

    $result = [ordered]@{
        ok = $false
        next_hop = $null
        interface_alias = $null
        route_metric = $null
        gateway_ping_ok = $null
        error = $null
        route_print = Get-RoutePrintDiagnostic -Target $Target
    }

    try
    {
        $route = Find-NetRoute -RemoteIPAddress $Target -ErrorAction Stop | Select-Object -First 1
        if ($route)
        {
            $result.ok = $true
            $result.next_hop = [string] $route.NextHop
            $result.interface_alias = [string] $route.InterfaceAlias
            $result.route_metric = [int] $route.RouteMetric

            if (-not [string]::IsNullOrWhiteSpace($result.next_hop) -and
                $result.next_hop -ne "0.0.0.0" -and
                $result.next_hop -ne "::")
            {
                $result.gateway_ping_ok = Test-IcmpReachable -Address $result.next_hop
            }
        }
        else
        {
            $result.error = "Find-NetRoute returned no route"
        }
    }
    catch
    {
        $result.error = $_.Exception.Message
    }

    return [pscustomobject] $result
}

function Test-LivePreflight
{
    param([bool] $RequireAdmin = $false)

    $admin = Test-IsAdmin

    $manualSaExists = Test-Path -LiteralPath $ManualSaExe
    $cliExists = Test-Path -LiteralPath $CliExe
    $pktmonExists = Test-CommandAvailable "pktmon.exe"
    $powershellExists = Test-CommandAvailable "powershell.exe"
    $dotnetExists = Test-CommandAvailable "dotnet"
    $pingOk = Test-IcmpReachable -Address $Remote
    $routeDiagnostic = Get-RemoteRouteDiagnostic -Target $Remote
    $portListeners = @(Get-NetTCPConnection -LocalPort $HelperPort -State Listen -ErrorAction SilentlyContinue)
    $helperPortFree = $portListeners.Count -eq 0
    $stalePidFiles = @(
        (Join-Path $ManualSaDir "bin\host-helper-cycle.pid"),
        (Join-Path $ManualSaDir "bin\u13-agent-bootstrap-server.pid")
    ) | Where-Object { Test-Path -LiteralPath $_ }

    $stats = $null
    if ($admin -and $manualSaExists)
    {
        try
        {
            $statsOutput = & $ManualSaExe "stats" 2>&1
            $stats = [pscustomobject]@{
                exit_code = $LASTEXITCODE
                output_tail = Get-TextTail $statsOutput 20
            }
        }
        catch
        {
            $stats = [pscustomobject]@{
                exit_code = -1
                error = $_.Exception.Message
            }
        }
    }

    $criticalErrors = @()
    if (-not $manualSaExists) { $criticalErrors += "manual-sa.exe missing: $ManualSaExe" }
    if (-not $cliExists) { $criticalErrors += "AdaptiveClient.Cli.exe missing: $CliExe" }
    if (-not $pktmonExists) { $criticalErrors += "pktmon.exe is not available" }
    if (-not $powershellExists) { $criticalErrors += "powershell.exe is not available" }
    if (-not $dotnetExists) { $criticalErrors += "dotnet is not available" }
    if ($RequireAdmin -and -not $admin) { $criticalErrors += "Administrator shell is required for LiveFull host-side WFP/manual-SA operations" }
    if (-not $helperPortFree) { $criticalErrors += "helper port $HelperPort is busy" }
    if (-not $pingOk) { $criticalErrors += "remote $Remote is not reachable by ICMP; fix U13 power/state or Windows route before LiveFull" }
    if (-not $pingOk -and $routeDiagnostic.ok -ne $true) { $criticalErrors += "route lookup to $Remote failed: $($routeDiagnostic.error)" }
    if (-not $pingOk -and
        $routeDiagnostic.ok -eq $true -and
        $routeDiagnostic.gateway_ping_ok -eq $false -and
        -not [string]::IsNullOrWhiteSpace([string] $routeDiagnostic.next_hop) -and
        $routeDiagnostic.next_hop -ne "0.0.0.0")
    {
        $criticalErrors += "route to $Remote uses unreachable next hop $($routeDiagnostic.next_hop) on $($routeDiagnostic.interface_alias)"
    }

    if ($criticalErrors.Count -gt 0)
    {
        throw ($criticalErrors -join "; ")
    }

    return [pscustomobject]@{
        admin = $admin
        admin_required = $RequireAdmin
        admin_missing_warning = if ($RequireAdmin -and -not $admin) { "wrapper is not elevated; nested live/recover scripts may request UAC elevation" } else { $null }
        manual_sa_exists = $manualSaExists
        cli_exists = $cliExists
        pktmon_exists = $pktmonExists
        powershell_exists = $powershellExists
        dotnet_exists = $dotnetExists
        remote_ping_ok = $pingOk
        remote_route = $routeDiagnostic
        helper_port = $HelperPort
        helper_port_free = $helperPortFree
        helper_port_listeners = @($portListeners | ForEach-Object {
            [pscustomobject]@{
                local_address = $_.LocalAddress
                local_port = $_.LocalPort
                owning_process = $_.OwningProcess
            }
        })
        stale_pid_files = @($stalePidFiles)
        manual_sa_stats = $stats
    }
}

function Invoke-RecoverLabState
{
    $stopScript = Join-Path $ManualSaDir "stop-live-gate-stale-processes.ps1"
    $recoverScript = Join-Path $ManualSaDir "recover-protocol0-elevated.ps1"
    if (-not (Test-Path -LiteralPath $stopScript))
    {
        throw "stop-live-gate-stale-processes.ps1 not found: $stopScript"
    }
    if (-not (Test-Path -LiteralPath $recoverScript))
    {
        throw "recover-protocol0-elevated.ps1 not found: $recoverScript"
    }

    $stopResult = Invoke-NativeCommand "powershell.exe" @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", $stopScript,
        "-HelperPort", [string] $HelperPort
    )

    $recoverResult = Invoke-NativeCommand "powershell.exe" @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", $recoverScript,
        "-Local", $Local,
        "-Remote", $Remote,
        "-Protocol", $RecoveryProtocol
    )

    $reportPath = Join-Path $ManualSaDir "bin\protocol0-recover-report.json"
    Add-Artifact "recover-report" $reportPath

    return [pscustomobject]@{
        stop_stale = $stopResult
        recover = $recoverResult
        report_path = if (Test-Path -LiteralPath $reportPath) { [System.IO.Path]::GetFullPath($reportPath) } else { $null }
    }
}

function Wait-TcpPort
{
    param(
        [Parameter(Mandatory)] [int] $Port,
        [int] $TimeoutSeconds = 15
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline)
    {
        $client = [System.Net.Sockets.TcpClient]::new()
        try
        {
            $async = $client.BeginConnect("127.0.0.1", $Port, $null, $null)
            if ($async.AsyncWaitHandle.WaitOne(300))
            {
                $client.EndConnect($async)
                return $true
            }
        }
        catch
        {
        }
        finally
        {
            $client.Close()
        }

        Start-Sleep -Milliseconds 250
    }

    return $false
}

function Invoke-CliJson
{
    param([Parameter(Mandatory)] [string[]] $Arguments)

    $output = & $CliExe @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0)
    {
        throw "AdaptiveClient.Cli failed with $exitCode. Args=$($Arguments -join ' ') Output tail: $(Get-TextTail $output 30)"
    }

    $parsed = ($output -join "`n") | ConvertFrom-Json
    if (-not $parsed.success)
    {
        throw "AdaptiveClient.Cli returned success=false. Args=$($Arguments -join ' ') Error=$($parsed.error | ConvertTo-Json -Compress)"
    }

    return $parsed
}

function Invoke-CliJsonFailure
{
    param(
        [Parameter(Mandatory)] [string[]] $Arguments,
        [Parameter(Mandatory)] [string] $ExpectedCodeFragment
    )

    $output = & $CliExe @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -eq 0)
    {
        throw "AdaptiveClient.Cli unexpectedly succeeded. Args=$($Arguments -join ' ') Output tail: $(Get-TextTail $output 30)"
    }

    $parsed = ($output -join "`n") | ConvertFrom-Json
    if ($parsed.success)
    {
        throw "AdaptiveClient.Cli returned success=true with nonzero exit. Args=$($Arguments -join ' ')"
    }

    $errorJson = $parsed.error | ConvertTo-Json -Compress
    if ($errorJson.IndexOf($ExpectedCodeFragment, [StringComparison]::OrdinalIgnoreCase) -lt 0)
    {
        throw "AdaptiveClient.Cli failure did not contain expected '$ExpectedCodeFragment'. Error=$errorJson"
    }

    return $parsed
}

function Assert-PeerInstallBundle
{
    param(
        [Parameter(Mandatory)] [object] $Response,
        [Parameter(Mandatory)] [string] $CommandName
    )

    $bundle = $Response.data.peer_install_bundle
    if ($null -eq $bundle)
    {
        throw "$CommandName response missing peer_install_bundle"
    }

    if ($bundle.install_mode -ne "peer_external_orchestration")
    {
        throw "$CommandName peer_install_bundle.install_mode unexpected: $($bundle.install_mode)"
    }

    if ($bundle.plan_transfer -ne "encrypted_control_plane_required")
    {
        throw "$CommandName peer_install_bundle.plan_transfer unexpected: $($bundle.plan_transfer)"
    }

    foreach ($required in @("peer_id", "session_id", "route_id", "local_ip", "remote_ip", "inbound_spi", "outbound_spi", "traffic_selector_protocol"))
    {
        if ($null -eq $bundle.$required -or [string]::IsNullOrWhiteSpace([string] $bundle.$required))
        {
            throw "$CommandName peer_install_bundle missing $required"
        }
    }

    $json = $bundle | ConvertTo-Json -Depth 20 -Compress
    foreach ($marker in $ForbiddenSecretMarkers)
    {
        if ($json.IndexOf($marker, [StringComparison]::OrdinalIgnoreCase) -ge 0)
        {
            throw "$CommandName peer_install_bundle leaked raw-key marker: $marker"
        }
    }
}

function Assert-ServiceIdentitySignature
{
    param(
        [Parameter(Mandatory)] [object] $Response,
        [Parameter(Mandatory)] [string] $CommandName
    )

    $mode = [string] $Response.data.identity_signature_mode
    if ($mode.IndexOf("Ed25519", [StringComparison]::Ordinal) -lt 0)
    {
        throw "$CommandName did not use local Ed25519 signature mode: $mode"
    }

    if ($null -eq $Response.data.identity)
    {
        throw "$CommandName response missing identity summary"
    }

    $localPublicKey = [string] $Response.data.identity.local_public_identity_key
    if (-not $localPublicKey.StartsWith("ed25519:", [StringComparison]::Ordinal))
    {
        throw "$CommandName local identity is not ed25519: $localPublicKey"
    }
}

function New-PeerInstallPayloadFile
{
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $LocalIp,
        [Parameter(Mandatory)] [string] $RemoteIp,
        [Parameter(Mandatory)] [string] $LocalPeerId,
        [Parameter(Mandatory)] [string] $RemotePeerId,
        [Parameter(Mandatory)] [string] $RecipientX25519PublicKey,
        [Parameter(Mandatory)] [uint32] $HostInboundSpi,
        [Parameter(Mandatory)] [uint32] $PeerInboundSpi
    )

    $raw = & $CliExe @(
        "aike-key-export",
        "--local", $LocalIp,
        "--remote", $RemoteIp,
        "--local-peer", $LocalPeerId,
        "--remote-peer", $RemotePeerId,
        "--host-in-spi", [string] $HostInboundSpi,
        "--peer-in-spi", [string] $PeerInboundSpi,
        "--json"
    ) 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0)
    {
        throw "aike-key-export for peer-install payload failed with $exitCode. Output tail: $(Get-TextTail $raw 20)"
    }

    $parsed = ($raw -join "`n") | ConvertFrom-Json
    if (-not $parsed.success)
    {
        throw "aike-key-export for peer-install payload returned success=false"
    }

    $data = $parsed.data
    $plainPath = Join-Path ([System.IO.Path]::GetTempPath()) "adaptive-peer-install-plain-$RunId.json"
    try
    {
        $payload = [ordered]@{
            peer_id = $RemotePeerId
            remote_peer_id = $LocalPeerId
            session_id = $data.session_id
            route_id = $data.route_id
            local_ip = $data.peer_ip
            remote_ip = $data.host_ip
            traffic_selector_protocol = "icmp"
            inbound_spi = $PeerInboundSpi
            outbound_spi = $HostInboundSpi
            in_cipher_key = $data.peer.in_cipher_key
            in_auth_key = $data.peer.in_auth_key
            out_cipher_key = $data.peer.out_cipher_key
            out_auth_key = $data.peer.out_auth_key
            identity_signature_mode = [string] $data.identity_signature_mode
            source = "service-smoke-sensitive-control-plane-payload"
        }

        $payload | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $plainPath -Encoding UTF8
        $envelopeOutput = & $CliExe @(
            "peer-install-envelope",
            "--payload-file", $plainPath,
            "--output", $Path,
            "--envelope-secret", $PeerInstallEnvelopeSecret,
            "--sender", $LocalPeerId,
            "--recipient", $LocalPeerId,
            "--recipient-x25519-public", $RecipientX25519PublicKey,
            "--json"
        ) 2>&1
        $envelopeExitCode = $LASTEXITCODE
        if ($envelopeExitCode -ne 0)
        {
            throw "peer-install-envelope failed with $envelopeExitCode. Output tail: $(Get-TextTail $envelopeOutput 20)"
        }

        $envelopeText = Get-Content -LiteralPath $Path -Raw
        foreach ($marker in $ForbiddenSecretMarkers)
        {
            if ($envelopeText.IndexOf($marker, [StringComparison]::OrdinalIgnoreCase) -ge 0)
            {
                throw "encrypted peer-install envelope leaked raw-key marker: $marker"
            }
        }
    }
    finally
    {
        Remove-Item -LiteralPath $plainPath -Force -ErrorAction SilentlyContinue
    }

    return $Path
}

function Assert-PeerInstallAck
{
    param(
        [Parameter(Mandatory)] [object] $Response
    )

    if ($null -eq $Response.data.peer_install_ack)
    {
        throw "peer-install response missing peer_install_ack"
    }

    if ($Response.data.peer_install_ack.status -ne "accepted")
    {
        throw "peer-install ack was not accepted: $($Response.data.peer_install_ack.status)"
    }

    if ($Response.data.peer_install_ack.raw_keys_logged -ne $false)
    {
        throw "peer-install ack did not confirm raw_keys_logged=false"
    }

    if ($Response.data.lifecycle_state -ne "peer_install_validated")
    {
        throw "peer-install lifecycle_state unexpected: $($Response.data.lifecycle_state)"
    }

    if ($Response.data.payload_transport -ne "encrypted_envelope")
    {
        throw "peer-install payload_transport unexpected: $($Response.data.payload_transport)"
    }

    if ($Response.data.payload_envelope_mode -ne "x25519")
    {
        throw "peer-install payload_envelope_mode unexpected: $($Response.data.payload_envelope_mode)"
    }

    $responseJson = $Response | ConvertTo-Json -Depth 30 -Compress
    foreach ($marker in $ForbiddenSecretMarkers)
    {
        if ($responseJson.IndexOf($marker, [StringComparison]::OrdinalIgnoreCase) -ge 0)
        {
            throw "peer-install response leaked raw-key marker: $marker"
        }
    }
}

function Assert-DualEd25519SelfTest
{
    param(
        [Parameter(Mandatory)] [object] $Response
    )

    $mode = [string] $Response.data.dual_identity_export_smoke
    if ($mode -ne "Ed25519")
    {
        throw "self-test dual identity export did not use pure Ed25519 mode: $mode"
    }
}

function Invoke-ServiceSmoke
{
    $serviceExe = Join-Path $ClientDir "AdaptiveClient.Service\bin\Debug\net9.0\AdaptiveClient.Service.exe"
    if (-not (Test-Path -LiteralPath $serviceExe))
    {
        throw "AdaptiveClient.Service.exe not found: $serviceExe"
    }

    $tempConfig = Join-Path $ReportDir "service-smoke-config-$RunId.json"
    $serviceLog = Join-Path $ReportDir "service-smoke-$RunId.jsonl"
    $stdoutPath = Join-Path $ReportDir "service-smoke-$RunId.stdout.log"
    $stderrPath = Join-Path $ReportDir "service-smoke-$RunId.stderr.log"
    $restartLog = Join-Path $ReportDir "service-smoke-restart-$RunId.jsonl"
    $restartStdoutPath = Join-Path $ReportDir "service-smoke-restart-$RunId.stdout.log"
    $restartStderrPath = Join-Path $ReportDir "service-smoke-restart-$RunId.stderr.log"
    $config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
    $config.listen_port = $ServicePort
    $config.manual_sa_path = $ManualSaExe
    $stateDir = Join-Path $ReportDir "service-smoke-state-$RunId"
    $sessionIndexPath = Join-Path $ReportDir "service-smoke-session-index-$RunId.json"
    $peerInstallReplayCachePath = Join-Path $ReportDir "service-smoke-peer-install-replay-cache-$RunId.json"
    $endpointRegistryPath = Join-Path $ReportDir "service-smoke-endpoint-registry-$RunId.json"
    $config | Add-Member -NotePropertyName "manual_sa_install_mode" -NotePropertyValue "validate_only" -Force
    $config | Add-Member -NotePropertyName "manual_sa_state_dir" -NotePropertyValue $stateDir -Force
    $config | Add-Member -NotePropertyName "manual_sa_session_index_path" -NotePropertyValue $sessionIndexPath -Force
    $config | Add-Member -NotePropertyName "peer_install_replay_cache_path" -NotePropertyValue $peerInstallReplayCachePath -Force
    $identityPath = Join-Path $ReportDir "service-smoke-identity-$RunId.json"
    $config | Add-Member -NotePropertyName "identity_store_path" -NotePropertyValue $identityPath -Force
    $config | Add-Member -NotePropertyName "endpoint_registry_path" -NotePropertyValue $endpointRegistryPath -Force
    $config | Add-Member -NotePropertyName "peer_install_envelope_secret" -NotePropertyValue $PeerInstallEnvelopeSecret -Force
    $config | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $tempConfig -Encoding UTF8

    $process = Start-Process `
        -FilePath $serviceExe `
        -ArgumentList @("--config", $tempConfig, "--log", $serviceLog) `
        -NoNewWindow `
        -PassThru `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath

    try
    {
        if (-not (Wait-TcpPort -Port $ServicePort -TimeoutSeconds 15))
        {
            throw "service did not open 127.0.0.1:$ServicePort"
        }

        $commands = New-Object System.Collections.Generic.List[object]
        $initialStatus = Invoke-CliJson @("status", "--config", $tempConfig, "--port", [string] $ServicePort, "--json")
        $recipientX25519PublicKey = [string] $initialStatus.data.identity.peer_install_envelope_public_key
        if (-not $recipientX25519PublicKey.StartsWith("x25519:", [StringComparison]::Ordinal))
        {
            throw "status did not expose x25519 peer install envelope public key: $recipientX25519PublicKey"
        }

        $commands.Add($initialStatus)
        $maintenance = Invoke-CliJson @("maintenance", "--config", $tempConfig, "--port", [string] $ServicePort, "--json")
        if ($maintenance.data.enabled -ne $true)
        {
            throw "maintenance command did not report enabled policy"
        }
        if ($maintenance.data.expired_cleanup_count -ne 0 -or $maintenance.data.due_rekey_count -ne 0)
        {
            throw "maintenance command unexpectedly found due lifecycle actions in fresh service smoke"
        }
        $commands.Add($maintenance)
        $selfTest = Invoke-CliJson @("self-test", "--config", $tempConfig, "--port", [string] $ServicePort, "--json")
        Assert-DualEd25519SelfTest -Response $selfTest
        $commands.Add($selfTest)
        $commands.Add((Invoke-CliJson @("endpoint-update", "User1", "192.168.101.20", "--selector", "icmp", "--config", $tempConfig, "--port", [string] $ServicePort, "--json")))
        $connect = Invoke-CliJson @("connect", "User1", "--config", $tempConfig, "--port", [string] $ServicePort, "--json")
        Assert-PeerInstallBundle -Response $connect -CommandName "connect"
        Assert-ServiceIdentitySignature -Response $connect -CommandName "connect"
        $commands.Add($connect)
        $manualSaPlan = Invoke-CliJson @("manual-sa-plan", "User1", "--config", $tempConfig, "--port", [string] $ServicePort, "--json")
        Assert-PeerInstallBundle -Response $manualSaPlan -CommandName "manual-sa-plan"
        $commands.Add($manualSaPlan)
        $rekey = Invoke-CliJson @("rekey", "User1", "--config", $tempConfig, "--port", [string] $ServicePort, "--json")
        Assert-PeerInstallBundle -Response $rekey -CommandName "rekey"
        Assert-ServiceIdentitySignature -Response $rekey -CommandName "rekey"
        $commands.Add($rekey)

        $peerPayloadPath = Join-Path ([System.IO.Path]::GetTempPath()) "adaptive-peer-install-$RunId.json"
        try
        {
            New-PeerInstallPayloadFile `
                -Path $peerPayloadPath `
                -LocalIp "192.168.113.199" `
                -RemoteIp "192.168.101.20" `
                -LocalPeerId "Windows11-U13" `
                -RemotePeerId "User1" `
                -RecipientX25519PublicKey $recipientX25519PublicKey `
                -HostInboundSpi 0xA100 `
                -PeerInboundSpi 0xB200 | Out-Null
            $peerInstall = Invoke-CliJson @("peer-install", "User1", "--payload-file", $peerPayloadPath, "--config", $tempConfig, "--port", [string] $ServicePort, "--json")
            Assert-PeerInstallAck -Response $peerInstall
            $commands.Add($peerInstall)
            $duplicateReplay = Invoke-CliJsonFailure `
                -Arguments @("peer-install", "User1", "--payload-file", $peerPayloadPath, "--config", $tempConfig, "--port", [string] $ServicePort, "--json") `
                -ExpectedCodeFragment "replay_detected"
            $commands.Add($duplicateReplay)

            $tamperedPath = Join-Path ([System.IO.Path]::GetTempPath()) "adaptive-peer-install-tampered-$RunId.json"
            try
            {
                $tampered = Get-Content -LiteralPath $peerPayloadPath -Raw | ConvertFrom-Json
                $tampered.envelope_recipient_id = "UnexpectedNode"
                $tampered | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $tamperedPath -Encoding UTF8
                $recipientMismatch = Invoke-CliJsonFailure `
                    -Arguments @("peer-install", "User1", "--payload-file", $tamperedPath, "--config", $tempConfig, "--port", [string] $ServicePort, "--json") `
                    -ExpectedCodeFragment "recipient"
                $commands.Add($recipientMismatch)
            }
            finally
            {
                Remove-Item -LiteralPath $tamperedPath -Force -ErrorAction SilentlyContinue
            }

            if ($process -and -not $process.HasExited)
            {
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
                $process.WaitForExit(5000) | Out-Null
            }

            $process = Start-Process `
                -FilePath $serviceExe `
                -ArgumentList @("--config", $tempConfig, "--log", $restartLog) `
                -NoNewWindow `
                -PassThru `
                -RedirectStandardOutput $restartStdoutPath `
                -RedirectStandardError $restartStderrPath
            if (-not (Wait-TcpPort -Port $ServicePort -TimeoutSeconds 15))
            {
                throw "service did not reopen 127.0.0.1:$ServicePort after restart"
            }

            $restartReplay = Invoke-CliJsonFailure `
                -Arguments @("peer-install", "User1", "--payload-file", $peerPayloadPath, "--config", $tempConfig, "--port", [string] $ServicePort, "--json") `
                -ExpectedCodeFragment "replay_detected"
            $commands.Add($restartReplay)
        }
        finally
        {
            Remove-Item -LiteralPath $peerPayloadPath -Force -ErrorAction SilentlyContinue
        }

        $commands.Add((Invoke-CliJson @("status", "--config", $tempConfig, "--port", [string] $ServicePort, "--json")))

        $cleanup = $null
        if (Test-IsAdmin)
        {
            $cleanup = Invoke-CliJson @("cleanup", "User1", "--config", $tempConfig, "--port", [string] $ServicePort, "--json")
        }

        Add-Artifact "service-smoke-config" $tempConfig
        Add-IdentitySummaryArtifact "service-smoke-identity-summary" $identityPath
        Add-Artifact "service-smoke-log" $serviceLog
        Add-Artifact "service-smoke-stdout" $stdoutPath
        Add-Artifact "service-smoke-stderr" $stderrPath
        Add-Artifact "service-smoke-restart-log" $restartLog
        Add-Artifact "service-smoke-restart-stdout" $restartStdoutPath
        Add-Artifact "service-smoke-restart-stderr" $restartStderrPath
        Add-Artifact "service-smoke-session-index" $sessionIndexPath
        Add-Artifact "service-smoke-peer-install-replay-cache" $peerInstallReplayCachePath
        Add-Artifact "service-smoke-endpoint-registry" $endpointRegistryPath
        if (Test-Path -LiteralPath $stateDir)
        {
            Add-Artifact "service-smoke-state-dir" $stateDir
        }

        return [pscustomobject]@{
            port = $ServicePort
            process_id = $process.Id
            command_count = $commands.Count
            maintenance_enabled = [bool] $maintenance.data.enabled
            dual_identity_export_smoke = [string] $selfTest.data.dual_identity_export_smoke
            connect_identity_signature_mode = [string] $connect.data.identity_signature_mode
            rekey_identity_signature_mode = [string] $rekey.data.identity_signature_mode
            peer_install_ack_status = [string] $peerInstall.data.peer_install_ack.status
            peer_install_lifecycle_state = [string] $peerInstall.data.lifecycle_state
            peer_install_payload_transport = [string] $peerInstall.data.payload_transport
            peer_install_payload_envelope_mode = [string] $peerInstall.data.payload_envelope_mode
            peer_install_replay_guard = "replay_detected"
            peer_install_restart_replay_guard = "replay_detected_after_restart"
            peer_install_recipient_guard = "recipient_mismatch_rejected"
            cleanup_exercised = $null -ne $cleanup
            cleanup_skipped_reason = if ($cleanup) { $null } else { "not elevated; avoiding WFP cleanup in service smoke" }
            log = $serviceLog
            stdout = $stdoutPath
            stderr = $stderrPath
        }
    }
    finally
    {
        if ($process -and -not $process.HasExited)
        {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            $process.WaitForExit(5000) | Out-Null
        }
    }
}

function Invoke-ServiceToServiceSmoke
{
    $serviceExe = Join-Path $ClientDir "AdaptiveClient.Service\bin\Debug\net9.0\AdaptiveClient.Service.exe"
    if (-not (Test-Path -LiteralPath $serviceExe))
    {
        throw "AdaptiveClient.Service.exe not found: $serviceExe"
    }

    $initiatorPort = $ServicePort + 20
    $peerPort = $ServicePort + 21
    $initiatorConfigPath = Join-Path $ReportDir "service-to-service-initiator-config-$RunId.json"
    $peerConfigPath = Join-Path $ReportDir "service-to-service-peer-config-$RunId.json"
    $initiatorLog = Join-Path $ReportDir "service-to-service-initiator-$RunId.jsonl"
    $peerLog = Join-Path $ReportDir "service-to-service-peer-$RunId.jsonl"
    $initiatorStdout = Join-Path $ReportDir "service-to-service-initiator-$RunId.stdout.log"
    $initiatorStderr = Join-Path $ReportDir "service-to-service-initiator-$RunId.stderr.log"
    $peerStdout = Join-Path $ReportDir "service-to-service-peer-$RunId.stdout.log"
    $peerStderr = Join-Path $ReportDir "service-to-service-peer-$RunId.stderr.log"

    function New-ServiceToServiceConfig
    {
        param(
            [Parameter(Mandatory)] [string] $Path,
            [Parameter(Mandatory)] [string] $NodeId,
            [Parameter(Mandatory)] [int] $ListenPort,
            [Parameter(Mandatory)] [int] $InitiatorPortValue,
            [Parameter(Mandatory)] [int] $PeerPortValue,
            [Parameter(Mandatory)] [string] $DeliveryMode
        )

        $config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
        $config.node_id = $NodeId
        $config.listen_port = $ListenPort
        $config.manual_sa_path = $ManualSaExe
        $config | Add-Member -NotePropertyName "manual_sa_install_mode" -NotePropertyValue "validate_only" -Force
        $config | Add-Member -NotePropertyName "manual_sa_state_dir" -NotePropertyValue (Join-Path $ReportDir "service-to-service-state-$NodeId-$RunId") -Force
        $config | Add-Member -NotePropertyName "manual_sa_session_index_path" -NotePropertyValue (Join-Path $ReportDir "service-to-service-session-index-$NodeId-$RunId.json") -Force
        $config | Add-Member -NotePropertyName "peer_install_replay_cache_path" -NotePropertyValue (Join-Path $ReportDir "service-to-service-peer-install-replay-cache-$NodeId-$RunId.json") -Force
        $config | Add-Member -NotePropertyName "identity_store_path" -NotePropertyValue (Join-Path $ReportDir "service-to-service-identity-$NodeId-$RunId.json") -Force
        $config | Add-Member -NotePropertyName "endpoint_registry_path" -NotePropertyValue (Join-Path $ReportDir "service-to-service-endpoint-registry-$NodeId-$RunId.json") -Force
        $config | Add-Member -NotePropertyName "peer_install_envelope_secret" -NotePropertyValue $PeerInstallEnvelopeSecret -Force
        $config | Add-Member -NotePropertyName "peer_install_delivery_mode" -NotePropertyValue $DeliveryMode -Force
        $config | Add-Member -NotePropertyName "peer_lifecycle_delivery_required" -NotePropertyValue ($DeliveryMode -eq "service_to_service") -Force

        $config.peers.User1.endpoint = "192.168.101.20"
        $config.peers.User1.overlay_endpoint = "127.0.0.1:$PeerPortValue"
        $config.peers.User1.allowed_selector = "icmp"
        $config.peers.'Windows11-U13'.endpoint = "192.168.113.199"
        $config.peers.'Windows11-U13'.overlay_endpoint = "127.0.0.1:$InitiatorPortValue"
        $config.peers.'Windows11-U13'.allowed_selector = "icmp"

        $config | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Path -Encoding UTF8
        return $config
    }

    function Save-ServiceToServiceConfig
    {
        param(
            [Parameter(Mandatory)] [object] $Config,
            [Parameter(Mandatory)] [string] $Path
        )

        $Config | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Path -Encoding UTF8
    }

    function Pin-ServiceToServiceIdentities
    {
        param(
            [Parameter(Mandatory)] [object] $Config,
            [Parameter(Mandatory)] [string] $InitiatorPublicIdentityKey,
            [Parameter(Mandatory)] [string] $PeerPublicIdentityKey
        )

        $Config.identity_public_keys.User1 = $PeerPublicIdentityKey
        $Config.identity_public_keys.'Windows11-U13' = $InitiatorPublicIdentityKey
        $Config.peers.User1.public_identity_key = $PeerPublicIdentityKey
        $Config.peers.'Windows11-U13'.public_identity_key = $InitiatorPublicIdentityKey
    }

    function Stop-TestServiceProcess
    {
        param([AllowNull()] [System.Diagnostics.Process] $Process)

        if ($Process -and -not $Process.HasExited)
        {
            Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
            $Process.WaitForExit(5000) | Out-Null
        }
    }

    function Start-TestServiceProcess
    {
        param(
            [Parameter(Mandatory)] [string] $ConfigPath,
            [Parameter(Mandatory)] [string] $LogPath,
            [Parameter(Mandatory)] [string] $StdoutPath,
            [Parameter(Mandatory)] [string] $StderrPath
        )

        return Start-Process `
            -FilePath $serviceExe `
            -ArgumentList @("--config", $ConfigPath, "--log", $LogPath) `
            -NoNewWindow `
            -PassThru `
            -RedirectStandardOutput $StdoutPath `
            -RedirectStandardError $StderrPath
    }

    $initiatorConfig = New-ServiceToServiceConfig `
        -Path $initiatorConfigPath `
        -NodeId "Windows11-U13" `
        -ListenPort $initiatorPort `
        -InitiatorPortValue $initiatorPort `
        -PeerPortValue $peerPort `
        -DeliveryMode "service_to_service"
    $peerConfig = New-ServiceToServiceConfig `
        -Path $peerConfigPath `
        -NodeId "User1" `
        -ListenPort $peerPort `
        -InitiatorPortValue $initiatorPort `
        -PeerPortValue $peerPort `
        -DeliveryMode "disabled"

    $initiatorProcess = Start-TestServiceProcess -ConfigPath $initiatorConfigPath -LogPath $initiatorLog -StdoutPath $initiatorStdout -StderrPath $initiatorStderr
    $peerProcess = Start-TestServiceProcess -ConfigPath $peerConfigPath -LogPath $peerLog -StdoutPath $peerStdout -StderrPath $peerStderr

    try
    {
        if (-not (Wait-TcpPort -Port $initiatorPort -TimeoutSeconds 15))
        {
            throw "initiator service did not open 127.0.0.1:$initiatorPort"
        }

        if (-not (Wait-TcpPort -Port $peerPort -TimeoutSeconds 15))
        {
            throw "peer service did not open 127.0.0.1:$peerPort"
        }

        $initiatorStatus = Invoke-CliJson @("status", "--config", $initiatorConfigPath, "--port", [string] $initiatorPort, "--json")
        $peerStatusBefore = Invoke-CliJson @("status", "--config", $peerConfigPath, "--port", [string] $peerPort, "--json")
        if (-not ([string] $peerStatusBefore.data.identity.peer_install_envelope_public_key).StartsWith("x25519:", [StringComparison]::Ordinal))
        {
            throw "peer service did not expose x25519 peer_install_envelope_public_key"
        }

        $initiatorPublicIdentityKey = [string] $initiatorStatus.data.identity.local_public_identity_key
        $peerPublicIdentityKey = [string] $peerStatusBefore.data.identity.local_public_identity_key
        if (-not $initiatorPublicIdentityKey.StartsWith("ed25519:", [StringComparison]::Ordinal))
        {
            throw "initiator service did not expose ed25519 local_public_identity_key"
        }

        if (-not $peerPublicIdentityKey.StartsWith("ed25519:", [StringComparison]::Ordinal))
        {
            throw "peer service did not expose ed25519 local_public_identity_key"
        }

        Pin-ServiceToServiceIdentities -Config $initiatorConfig -InitiatorPublicIdentityKey $initiatorPublicIdentityKey -PeerPublicIdentityKey $peerPublicIdentityKey
        Pin-ServiceToServiceIdentities -Config $peerConfig -InitiatorPublicIdentityKey $initiatorPublicIdentityKey -PeerPublicIdentityKey $peerPublicIdentityKey
        Save-ServiceToServiceConfig -Config $initiatorConfig -Path $initiatorConfigPath
        Save-ServiceToServiceConfig -Config $peerConfig -Path $peerConfigPath

        Stop-TestServiceProcess $initiatorProcess
        Stop-TestServiceProcess $peerProcess
        Start-Sleep -Milliseconds 750
        $initiatorProcess = Start-TestServiceProcess -ConfigPath $initiatorConfigPath -LogPath $initiatorLog -StdoutPath $initiatorStdout -StderrPath $initiatorStderr
        $peerProcess = Start-TestServiceProcess -ConfigPath $peerConfigPath -LogPath $peerLog -StdoutPath $peerStdout -StderrPath $peerStderr

        if (-not (Wait-TcpPort -Port $initiatorPort -TimeoutSeconds 15))
        {
            throw "pinned initiator service did not reopen 127.0.0.1:$initiatorPort"
        }

        if (-not (Wait-TcpPort -Port $peerPort -TimeoutSeconds 15))
        {
            throw "pinned peer service did not reopen 127.0.0.1:$peerPort"
        }

        $initiatorStatus = Invoke-CliJson @("status", "--config", $initiatorConfigPath, "--port", [string] $initiatorPort, "--json")
        $peerStatusBefore = Invoke-CliJson @("status", "--config", $peerConfigPath, "--port", [string] $peerPort, "--json")
        if ($null -eq $peerStatusBefore.data.identity.identity_proof)
        {
            throw "peer service did not expose signed identity_proof"
        }

        Invoke-CliJson @("endpoint-update", "Windows11-U13", "192.168.113.199", "--selector", "icmp", "--config", $initiatorConfigPath, "--port", [string] $initiatorPort, "--json") | Out-Null
        Invoke-CliJson @("endpoint-update", "User1", "192.168.101.20", "--selector", "icmp", "--config", $initiatorConfigPath, "--port", [string] $initiatorPort, "--json") | Out-Null
        Invoke-CliJson @("endpoint-update", "User1", "192.168.101.20", "--selector", "icmp", "--config", $peerConfigPath, "--port", [string] $peerPort, "--json") | Out-Null
        Invoke-CliJson @("endpoint-update", "Windows11-U13", "192.168.113.199", "--selector", "icmp", "--config", $peerConfigPath, "--port", [string] $peerPort, "--json") | Out-Null

        $connect = Invoke-CliJson @("connect", "User1", "--config", $initiatorConfigPath, "--port", [string] $initiatorPort, "--json")
        if ($connect.data.peer_lifecycle_delivery_required -ne $true)
        {
            throw "service-to-service connect did not require peer lifecycle delivery"
        }

        if ($connect.data.end_to_end_lifecycle_state -ne "end_to_end_install_validated")
        {
            throw "service-to-service connect end_to_end_lifecycle_state unexpected: $($connect.data.end_to_end_lifecycle_state)"
        }

        if ($connect.data.peer_install_delivery.attempted -ne $true)
        {
            throw "service-to-service peer install delivery was not attempted"
        }

        if ($connect.data.peer_install_delivery.success -ne $true)
        {
            throw "service-to-service peer install delivery failed: $($connect.data.peer_install_delivery.error)"
        }

        if ($connect.data.peer_install_delivery.payload_transport -ne "encrypted_envelope")
        {
            throw "service-to-service payload_transport unexpected: $($connect.data.peer_install_delivery.payload_transport)"
        }

        if ($connect.data.peer_install_delivery.envelope_mode -ne "x25519")
        {
            throw "service-to-service envelope_mode unexpected: $($connect.data.peer_install_delivery.envelope_mode)"
        }

        if ($connect.data.peer_install_delivery.identity_proof_status -ne "verified")
        {
            throw "service-to-service identity proof was not verified on connect: $($connect.data.peer_install_delivery.identity_proof_error)"
        }

        if ($null -ne $connect.data.peer_install_delivery.peer_inbound_spi)
        {
            $connectHostOutboundSpi = [uint32]($connect.data.material_summary.outbound_spi)
            $connectPeerInboundSpi = [uint32]($connect.data.peer_install_delivery.peer_inbound_spi)
            if ($connectHostOutboundSpi -ne $connectPeerInboundSpi)
            {
                throw "service-to-service connect host outbound_spi does not match peer actual inbound SPI"
            }
        }

        $peerStatusAfter = Invoke-CliJson @("status", "--config", $peerConfigPath, "--port", [string] $peerPort, "--json")
        $validatedSessions = @($peerStatusAfter.data.sessions | Where-Object { $_.lifecycle_state -eq "peer_install_validated" })
        if ($validatedSessions.Count -lt 1)
        {
            throw "peer service has no peer_install_validated session after service-to-service delivery"
        }

        $rekey = Invoke-CliJson @("rekey", "User1", "--config", $initiatorConfigPath, "--port", [string] $initiatorPort, "--json")
        if ($rekey.data.peer_lifecycle_delivery_required -ne $true)
        {
            throw "service-to-service rekey did not require peer lifecycle delivery"
        }

        if ($rekey.data.end_to_end_lifecycle_state -ne "end_to_end_install_validated")
        {
            throw "service-to-service rekey end_to_end_lifecycle_state unexpected: $($rekey.data.end_to_end_lifecycle_state)"
        }

        if ($rekey.data.peer_install_delivery.success -ne $true)
        {
            throw "service-to-service rekey delivery failed: $($rekey.data.peer_install_delivery.error)"
        }

        if ($rekey.data.peer_install_delivery.identity_proof_status -ne "verified")
        {
            throw "service-to-service identity proof was not verified on rekey: $($rekey.data.peer_install_delivery.identity_proof_error)"
        }

        if ($null -ne $rekey.data.peer_install_delivery.peer_inbound_spi)
        {
            $rekeyHostOutboundSpi = [uint32]($rekey.data.material_summary.outbound_spi)
            $rekeyPeerInboundSpi = [uint32]($rekey.data.peer_install_delivery.peer_inbound_spi)
            if ($rekeyHostOutboundSpi -ne $rekeyPeerInboundSpi)
            {
                throw "service-to-service rekey host outbound_spi does not match peer actual inbound SPI"
            }
        }

        if ($rekey.data.session_id -eq $connect.data.session_id)
        {
            throw "service-to-service rekey did not create a new session id"
        }

        $peerStatusAfterRekey = Invoke-CliJson @("status", "--config", $peerConfigPath, "--port", [string] $peerPort, "--json")
        $rekeyValidatedSessions = @($peerStatusAfterRekey.data.sessions | Where-Object { $_.lifecycle_state -eq "peer_install_validated" })
        if ($peerStatusAfterRekey.data.session_count -ne 1 -or $rekeyValidatedSessions.Count -ne 1)
        {
            throw "peer service did not converge to exactly one validated session after rekey"
        }

        if ([string] $rekeyValidatedSessions[0].session_id -ne [string] $rekey.data.peer_install_delivery.peer_session_id)
        {
            throw "peer service session id after rekey does not match delivery ack"
        }

        $cleanup = Invoke-CliJson @("cleanup", "User1", "--config", $initiatorConfigPath, "--port", [string] $initiatorPort, "--json")
        if ($cleanup.data.peer_lifecycle_delivery_required -ne $true)
        {
            throw "service-to-service cleanup did not require peer lifecycle delivery"
        }

        if ($cleanup.data.peer_cleanup_delivery.attempted -ne $true)
        {
            throw "service-to-service peer cleanup delivery was not attempted"
        }

        if ($cleanup.data.peer_cleanup_delivery.success -ne $true)
        {
            throw "service-to-service peer cleanup delivery failed: $($cleanup.data.peer_cleanup_delivery.error)"
        }

        $initiatorStatusAfterCleanup = Invoke-CliJson @("status", "--config", $initiatorConfigPath, "--port", [string] $initiatorPort, "--json")
        $peerStatusAfterCleanup = Invoke-CliJson @("status", "--config", $peerConfigPath, "--port", [string] $peerPort, "--json")
        if ($initiatorStatusAfterCleanup.data.session_count -ne 0)
        {
            throw "initiator service still has sessions after cleanup"
        }

        if ($peerStatusAfterCleanup.data.session_count -ne 0)
        {
            throw "peer service still has sessions after peer cleanup delivery"
        }

        Stop-TestServiceProcess $peerProcess
        $peerProcess = $null
        Start-Sleep -Milliseconds 500
        $strictDeliveryFailure = Invoke-CliJsonFailure `
            -Arguments @("connect", "User1", "--config", $initiatorConfigPath, "--port", [string] $initiatorPort, "--json") `
            -ExpectedCodeFragment "peer_install_delivery_failed"
        $initiatorStatusAfterStrictFailure = Invoke-CliJson @("status", "--config", $initiatorConfigPath, "--port", [string] $initiatorPort, "--json")
        if ($initiatorStatusAfterStrictFailure.data.session_count -ne 0)
        {
            throw "initiator service kept a session after strict peer-delivery failure"
        }

        Add-Artifact "service-to-service-initiator-config" $initiatorConfigPath
        Add-Artifact "service-to-service-peer-config" $peerConfigPath
        Add-IdentitySummaryArtifact "service-to-service-initiator-identity-summary" $initiatorConfig.identity_store_path
        Add-IdentitySummaryArtifact "service-to-service-peer-identity-summary" $peerConfig.identity_store_path
        Add-Artifact "service-to-service-initiator-log" $initiatorLog
        Add-Artifact "service-to-service-peer-log" $peerLog
        Add-Artifact "service-to-service-initiator-stdout" $initiatorStdout
        Add-Artifact "service-to-service-peer-stdout" $peerStdout
        Add-Artifact "service-to-service-initiator-stderr" $initiatorStderr
        Add-Artifact "service-to-service-peer-stderr" $peerStderr
        Add-Artifact "service-to-service-initiator-endpoint-registry" $initiatorConfig.endpoint_registry_path
        Add-Artifact "service-to-service-peer-endpoint-registry" $peerConfig.endpoint_registry_path
        Add-Artifact "service-to-service-initiator-replay-cache" $initiatorConfig.peer_install_replay_cache_path
        Add-Artifact "service-to-service-peer-replay-cache" $peerConfig.peer_install_replay_cache_path

        return [pscustomobject]@{
            initiator_port = $initiatorPort
            peer_port = $peerPort
            initiator_node = [string] $initiatorStatus.data.node_id
            peer_node = [string] $peerStatusBefore.data.node_id
            connect_session_id = [string] $connect.data.session_id
            rekey_session_id = [string] $rekey.data.session_id
            delivery_success = [bool] $connect.data.peer_install_delivery.success
            delivery_ack_status = [string] $connect.data.peer_install_delivery.ack_status
            delivery_payload_transport = [string] $connect.data.peer_install_delivery.payload_transport
            delivery_envelope_mode = [string] $connect.data.peer_install_delivery.envelope_mode
            delivery_identity_proof_status = [string] $connect.data.peer_install_delivery.identity_proof_status
            peer_lifecycle_delivery_required = [bool] $connect.data.peer_lifecycle_delivery_required
            end_to_end_lifecycle_state = [string] $connect.data.end_to_end_lifecycle_state
            rekey_delivery_success = [bool] $rekey.data.peer_install_delivery.success
            rekey_delivery_ack_status = [string] $rekey.data.peer_install_delivery.ack_status
            rekey_identity_proof_status = [string] $rekey.data.peer_install_delivery.identity_proof_status
            rekey_end_to_end_lifecycle_state = [string] $rekey.data.end_to_end_lifecycle_state
            cleanup_delivery_success = [bool] $cleanup.data.peer_cleanup_delivery.success
            cleanup_delivery_action = [string] $cleanup.data.peer_cleanup_delivery.action
            strict_delivery_failure_guard = [string] $strictDeliveryFailure.error.code
            peer_session_count_after_connect = [int] $peerStatusAfter.data.session_count
            peer_session_count_after_rekey = [int] $peerStatusAfterRekey.data.session_count
            initiator_session_count_after_cleanup = [int] $initiatorStatusAfterCleanup.data.session_count
            peer_session_count_after_cleanup = [int] $peerStatusAfterCleanup.data.session_count
            initiator_session_count_after_strict_failure = [int] $initiatorStatusAfterStrictFailure.data.session_count
            peer_validated_sessions = $validatedSessions.Count
            initiator_log = $initiatorLog
            peer_log = $peerLog
        }
    }
    finally
    {
        foreach ($process in @($initiatorProcess, $peerProcess))
        {
            if ($process -and -not $process.HasExited)
            {
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
                $process.WaitForExit(5000) | Out-Null
            }
        }
    }
}

function Invoke-LocalInstallSmoke
{
    if (-not (Test-IsAdmin))
    {
        throw "local install smoke requires elevated Administrator PowerShell"
    }

    $serviceExe = Join-Path $ClientDir "AdaptiveClient.Service\bin\Debug\net9.0\AdaptiveClient.Service.exe"
    if (-not (Test-Path -LiteralPath $serviceExe))
    {
        throw "AdaptiveClient.Service.exe not found: $serviceExe"
    }

    $localInstallPort = $ServicePort + 10
    $tempConfig = Join-Path $ReportDir "local-install-smoke-config-$RunId.json"
    $serviceLog = Join-Path $ReportDir "local-install-smoke-$RunId.jsonl"
    $stdoutPath = Join-Path $ReportDir "local-install-smoke-$RunId.stdout.log"
    $stderrPath = Join-Path $ReportDir "local-install-smoke-$RunId.stderr.log"
    $stateDir = Join-Path $ReportDir "local-install-state-$RunId"
    $sessionIndexPath = Join-Path $ReportDir "local-install-session-index-$RunId.json"
    $endpointRegistryPath = Join-Path $ReportDir "local-install-endpoint-registry-$RunId.json"
    $identityPath = Join-Path $ReportDir "local-install-identity-$RunId.json"

    $config = [ordered]@{
        node_id = $LocalPeer
        listen_port = $localInstallPort
        manual_sa_path = $ManualSaExe
        manual_sa_install_mode = "local_install"
        manual_sa_state_dir = $stateDir
        manual_sa_session_index_path = $sessionIndexPath
        peer_install_replay_cache_path = (Join-Path $ReportDir "local-install-peer-install-replay-cache-$RunId.json")
        identity_store_path = $identityPath
        endpoint_registry_path = $endpointRegistryPath
        validate_manual_sa_plan = $true
        endpoint_registry = @{ default_ttl_seconds = 300; records = @{} }
        peers = @{
            $LocalPeer = @{
                endpoint = $Local
                overlay_endpoint = "$($Local):9000"
                public_identity_key = "ed25519-public-key-placeholder-$LocalPeer"
                allowed_selector = "icmp"
            }
            $RemotePeer = @{
                endpoint = $Remote
                overlay_endpoint = "$($Remote):9000"
                public_identity_key = "ed25519-public-key-placeholder-$RemotePeer"
                allowed_selector = "icmp"
            }
        }
        identity_public_keys = @{
            $LocalPeer = "ed25519-public-key-placeholder-$LocalPeer"
            $RemotePeer = "ed25519-public-key-placeholder-$RemotePeer"
        }
        route_policy = @{
            mode = "direct_live_local_install"
            hop_count = 0
            candidate_intermediates = @($RemotePeer)
        }
        retry_policy = @{
            connect_timeout_ms = 30000
            max_attempts = 1
            backoff_ms = 1000
            cleanup_stale_after_seconds = 120
        }
    }

    $config | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $tempConfig -Encoding UTF8

    $process = Start-Process `
        -FilePath $serviceExe `
        -ArgumentList @("--config", $tempConfig, "--log", $serviceLog) `
        -NoNewWindow `
        -PassThru `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath

    try
    {
        if (-not (Wait-TcpPort -Port $localInstallPort -TimeoutSeconds 20))
        {
            throw "service did not open 127.0.0.1:$localInstallPort"
        }

        $connect = Invoke-CliJson @("connect", $RemotePeer, "--config", $tempConfig, "--port", [string] $localInstallPort, "--json")
        Assert-PeerInstallBundle -Response $connect -CommandName "connect-local-install"
        Assert-ServiceIdentitySignature -Response $connect -CommandName "connect-local-install"
        if (-not $connect.data.manual_sa_install.installed)
        {
            throw "local_install did not install manual-SA: $($connect.data.manual_sa_install.error)"
        }

        $status = Invoke-CliJson @("status", "--config", $tempConfig, "--port", [string] $localInstallPort, "--json")
        $cleanup = Invoke-CliJson @("cleanup", $RemotePeer, "--config", $tempConfig, "--port", [string] $localInstallPort, "--json")
        if ($cleanup.data.cleanup_method -ne "state_delete")
        {
            throw "expected cleanup_method=state_delete, got $($cleanup.data.cleanup_method)"
        }
        if ($cleanup.data.manual_sa_result.exit_code -ne 0)
        {
            throw "local install cleanup failed with exit_code=$($cleanup.data.manual_sa_result.exit_code)"
        }

        Add-Artifact "local-install-smoke-config" $tempConfig
        Add-Artifact "local-install-smoke-state-dir" $stateDir
        Add-IdentitySummaryArtifact "local-install-smoke-identity-summary" $identityPath
        Add-Artifact "local-install-smoke-log" $serviceLog
        Add-Artifact "local-install-smoke-stdout" $stdoutPath
        Add-Artifact "local-install-smoke-stderr" $stderrPath
        Add-Artifact "local-install-smoke-session-index" $sessionIndexPath
        Add-Artifact "local-install-smoke-peer-install-replay-cache" $config.peer_install_replay_cache_path
        Add-Artifact "local-install-smoke-endpoint-registry" $endpointRegistryPath

        return [pscustomobject]@{
            port = $localInstallPort
            peer = $RemotePeer
            connect_action = $connect.data.action
            lifecycle_state = $connect.data.lifecycle_state
            install_mode = $connect.data.install_mode
            installed = [bool] $connect.data.manual_sa_install.installed
            install_exit_code = $connect.data.manual_sa_install.exit_code
            plan_file_deleted = [bool] $connect.data.manual_sa_install.plan_file_deleted
            cleanup_method = $cleanup.data.cleanup_method
            cleanup_exit_code = $cleanup.data.manual_sa_result.exit_code
            state_dir = $stateDir
            status_session_count = $status.data.session_count
        }
    }
    finally
    {
        if ($process -and -not $process.HasExited)
        {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            $process.WaitForExit(5000) | Out-Null
        }
    }
}

function New-SoakSummaryReport
{
    if ($Script:SoakResults.Count -eq 0)
    {
        return [pscustomobject]@{
            count = 0
            csv_path = $null
            markdown_path = $null
        }
    }

    $csvPath = Join-Path $ReportDir "windows-exe-soak-$RunId.csv"
    $mdPath = Join-Path $ReportDir "windows-exe-soak-$RunId.md"
    $Script:SoakResults | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding UTF8

    $durations = @($Script:SoakResults | ForEach-Object { [double] $_.duration_ms } | Sort-Object)
    $count = $durations.Count
    $p50Index = [int] [Math]::Min($count - 1, [Math]::Floor(($count - 1) * 0.50))
    $p95Index = [int] [Math]::Min($count - 1, [Math]::Floor(($count - 1) * 0.95))
    $p50 = $durations[$p50Index]
    $p95 = $durations[$p95Index]
    $avg = ($durations | Measure-Object -Average).Average
    $max = ($durations | Measure-Object -Maximum).Maximum
    $min = ($durations | Measure-Object -Minimum).Minimum
    $outlierThreshold = [Math]::Max($p95 * 1.5, $avg * 2)
    $outliers = @($Script:SoakResults | Where-Object { [double] $_.duration_ms -gt $outlierThreshold })

    $lines = @(
        "# Windows EXE soak $RunId",
        "",
        "- count: $count",
        "- min_ms: $([int] $min)",
        "- avg_ms: $([int] $avg)",
        "- p50_ms: $([int] $p50)",
        "- p95_ms: $([int] $p95)",
        "- max_ms: $([int] $max)",
        "- outlier_threshold_ms: $([int] $outlierThreshold)",
        "- outliers: $($outliers.Count)",
        "",
        "CSV: $csvPath"
    )
    $lines | Set-Content -LiteralPath $mdPath -Encoding UTF8
    Add-Artifact "soak-csv" $csvPath
    Add-Artifact "soak-markdown" $mdPath

    return [pscustomobject]@{
        count = $count
        min_ms = [int] $min
        avg_ms = [int] $avg
        p50_ms = [int] $p50
        p95_ms = [int] $p95
        max_ms = [int] $max
        outlier_threshold_ms = [int] $outlierThreshold
        outlier_count = $outliers.Count
        csv_path = $csvPath
        markdown_path = $mdPath
    }
}

function New-DissertationEvidenceReport
{
    $aikeReportPath = Join-Path $ManualSaDir "bin\aike-protocol0-proof-report.json"
    if (-not (Test-Path -LiteralPath $aikeReportPath))
    {
        throw "AIKE Protocol=0 proof report not found: $aikeReportPath"
    }

    $report = Get-Content -LiteralPath $aikeReportPath -Raw | ConvertFrom-Json
    $proof = $report.proof
    $aike = $report.aike
    $counters = $proof.counters
    $checks = $proof.checks
    $liveStep = @($Script:Steps | Where-Object { $_.name -eq "aike-live-proof" -and $_.ok } | Select-Object -Last 1)
    $durationMs = if ($liveStep.Count -gt 0 -and $liveStep[-1].result) { $liveStep[-1].result.duration_ms } else { $null }

    $csvPath = Join-Path $ReportDir "dissertation-evidence-$RunId.csv"
    $mdPath = Join-Path $ReportDir "dissertation-evidence-$RunId.md"
    $latestCsv = Join-Path $ReportDir "dissertation-evidence-latest.csv"
    $latestMd = Join-Path $ReportDir "dissertation-evidence-latest.md"

    $row = [pscustomobject]@{
        run_id = $report.run_id
        created_at = $report.created_at
        scenario = "AIKE-derived Windows manual-SA Protocol=0"
        local = $report.local
        remote = $report.remote
        local_peer_id = $report.local_peer_id
        remote_peer_id = $report.remote_peer_id
        aike_session_id = $aike.session_id
        aike_transcript_hash = $aike.transcript_hash
        esp_profile = $aike.esp_profile
        protocol = $proof.protocol
        host_spi = $proof.host_spi
        peer_spi = $proof.peer_spi
        duration_ms = $durationMs
        icmp_esp_packet_lines = $counters.icmp_esp_packet_lines
        clear_icmp_packet_lines = $counters.clear_icmp_packet_lines
        data_esp_packet_lines = $counters.data_esp_packet_lines
        data_clear_tcp_packet_lines = $counters.data_clear_tcp_test_packet_lines
        data_clear_udp_packet_lines = $counters.data_clear_udp_test_packet_lines
        control_clear_tcp_packet_lines = $counters.control_clear_tcp_packet_lines
        control_esp_packet_lines = $counters.control_esp_packet_lines
        icmp_data_plane_esp_confirmed = $checks.icmp_data_plane_esp_confirmed
        tcp_probe_ok = $checks.tcp_probe_ok
        udp_probe_ok = $checks.udp_probe_ok
        tcp_udp_data_plane_esp_confirmed = $checks.tcp_udp_data_plane_esp_confirmed
        control_plane_bypass_confirmed = $checks.control_plane_bypass_confirmed
        report_path = [System.IO.Path]::GetFullPath($aikeReportPath)
        run_dir = $proof.run_dir
    }

    $row | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding UTF8
    Copy-Item -LiteralPath $csvPath -Destination $latestCsv -Force

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# Dissertation Evidence: Windows AIKE -> ESP")
    $lines.Add("")
    $lines.Add("- generated_at: $((Get-Date).ToString("o"))")
    $lines.Add("- scenario: AIKE-derived Windows manual-SA Protocol=0")
    $lines.Add("- local: $($report.local) ($($report.local_peer_id))")
    $lines.Add("- remote: $($report.remote) ($($report.remote_peer_id))")
    $lines.Add("- session_id: $($aike.session_id)")
    $lines.Add("- transcript_hash: $($aike.transcript_hash)")
    $lines.Add("- esp_profile: $($aike.esp_profile)")
    $lines.Add("- protocol: $($proof.protocol)")
    $lines.Add("- host_spi: $($proof.host_spi)")
    $lines.Add("- peer_spi: $($proof.peer_spi)")
    if ($durationMs)
    {
        $lines.Add("- live_proof_duration_ms: $durationMs")
    }
    $lines.Add("")
    $lines.Add("## Packet Evidence")
    $lines.Add("")
    $lines.Add("| Metric | Value |")
    $lines.Add("| --- | ---: |")
    $lines.Add("| ICMP ESP packet lines | $($counters.icmp_esp_packet_lines) |")
    $lines.Add("| Clear ICMP packet lines | $($counters.clear_icmp_packet_lines) |")
    $lines.Add("| TCP/UDP data ESP packet lines | $($counters.data_esp_packet_lines) |")
    $lines.Add("| Clear TCP data packet lines | $($counters.data_clear_tcp_test_packet_lines) |")
    $lines.Add("| Clear UDP data packet lines | $($counters.data_clear_udp_test_packet_lines) |")
    $lines.Add("| Control-plane clear TCP packet lines | $($counters.control_clear_tcp_packet_lines) |")
    $lines.Add("| Control-plane ESP packet lines | $($counters.control_esp_packet_lines) |")
    $lines.Add("")
    $lines.Add("## Checks")
    $lines.Add("")
    $lines.Add("- ICMP data-plane ESP confirmed: $($checks.icmp_data_plane_esp_confirmed)")
    $lines.Add("- TCP probe OK: $($checks.tcp_probe_ok)")
    $lines.Add("- UDP probe OK: $($checks.udp_probe_ok)")
    $lines.Add("- TCP/UDP data-plane ESP confirmed: $($checks.tcp_udp_data_plane_esp_confirmed)")
    $lines.Add("- Control-plane bypass confirmed: $($checks.control_plane_bypass_confirmed)")
    $lines.Add("")
    $lines.Add("## Source Artifacts")
    $lines.Add("")
    $lines.Add("- AIKE proof JSON: $([System.IO.Path]::GetFullPath($aikeReportPath))")
    $lines.Add("- AIKE proof Markdown: $([System.IO.Path]::GetFullPath((Join-Path $ManualSaDir "bin\aike-protocol0-proof-report.md")))")
    $lines.Add("- Run directory: $($proof.run_dir)")
    $lines.Add("- CSV row: $csvPath")
    $lines.Add("")
    $lines.Add("Conclusion: AIKE/X25519/HKDF-derived directional ESP keys were installed into Windows manual-SA. ICMP/TCP/UDP data-plane traffic is observed as ESP, while the control-plane port remains clear and explicitly bypassed.")

    $lines | Set-Content -LiteralPath $mdPath -Encoding UTF8
    Copy-Item -LiteralPath $mdPath -Destination $latestMd -Force

    Add-Artifact "dissertation-evidence-csv" $csvPath
    Add-Artifact "dissertation-evidence-markdown" $mdPath
    Add-Artifact "dissertation-evidence-latest-csv" $latestCsv
    Add-Artifact "dissertation-evidence-latest-markdown" $latestMd

    return [pscustomobject]@{
        csv_path = $csvPath
        markdown_path = $mdPath
        latest_csv = $latestCsv
        latest_markdown = $latestMd
        source_report = [System.IO.Path]::GetFullPath($aikeReportPath)
        control_plane_bypass_confirmed = $checks.control_plane_bypass_confirmed
        data_plane_esp_confirmed = $checks.tcp_udp_data_plane_esp_confirmed
    }
}

function New-RegressionCheckpoint
{
    $stamp = (Get-Date).ToString("yyyyMMdd-HHmmss")
    $destination = Join-Path $CheckpointsDir "windows-exe-regression-$stamp.zip"
    $items = @(
        (Join-Path $ClientDir "run-windows-exe-regression.ps1"),
        (Join-Path $ClientDir "README.md"),
        (Join-Path $ClientDir "config.example.json"),
        (Join-Path $ClientDir "AdaptiveClient.Cli\Program.cs"),
        (Join-Path $ClientDir "AdaptiveClient.Cli\bin\Debug\net9.0\AdaptiveClient.Cli.exe"),
        (Join-Path $ClientDir "AdaptiveClient.Cli\bin\Debug\net9.0\AdaptiveClient.Cli.dll"),
        (Join-Path $ClientDir "AdaptiveClient.Cli\bin\Debug\net9.0\BouncyCastle.Crypto.dll"),
        (Join-Path $ClientDir "AdaptiveClient.Service\Program.cs"),
        (Join-Path $ClientDir "AdaptiveClient.Service\bin\Debug\net9.0\AdaptiveClient.Service.exe"),
        (Join-Path $ClientDir "AdaptiveClient.Service\bin\Debug\net9.0\AdaptiveClient.Service.dll"),
        (Join-Path $ClientDir "AdaptiveClient.Service\bin\Debug\net9.0\BouncyCastle.Crypto.dll"),
        (Join-Path $ClientDir "AdaptiveClient.Core\AikeManualSaKeyExporter.cs"),
        (Join-Path $ClientDir "AdaptiveClient.Core\AikeMessageCodecSmoke.cs"),
        (Join-Path $ClientDir "AdaptiveClient.Core\PeerInstallEnvelope.cs"),
        (Join-Path $ClientDir "AdaptiveClient.Core\Crypto\Ed25519Identity.cs"),
        (Join-Path $ClientDir "AdaptiveClient.Core\Crypto\X25519HkdfLabAikeCrypto.cs"),
        (Join-Path $ClientDir "AdaptiveClient.Core\bin\Debug\net9.0\AdaptiveClient.Core.dll"),
        (Join-Path $ClientDir "AdaptiveClient.Core\bin\Debug\net9.0\BouncyCastle.Crypto.dll"),
        (Join-Path $ManualSaDir "README.md"),
        (Join-Path $ManualSaDir "run-aike-protocol0-proof-suite.ps1"),
        (Join-Path $ManualSaDir "run-protocol0-proof-suite.ps1"),
        (Join-Path $ManualSaDir "run-host-clean-cycle-elevated.ps1"),
        (Join-Path $ManualSaDir "run-live-gate.ps1"),
        (Join-Path $ManualSaDir "bin\manual-sa.exe"),
        (Join-Path $ManualSaDir "bin\aike-protocol0-proof-report.json"),
        (Join-Path $ManualSaDir "bin\aike-protocol0-proof-report.md"),
        (Join-Path $ReportDir "windows-exe-regression-latest.json"),
        (Join-Path $ReportDir "windows-exe-regression-latest.md")
    ) | Where-Object { Test-Path -LiteralPath $_ }

    $items += @($Script:Artifacts | ForEach-Object { $_.path } | Where-Object { Test-Path -LiteralPath $_ })

    $redacted = Get-ChildItem -LiteralPath (Join-Path $ManualSaDir "bin") -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like "aike-key-export-redacted-*.json" } |
        Select-Object -Last 3
    $items += $redacted.FullName

    Compress-Archive -LiteralPath $items -DestinationPath $destination -Force
    $hash = Get-FileHash -Algorithm SHA256 -LiteralPath $destination
    $item = Get-Item -LiteralPath $destination

    return [pscustomobject]@{
        path = $item.FullName
        length = $item.Length
        sha256 = $hash.Hash
    }
}

function Write-RegressionReports
{
    param([object] $CheckpointResult)

    $endedAt = Get-Date
    $ok = $Script:Failures.Count -eq 0
    $report = [pscustomobject]@{
        run_id = $RunId
        started_at = $Script:StartedAt.ToString("o")
        ended_at = $endedAt.ToString("o")
        duration_ms = [int64] (($endedAt - $Script:StartedAt).TotalMilliseconds)
        ok = $ok
        modes = [pscustomobject]@{
            quick = [bool] $Quick
            live = [bool] $Live
            soak = [bool] $Soak
            checkpoint = [bool] $Checkpoint
            live_preflight = [bool] $LivePreflight
            recover = [bool] $Recover
            service_smoke = [bool] $ServiceSmoke
            soak_report = [bool] $SoakReport
            live_full = [bool] $LiveFull
            dissertation_report = [bool] $DissertationReport
            soak_count = $SoakCount
        }
        lab = [pscustomobject]@{
            local = $Local
            remote = $Remote
            local_peer = $LocalPeer
            remote_peer = $RemotePeer
            helper_listen_host = $HelperListenHost
            helper_port = $HelperPort
            control_plane_ports = $ControlPlanePorts
            tcp_port = $TcpPort
            udp_port = $UdpPort
        }
        steps = $Script:Steps
        failures = $Script:Failures
        artifacts = $Script:Artifacts
        checkpoint_result = $CheckpointResult
    }

    $jsonPath = Join-Path $ReportDir "windows-exe-regression-$RunId.json"
    $mdPath = Join-Path $ReportDir "windows-exe-regression-$RunId.md"
    $latestJson = Join-Path $ReportDir "windows-exe-regression-latest.json"
    $latestMd = Join-Path $ReportDir "windows-exe-regression-latest.md"

    $report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $jsonPath -Encoding UTF8
    Copy-Item -LiteralPath $jsonPath -Destination $latestJson -Force

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# Windows EXE regression $RunId")
    $lines.Add("")
    $lines.Add("- ok: $ok")
    $lines.Add("- started: $($report.started_at)")
    $lines.Add("- duration_ms: $($report.duration_ms)")
    $lines.Add("- modes: quick=$Quick live_full=$LiveFull live_preflight=$LivePreflight recover=$Recover service_smoke=$ServiceSmoke local_install_smoke=$LocalInstallSmoke live=$Live soak=$Soak dissertation_report=$DissertationReport checkpoint=$Checkpoint")
    $lines.Add("")
    $lines.Add("## Steps")
    foreach ($step in $Script:Steps)
    {
        $status = if ($step.ok) { "PASS" } else { "FAIL" }
        $lines.Add("- $status $($step.name) ($($step.duration_ms) ms)")
        if (-not $step.ok)
        {
            $lines.Add("  error: $($step.error)")
        }
    }

    if ($CheckpointResult)
    {
        $lines.Add("")
        $lines.Add("## Checkpoint")
        $lines.Add("- path: $($CheckpointResult.path)")
        $lines.Add("- length: $($CheckpointResult.length)")
        $lines.Add("- sha256: $($CheckpointResult.sha256)")
    }

    if ($Script:Artifacts.Count -gt 0)
    {
        $lines.Add("")
        $lines.Add("## Artifacts")
        foreach ($artifact in $Script:Artifacts)
        {
            $lines.Add("- $($artifact.kind): $($artifact.path)")
        }
    }

    $lines | Set-Content -LiteralPath $mdPath -Encoding UTF8
    Copy-Item -LiteralPath $mdPath -Destination $latestMd -Force

    Write-Host "regression_report=$jsonPath"
    Write-Host "regression_markdown=$mdPath"
    Write-Host "regression_ok=$ok"

    return $report
}

try
{
    if ($Quick)
    {
        Invoke-RegressionStep "build-core" { Invoke-NativeCommand "dotnet" @("build", $CoreProject, "--no-restore") } | Out-Null
        Invoke-RegressionStep "build-cli" { Invoke-NativeCommand "dotnet" @("build", $CliProject, "--no-restore") } | Out-Null
        Invoke-RegressionStep "build-service" { Invoke-NativeCommand "dotnet" @("build", $ServiceProject, "--no-restore") } | Out-Null
        Invoke-RegressionStep "powershell-parser" {
            Test-PowerShellSyntax @(
                (Join-Path $ClientDir "run-windows-exe-regression.ps1"),
                (Join-Path $ManualSaDir "run-aike-protocol0-proof-suite.ps1"),
                (Join-Path $ManualSaDir "run-protocol0-proof-suite.ps1"),
                (Join-Path $ManualSaDir "run-host-clean-cycle-elevated.ps1"),
                (Join-Path $ManualSaDir "run-live-gate.ps1"),
                (Join-Path $ManualSaDir "test-live-gate-preflight.ps1"),
                (Join-Path $ManualSaDir "test-validate-plan.ps1"),
                (Join-Path $ManualSaDir "test-validate-cleanup-plan.ps1"),
                (Join-Path $ClientDir "diagnose-u13-live-gate.ps1")
            )
        } | Out-Null
        Invoke-RegressionStep "aike-key-export-directional" { Test-AikeKeyExport } | Out-Null
        Invoke-RegressionStep "manual-sa-validate-plan-regression" { Invoke-NativeCommand "powershell.exe" @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $ManualSaDir "test-validate-plan.ps1")) } | Out-Null
        Invoke-RegressionStep "manual-sa-validate-cleanup-regression" { Invoke-NativeCommand "powershell.exe" @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $ManualSaDir "test-validate-cleanup-plan.ps1")) } | Out-Null
        Invoke-RegressionStep "aike-report-redaction-scan" { Test-ReportRedaction } | Out-Null
    }

    if ($LivePreflight)
    {
        Invoke-RegressionStep "live-preflight" { Test-LivePreflight -RequireAdmin:$false } | Out-Null
    }

    if ($Recover)
    {
        Invoke-RegressionStep "recover-lab-state" { Invoke-RecoverLabState } | Out-Null
        Invoke-RegressionStep "live-preflight-after-recover" { Test-LivePreflight -RequireAdmin:$false } | Out-Null
    }

    if ($ServiceSmoke)
    {
        Invoke-RegressionStep "service-smoke" { Invoke-ServiceSmoke } | Out-Null
        Invoke-RegressionStep "service-to-service-smoke" { Invoke-ServiceToServiceSmoke } | Out-Null
    }

    if ($LocalInstallSmoke)
    {
        Invoke-RegressionStep "local-install-smoke" { Invoke-LocalInstallSmoke } | Out-Null
    }

    if ($Live -or $Soak)
    {
        Invoke-RegressionStep "live-preflight-required" { Test-LivePreflight -RequireAdmin:$true } | Out-Null
    }

    if ($Live)
    {
        Invoke-RegressionStep "aike-live-proof" { Invoke-AikeLiveProof } | Out-Null
        Invoke-RegressionStep "aike-report-redaction-scan-after-live" { Test-ReportRedaction } | Out-Null
    }

    if ($Soak)
    {
        $soakResults = New-Object System.Collections.Generic.List[object]
        for ($i = 1; $i -le $SoakCount; $i++)
        {
            $result = Invoke-RegressionStep "aike-live-proof-soak-$i-of-$SoakCount" { Invoke-AikeLiveProof }
            if ($result)
            {
                $soakResults.Add($result)
                $Script:SoakResults.Add([pscustomobject]@{
                    iteration = $i
                    duration_ms = $result.duration_ms
                    session_id = $result.session_id
                    transcript_hash = $result.transcript_hash
                    host_spi = $result.host_spi
                    peer_spi = $result.peer_spi
                    icmp_esp_packet_lines = $result.icmp_esp_packet_lines
                    data_esp_packet_lines = $result.data_esp_packet_lines
                    control_clear_tcp_packet_lines = $result.control_clear_tcp_packet_lines
                    control_esp_packet_lines = $result.control_esp_packet_lines
                    report_path = $result.report_path
                })
            }
        }

        Invoke-RegressionStep "aike-live-proof-soak-summary" {
            $durations = @($soakResults | ForEach-Object { [double] $_.duration_ms })
            if ($durations.Count -eq 0)
            {
                throw "soak produced no successful results"
            }

            [pscustomobject]@{
                count = $durations.Count
                min_ms = [int] (($durations | Measure-Object -Minimum).Minimum)
                max_ms = [int] (($durations | Measure-Object -Maximum).Maximum)
                avg_ms = [int] (($durations | Measure-Object -Average).Average)
            }
        } | Out-Null
        if ($SoakReport)
        {
            Invoke-RegressionStep "aike-live-proof-soak-report" { New-SoakSummaryReport } | Out-Null
        }
        Invoke-RegressionStep "aike-report-redaction-scan-after-soak" { Test-ReportRedaction } | Out-Null
    }

    if ($DissertationReport)
    {
        Invoke-RegressionStep "dissertation-evidence-report" { New-DissertationEvidenceReport } | Out-Null
    }

    $checkpointResult = $null
    if ($Checkpoint)
    {
        Invoke-RegressionStep "checkpoint-redaction-scan" { Test-ReportRedaction } | Out-Null
        $checkpointResult = Invoke-RegressionStep "checkpoint-create" { New-RegressionCheckpoint }
    }

    $report = Write-RegressionReports $checkpointResult
    if (-not $report.ok)
    {
        exit 1
    }

    exit 0
}
catch
{
    if ($Script:Failures.Count -eq 0)
    {
        $Script:Failures.Add([pscustomobject]@{
            name = "fatal"
            ok = $false
            duration_ms = 0
            error = $_.Exception.Message
            result = $null
        })
    }

    Write-RegressionReports $null | Out-Null
    Write-Error $_
    exit 1
}
