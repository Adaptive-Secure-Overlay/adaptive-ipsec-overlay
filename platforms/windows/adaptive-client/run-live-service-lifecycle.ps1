param(
    [string] $LocalIp = "192.168.95.1",
    [string] $RemoteIp = "192.168.113.199",
    [string] $HelperListenHost = "192.168.95.1",
    [int] $HelperPort = 18091,
    [int] $HostPort = 19030,
    [int] $U13Port = 19031,
    [int] $U13DeployWaitSeconds = 420,
    [int] $U13StatusWaitSeconds = 180,
    [string] $LabGateway = "192.168.95.130",
    [string] $LabInterfaceAlias = "VMware Network Adapter VMnet8",
    [int] $ProgressPercent = 66,
    [switch] $PrepareOnly,
    [switch] $SkipBuild,
    [switch] $SkipPackage,
    [switch] $SkipU13Deploy,
    [switch] $SkipHostService,
    [switch] $NoConnect,
    [switch] $KeepSa,
    [switch] $KeepHostService,
    [switch] $AllowSleep
)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $PSCommandPath
$WorkspaceRoot = (Resolve-Path (Join-Path $ScriptDir "..\..")).Path
$ManualSaDir = Join-Path $WorkspaceRoot "windows-agent\experiments\manual-sa"
$RunId = Get-Date -Format "yyyyMMdd-HHmmss"
$OutDir = Join-Path $ScriptDir "bin\live-service-lifecycle"
$RunDir = Join-Path $OutDir $RunId
$ReportPath = Join-Path $RunDir "live-service-lifecycle.json"
$MarkdownPath = Join-Path $RunDir "live-service-lifecycle.md"
$LatestReportPath = Join-Path $OutDir "live-service-lifecycle-latest.json"
$LatestMarkdownPath = Join-Path $OutDir "live-service-lifecycle-latest.md"

$ServiceProject = Join-Path $ScriptDir "AdaptiveClient.Service\AdaptiveClient.Service.csproj"
$CliProject = Join-Path $ScriptDir "AdaptiveClient.Cli\AdaptiveClient.Cli.csproj"
$PackageScript = Join-Path $ScriptDir "New-LiveServicePackage.ps1"
$ServicePackage = Join-Path $ScriptDir "bin\live-service-package\adaptive-client-service-live.zip"
$InvokeU13Job = Join-Path $ManualSaDir "invoke-u13-agent-job.ps1"
$U13StartScript = Join-Path $ManualSaDir "u13-start-adaptive-service.ps1"
$U13StatusScript = Join-Path $ManualSaDir "u13-collect-adaptive-service-status.ps1"
$HostServiceExe = Join-Path $ScriptDir "AdaptiveClient.Service\bin\Debug\net9.0\AdaptiveClient.Service.exe"
$CliExe = Join-Path $ScriptDir "AdaptiveClient.Cli\bin\Debug\net9.0\AdaptiveClient.Cli.exe"
$ManualSaExe = Join-Path $ManualSaDir "bin\manual-sa.exe"
$HostConfig = Join-Path $RunDir "host-service-config.json"
$HostLog = Join-Path $RunDir "host-service-events.jsonl"
$HostStdout = Join-Path $RunDir "host-service-stdout.log"
$HostStderr = Join-Path $RunDir "host-service-stderr.log"
$HostPid = Join-Path $RunDir "host-service.pid"
$Steps = New-Object System.Collections.Generic.List[object]
$Artifacts = New-Object System.Collections.Generic.List[object]
$HostProcess = $null
$SleepHeld = $false
$HostPinnedPublicIdentityKey = ""
$U13PinnedPublicIdentityKey = ""

New-Item -ItemType Directory -Force -Path $RunDir | Out-Null

function Enable-NoSleep {
    if ("AdaptiveClient.LiveService.NativeMethods" -as [type]) {
        return
    }

    Add-Type -Namespace AdaptiveClient.LiveService -Name NativeMethods -MemberDefinition @"
[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
public static extern uint SetThreadExecutionState(uint esFlags);
"@
}

function Set-NoSleep {
    param([Parameter(Mandatory)] [bool] $Enabled)

    Enable-NoSleep
    $ES_CONTINUOUS = [uint32] 2147483648
    $ES_SYSTEM_REQUIRED = [uint32] 0x00000001
    $ES_DISPLAY_REQUIRED = [uint32] 0x00000002

    if ($Enabled) {
        [AdaptiveClient.LiveService.NativeMethods]::SetThreadExecutionState($ES_CONTINUOUS -bor $ES_SYSTEM_REQUIRED -bor $ES_DISPLAY_REQUIRED) | Out-Null
        return
    }

    [AdaptiveClient.LiveService.NativeMethods]::SetThreadExecutionState($ES_CONTINUOUS) | Out-Null
}

function Test-IsAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Add-Artifact {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [string] $Path
    )

    if (Test-Path -LiteralPath $Path) {
        $item = Get-Item -LiteralPath $Path
        $Artifacts.Add([ordered]@{
            name = $Name
            path = [System.IO.Path]::GetFullPath($Path)
            length = $(if ($item.PSIsContainer) { $null } else { [int64] $item.Length })
        })
    }
}

function Invoke-Step {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [scriptblock] $Body,
        [switch] $ContinueOnError
    )

    Write-Host "live_step_start=$Name"
    $started = Get-Date
    try {
        $result = & $Body
        $ended = Get-Date
        $Steps.Add([ordered]@{
            name = $Name
            ok = $true
            duration_ms = [int64](($ended - $started).TotalMilliseconds)
            result = $result
            error = $null
        })
        Write-Host "live_step_ok=$Name"
        return $result
    } catch {
        $ended = Get-Date
        $Steps.Add([ordered]@{
            name = $Name
            ok = $false
            duration_ms = [int64](($ended - $started).TotalMilliseconds)
            result = $null
            error = $_.Exception.Message
        })
        Write-Host "live_step_failed=$Name"
        Write-Host ("live_step_error=" + $_.Exception.Message)
        if (-not $ContinueOnError) {
            throw
        }
        return $null
    }
}

function Test-PingQuiet {
    param([Parameter(Mandatory)] [string] $Address)

    $output = & ping.exe "-n" "2" "-w" "1000" $Address 2>&1
    if ($LASTEXITCODE -eq 0) {
        return $true
    }
    return @($output | Where-Object { $_.ToString() -match 'TTL=' }).Count -gt 0
}

function Test-TcpPort {
    param(
        [Parameter(Mandatory)] [string] $Address,
        [Parameter(Mandatory)] [int] $Port,
        [int] $TimeoutMs = 1000
    )

    try {
        $client = [Net.Sockets.TcpClient]::new()
        $async = $client.BeginConnect($Address, $Port, $null, $null)
        $ok = $async.AsyncWaitHandle.WaitOne($TimeoutMs)
        if ($ok) {
            $client.EndConnect($async)
        }
        $client.Close()
        return $ok
    } catch {
        return $false
    }
}

function Get-Remote24Network {
    param([Parameter(Mandatory)] [string] $Address)

    $parts = $Address -split "\."
    if ($parts.Count -ne 4) {
        throw "Cannot derive /24 network from address: $Address"
    }
    return ($parts[0..2] -join ".") + ".0"
}

function Ensure-HostRoute {
    param(
        [Parameter(Mandatory)] [string] $Network,
        [Parameter(Mandatory)] [string] $Mask,
        [Parameter(Mandatory)] [string] $Gateway,
        [Parameter(Mandatory)] [string] $InterfaceAlias
    )

    if (-not (Test-IsAdmin)) {
        return [ordered]@{
            skipped = $true
            reason = "not_admin"
            network = $Network
            gateway = $Gateway
            interface_alias = $InterfaceAlias
        }
    }

    $interface = Get-NetIPInterface -AddressFamily IPv4 -InterfaceAlias $InterfaceAlias -ErrorAction Stop |
        Select-Object -First 1
    $ifIndex = [int]$interface.InterfaceIndex

    $deleteOutput = & route.exe delete $Network 2>&1
    $deleteExit = $LASTEXITCODE
    $addOutput = & route.exe add $Network mask $Mask $Gateway IF $ifIndex -p 2>&1
    $addExit = $LASTEXITCODE
    $printOutput = & route.exe print $Network 2>&1

    if ($addExit -ne 0) {
        throw "route add failed exit=$addExit output=$(($addOutput | ForEach-Object { $_.ToString() }) -join ' | ')"
    }

    return [ordered]@{
        skipped = $false
        network = $Network
        mask = $Mask
        gateway = $Gateway
        interface_alias = $InterfaceAlias
        interface_index = $ifIndex
        route_delete_exit = $deleteExit
        route_add_exit = $addExit
        route_delete_tail = (($deleteOutput | Select-Object -Last 20 | ForEach-Object { $_.ToString() }) -join "`n")
        route_add_tail = (($addOutput | Select-Object -Last 20 | ForEach-Object { $_.ToString() }) -join "`n")
        route_print_tail = (($printOutput | Select-Object -Last 80 | ForEach-Object { $_.ToString() }) -join "`n")
    }
}

function Invoke-HostManualSaPrecleanup {
    param(
        [Parameter(Mandatory)] [string] $Local,
        [Parameter(Mandatory)] [string] $Remote
    )

    if (-not (Test-Path -LiteralPath $ManualSaExe)) {
        return [ordered]@{
            skipped = $true
            reason = "manual-sa.exe missing before build"
            path = $ManualSaExe
        }
    }

    $items = [System.Collections.Generic.List[object]]::new()
    foreach ($protocol in @("0", "icmp")) {
        $output = & $ManualSaExe cleanup --local $Local --remote $Remote --protocol $protocol 2>&1
        $items.Add([ordered]@{
            protocol = $protocol
            exit_code = [int]$LASTEXITCODE
            output_tail = (($output | Select-Object -Last 60 | ForEach-Object { $_.ToString() }) -join "`n")
        })
    }

    return [ordered]@{
        skipped = $false
        local = $Local
        remote = $Remote
        protocols = @($items)
    }
}

function Wait-TcpPort {
    param(
        [Parameter(Mandatory)] [string] $Address,
        [Parameter(Mandatory)] [int] $Port,
        [int] $Seconds = 30
    )

    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-TcpPort -Address $Address -Port $Port -TimeoutMs 700) {
            return $true
        }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

function Invoke-U13StatusCollector {
    $statusResult = Join-Path $RunDir "u13-status-result.txt"
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $InvokeU13Job `
        -ScriptPath $U13StatusScript `
        -HelperListenHost $HelperListenHost `
        -HelperPort $HelperPort `
        -WaitSeconds $U13StatusWaitSeconds `
        -JobId "Windows11-U13-status-$RunId" `
        -ResultPath $statusResult
    Add-Artifact -Name "u13_status_result" -Path $statusResult
    return [ordered]@{
        result_path = $statusResult
        exists = $(Test-Path -LiteralPath $statusResult)
    }
}

function New-SingleQuotedPowerShellLiteral {
    param([Parameter(Mandatory)] [string] $Value)
    return "'" + ($Value -replace "'", "''") + "'"
}

function Invoke-U13IdentityPin {
    param([Parameter(Mandatory)] [string] $HostPublicIdentityKey)

    if ([string]::IsNullOrWhiteSpace($HostPublicIdentityKey)) {
        throw "Host public identity key is empty; cannot pin U13 service identities"
    }

    $pinScript = Join-Path $RunDir "u13-pin-service-identities.ps1"
    $pinResult = Join-Path $RunDir "u13-pin-service-identities-result.txt"
    $hostKeyLiteral = New-SingleQuotedPowerShellLiteral -Value $HostPublicIdentityKey
    $hostPortList = "$HostPort,$U13Port,$HelperPort,9000"

    @"
`$ErrorActionPreference = "Continue"
`$ProgressPreference = "SilentlyContinue"
`$base = "http://${HelperListenHost}:$HelperPort"
`$dir = "C:\adaptive-client-live"
`$configPath = Join-Path `$dir "config.json"
`$dotnet = Join-Path `$dir "dotnet\dotnet.exe"
`$serviceDll = Join-Path `$dir "app\AdaptiveClient.Service.dll"
`$cliDll = Join-Path `$dir "app\AdaptiveClient.Cli.dll"
`$logPath = Join-Path `$dir "service-events.jsonl"
`$stdoutPath = Join-Path `$dir "service-stdout.log"
`$stderrPath = Join-Path `$dir "service-stderr.log"
`$pidPath = Join-Path `$dir "service.pid"
`$listenPort = $U13Port
`$hostPublicIdentityKey = $hostKeyLiteral
`$lines = New-Object System.Collections.Generic.List[string]
`$pinOk = `$true

function Add-Line([string]`$Text) {
    `$lines.Add(`$Text)
    Write-Host `$Text
    try {
        Invoke-WebRequest -Uri "`$base/agent/log" -Method POST -Body ("u13_identity_pin " + `$Text) -ContentType "text/plain" -UseBasicParsing -TimeoutSec 3 | Out-Null
    } catch {}
}

function Set-JsonProperty([object]`$Object, [string]`$Name, [object]`$Value) {
    if (`$null -eq `$Object) {
        throw "target object is null for property `$Name"
    }
    `$property = `$Object.PSObject.Properties | Where-Object { `$_.Name -eq `$Name } | Select-Object -First 1
    if (`$null -eq `$property) {
        `$Object | Add-Member -NotePropertyName `$Name -NotePropertyValue `$Value
    } else {
        `$property.Value = `$Value
    }
}

function Get-JsonPropertyValue([object]`$Object, [string]`$Name) {
    if (`$null -eq `$Object) {
        return `$null
    }
    `$property = `$Object.PSObject.Properties | Where-Object { `$_.Name -eq `$Name } | Select-Object -First 1
    if (`$null -eq `$property) {
        return `$null
    }
    return `$property.Value
}

function Wait-Port([int]`$Port, [int]`$Seconds) {
    `$deadline = (Get-Date).AddSeconds(`$Seconds)
    while ((Get-Date) -lt `$deadline) {
        try {
            `$client = [Net.Sockets.TcpClient]::new()
            `$async = `$client.BeginConnect("127.0.0.1", `$Port, `$null, `$null)
            if (`$async.AsyncWaitHandle.WaitOne(300)) {
                `$client.EndConnect(`$async)
                `$client.Close()
                return `$true
            }
            `$client.Close()
        } catch {}
        Start-Sleep -Milliseconds 300
    }
    return `$false
}

Add-Line "u13_pin_service_identities"
Add-Line ("time=" + (Get-Date -Format s))
Add-Line ("job_id=" + `$env:ADAPTIVE_AGENT_JOB_ID)

try {
    `$statusRaw = & `$dotnet `$cliDll status --port `$listenPort --json 2>&1
    `$statusText = (`$statusRaw -join "`n")
    Add-Line ("status_exit=" + `$LASTEXITCODE)
    if (`$LASTEXITCODE -ne 0) {
        throw "U13 local status failed: `$statusText"
    }
    `$status = `$statusText | ConvertFrom-Json
    `$u13PublicIdentityKey = [string]`$status.data.identity.local_public_identity_key
    `$u13KeyId = [string]`$status.data.identity.local_key_id
    Add-Line ("u13_public_identity_key=" + `$u13PublicIdentityKey)
    Add-Line ("u13_key_id=" + `$u13KeyId)
    if (-not `$u13PublicIdentityKey.StartsWith("ed25519:", [StringComparison]::Ordinal)) {
        throw "U13 service did not expose ed25519 public identity key"
    }

    `$config = Get-Content -LiteralPath `$configPath -Raw | ConvertFrom-Json
    if (`$null -eq `$config.identity_public_keys) {
        Set-JsonProperty `$config "identity_public_keys" ([pscustomobject]@{})
    }
    `$identityKeys = Get-JsonPropertyValue `$config "identity_public_keys"
    `$hostPeer = Get-JsonPropertyValue `$config.peers "HostWindows"
    `$u13Peer = Get-JsonPropertyValue `$config.peers "Windows11-U13"
    if (`$null -eq `$hostPeer) { throw "HostWindows peer config missing" }
    if (`$null -eq `$u13Peer) { throw "Windows11-U13 peer config missing" }

    Set-JsonProperty `$identityKeys "HostWindows" `$hostPublicIdentityKey
    Set-JsonProperty `$identityKeys "Windows11-U13" `$u13PublicIdentityKey
    Set-JsonProperty `$hostPeer "public_identity_key" `$hostPublicIdentityKey
    Set-JsonProperty `$u13Peer "public_identity_key" `$u13PublicIdentityKey
    Set-JsonProperty `$config "peer_install_delivery_mode" "disabled"
    Set-JsonProperty `$config "peer_lifecycle_delivery_required" `$false
    Set-JsonProperty `$config "manual_sa_exempt_tcp_ports" "$hostPortList"
    `$config | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath `$configPath -Encoding UTF8
    Add-Line "config_pinned=true"

    if (Test-Path -LiteralPath `$pidPath) {
        `$oldPid = (Get-Content -LiteralPath `$pidPath -Raw).Trim()
        if (`$oldPid -match '^\d+$') {
            Stop-Process -Id ([int]`$oldPid) -Force -ErrorAction SilentlyContinue
            Add-Line ("stopped_previous_pid=" + `$oldPid)
        }
    }
    Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object { `$_.CommandLine -and `$_.CommandLine -match 'AdaptiveClient\.Service\.dll' -and `$_.CommandLine -match [regex]::Escape(`$dir) } |
        ForEach-Object { Stop-Process -Id `$_.ProcessId -Force -ErrorAction SilentlyContinue }

    `$process = Start-Process -FilePath `$dotnet -ArgumentList @(`$serviceDll, "--config", `$configPath, "--log", `$logPath) -PassThru -WindowStyle Hidden -RedirectStandardOutput `$stdoutPath -RedirectStandardError `$stderrPath
    Set-Content -LiteralPath `$pidPath -Value `$process.Id -Encoding ASCII
    Add-Line ("service_pid=" + `$process.Id)
    `$ready = Wait-Port -Port `$listenPort -Seconds 30
    Add-Line ("service_ready=" + `$ready)
    if (-not `$ready) {
        throw "U13 service did not reopen after identity pin"
    }
} catch {
    Add-Line ("pin_error=" + `$_.Exception.Message)
    `$pinOk = `$false
}

`$lines.Add("pin_ok=`$pinOk")
`$body = (`$lines -join "`n")
try {
    Invoke-WebRequest -Uri "`$base/result" -Method POST -Body `$body -ContentType "text/plain" -UseBasicParsing -TimeoutSec 10 | Out-Null
    Add-Line "posted_result=true"
} catch {
    Add-Line ("posted_result=false error=" + `$_.Exception.Message)
}

if (-not `$pinOk) { exit 1 }
exit 0
"@ | Set-Content -LiteralPath $pinScript -Encoding UTF8

    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $InvokeU13Job `
        -ScriptPath $pinScript `
        -HelperListenHost $HelperListenHost `
        -HelperPort $HelperPort `
        -WaitSeconds 180 `
        -JobId "Windows11-U13-pin-identities-$RunId" `
        -ResultPath $pinResult
    $exit = $LASTEXITCODE
    Add-Artifact -Name "u13_identity_pin_script" -Path $pinScript
    Add-Artifact -Name "u13_identity_pin_result" -Path $pinResult
    if ($exit -ne 0) {
        throw "U13 identity pin failed with exit $exit"
    }

    $text = Get-Content -LiteralPath $pinResult -Raw
    $u13Key = ([regex]::Match($text, '(?m)^u13_public_identity_key=(.+)$')).Groups[1].Value.Trim()
    $pinOk = ([regex]::Match($text, '(?m)^pin_ok=(.+)$')).Groups[1].Value.Trim()
    if ($pinOk -ne "True" -or [string]::IsNullOrWhiteSpace($u13Key)) {
        throw "U13 identity pin result did not contain success/public key: $pinResult"
    }

    return [ordered]@{
        result_path = $pinResult
        u13_public_identity_key = $u13Key
        pin_ok = $pinOk
    }
}

function Write-HostServiceConfig {
    $identityPublicKeys = @{}
    if (-not [string]::IsNullOrWhiteSpace($script:HostPinnedPublicIdentityKey)) {
        $identityPublicKeys["HostWindows"] = $script:HostPinnedPublicIdentityKey
    }
    if (-not [string]::IsNullOrWhiteSpace($script:U13PinnedPublicIdentityKey)) {
        $identityPublicKeys["Windows11-U13"] = $script:U13PinnedPublicIdentityKey
    }

    $config = [ordered]@{
        node_id = "HostWindows"
        listen_host = "0.0.0.0"
        listen_port = $HostPort
        manual_sa_path = $ManualSaExe
        manual_sa_install_mode = "local_install"
        manual_sa_exempt_tcp_ports = "$HostPort,$U13Port,$HelperPort,9000"
        manual_sa_state_dir = (Join-Path $RunDir "state")
        manual_sa_session_index_path = (Join-Path $RunDir "state\sessions.index.json")
        peer_install_replay_cache_path = (Join-Path $RunDir "state\peer-install-replay-cache.json")
        identity_store_path = (Join-Path $RunDir "identity.local.json")
        endpoint_registry_path = (Join-Path $RunDir "endpoint-registry.json")
        peer_install_envelope_secret = "lab-peer-install-envelope-secret-change-me"
        peer_install_delivery_mode = "service_to_service"
        peer_lifecycle_delivery_required = $true
        validate_manual_sa_plan = $true
        endpoint_registry = @{
            default_ttl_seconds = 300
            records = @{}
        }
        peers = @{
            "HostWindows" = @{
                endpoint = $LocalIp
                overlay_endpoint = "${LocalIp}:$HostPort"
                public_identity_key = $script:HostPinnedPublicIdentityKey
                allowed_selector = "0"
            }
            "Windows11-U13" = @{
                endpoint = $RemoteIp
                overlay_endpoint = "${RemoteIp}:$U13Port"
                public_identity_key = $script:U13PinnedPublicIdentityKey
                allowed_selector = "0"
            }
        }
        identity_public_keys = $identityPublicKeys
        route_policy = @{
            mode = "direct_live_service"
            hop_count = 0
            candidate_intermediates = @()
        }
        retry_policy = @{
            connect_timeout_ms = 30000
            max_attempts = 3
            backoff_ms = 1000
            cleanup_stale_after_seconds = 120
        }
    }

    $config | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $HostConfig -Encoding UTF8
    Add-Artifact -Name "host_config" -Path $HostConfig
    return $HostConfig
}

function Start-HostService {
    if (-not (Test-Path -LiteralPath $HostServiceExe)) {
        throw "AdaptiveClient.Service.exe not found: $HostServiceExe"
    }
    if (-not (Test-Path -LiteralPath $ManualSaExe)) {
        throw "manual-sa.exe not found: $ManualSaExe"
    }

    Write-HostServiceConfig | Out-Null
    $argumentList = @("--config", $HostConfig, "--log", $HostLog)
    $script:HostProcess = Start-Process -FilePath $HostServiceExe -ArgumentList $argumentList -PassThru -WindowStyle Hidden -RedirectStandardOutput $HostStdout -RedirectStandardError $HostStderr
    Set-Content -LiteralPath $HostPid -Value $script:HostProcess.Id -Encoding ASCII

    $ready = Wait-TcpPort -Address "127.0.0.1" -Port $HostPort -Seconds 30
    if (-not $ready) {
        throw "Host AdaptiveClient.Service did not open 127.0.0.1:$HostPort"
    }

    Add-Artifact -Name "host_log" -Path $HostLog
    Add-Artifact -Name "host_stdout" -Path $HostStdout
    Add-Artifact -Name "host_stderr" -Path $HostStderr
    return [ordered]@{
        pid = $script:HostProcess.Id
        port = $HostPort
        ready = $ready
    }
}

function Stop-HostService {
    if ($script:HostProcess -and -not $script:HostProcess.HasExited) {
        Stop-Process -Id $script:HostProcess.Id -Force -ErrorAction SilentlyContinue
        $script:HostProcess.WaitForExit(5000) | Out-Null
        return [ordered]@{
            stopped = $true
            pid = $script:HostProcess.Id
        }
    }
    if (Test-Path -LiteralPath $HostPid) {
        $pidText = (Get-Content -LiteralPath $HostPid -Raw).Trim()
        if ($pidText -match '^\d+$') {
            Stop-Process -Id ([int]$pidText) -Force -ErrorAction SilentlyContinue
            return [ordered]@{
                stopped = $true
                pid = [int]$pidText
            }
        }
    }
    return [ordered]@{
        stopped = $false
        pid = $null
    }
}

function Invoke-CliJson {
    param([Parameter(Mandatory)] [string[]] $Args)

    if (-not (Test-Path -LiteralPath $CliExe)) {
        throw "AdaptiveClient.Cli.exe not found: $CliExe"
    }

    $output = & $CliExe @Args 2>&1
    $exit = $LASTEXITCODE
    $text = ($output -join "`n")
    $path = Join-Path $RunDir ("cli-" + ($Args[0] -replace '[^a-zA-Z0-9_-]', '_') + ".json")
    $text | Set-Content -LiteralPath $path -Encoding UTF8
    Add-Artifact -Name ("cli_" + $Args[0]) -Path $path

    if ($exit -ne 0) {
        throw "CLI $($Args[0]) failed with exit $exit`: $text"
    }

    try {
        return $text | ConvertFrom-Json
    } catch {
        throw "CLI $($Args[0]) returned non-JSON output: $text"
    }
}

function Write-LiveReport {
    param(
        [Parameter(Mandatory)] [bool] $Ok,
        [string] $ErrorMessage = "",
        [int] $ExitCode = 0
    )

    Add-Artifact -Name "report_json" -Path $ReportPath
    Add-Artifact -Name "report_markdown" -Path $MarkdownPath
    Add-Artifact -Name "service_package" -Path $ServicePackage
    Add-Artifact -Name "host_log" -Path $HostLog
    Add-Artifact -Name "host_stdout" -Path $HostStdout
    Add-Artifact -Name "host_stderr" -Path $HostStderr

    if ($Ok) {
        $effectiveProgress = [int] $ProgressPercent
    } else {
        $effectiveProgress = [int] ([Math]::Min([int] $ProgressPercent, 64))
    }

    if ($Ok -and $PrepareOnly.IsPresent) {
        $nextAction = "Run elevated live lifecycle without -PrepareOnly when U13 is reachable."
    } elseif ($Ok) {
        $nextAction = "Live lifecycle is green. Continue service-side connect/rekey cleanup hardening."
    } else {
        $nextAction = "Open the failed step and u13 status result, then rerun run-live-service-lifecycle.ps1."
    }

    $stepSnapshot = New-Object object[] $Steps.Count
    $Steps.CopyTo($stepSnapshot, 0)
    $artifactSnapshot = New-Object object[] $Artifacts.Count
    $Artifacts.CopyTo($artifactSnapshot, 0)

    $report = [ordered]@{
        ok = $Ok
        exit_code = $ExitCode
        error = $ErrorMessage
        run_id = $RunId
        created_at = (Get-Date).ToString("O")
        estimated_product_progress_percent = $effectiveProgress
        parameters = [ordered]@{
            local_ip = $LocalIp
            remote_ip = $RemoteIp
            helper_listen_host = $HelperListenHost
            helper_port = $HelperPort
            host_port = $HostPort
            u13_port = $U13Port
            prepare_only = $PrepareOnly.IsPresent
            skip_u13_deploy = $SkipU13Deploy.IsPresent
            no_connect = $NoConnect.IsPresent
            keep_sa = $KeepSa.IsPresent
        }
        steps = $stepSnapshot
        artifacts = $artifactSnapshot
        next_action = $nextAction
    }

    $report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $ReportPath -Encoding UTF8
    Copy-Item -LiteralPath $ReportPath -Destination $LatestReportPath -Force

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("# Live service lifecycle")
    $lines.Add("")
    $lines.Add("- ok: $Ok")
    $lines.Add("- exit_code: $ExitCode")
    $lines.Add("- progress_percent: $($report.estimated_product_progress_percent)")
    $lines.Add("- local_ip: $LocalIp")
    $lines.Add("- remote_ip: $RemoteIp")
    if (-not [string]::IsNullOrWhiteSpace($ErrorMessage)) {
        $lines.Add("- error: $ErrorMessage")
    }
    $lines.Add("")
    $lines.Add("## Steps")
    foreach ($step in $Steps) {
        $lines.Add("- $($step.name): ok=$($step.ok), duration_ms=$($step.duration_ms)")
        if ($step.error) {
            $lines.Add("  error: $($step.error)")
        }
    }
    $lines.Add("")
    $lines.Add("## Next")
    $lines.Add($report.next_action)
    $lines | Set-Content -LiteralPath $MarkdownPath -Encoding UTF8
    Copy-Item -LiteralPath $MarkdownPath -Destination $LatestMarkdownPath -Force

    Write-Host "live_service_lifecycle_report=$ReportPath"
    Write-Host "live_service_lifecycle_markdown=$MarkdownPath"
    Write-Host "live_service_lifecycle_ok=$Ok"
    Write-Host "windows_exe_progress_percent=$($report.estimated_product_progress_percent)"
}

$failure = $null
$exitCode = 0

try {
    if (-not $AllowSleep) {
        Set-NoSleep -Enabled $true
        $SleepHeld = $true
        Write-Host "live_no_sleep=enabled"
    }

    Invoke-Step "preflight-local-paths" {
        foreach ($path in @($ServiceProject, $CliProject, $PackageScript, $InvokeU13Job, $U13StartScript, $U13StatusScript)) {
            if (-not (Test-Path -LiteralPath $path)) {
                throw "required path missing: $path"
            }
        }
        [ordered]@{
            is_admin = $(Test-IsAdmin)
            run_dir = $RunDir
        }
    } | Out-Null

    if (-not $SkipBuild) {
        Invoke-Step "build-service" {
            & dotnet build $ServiceProject --no-restore
            if ($LASTEXITCODE -ne 0) { throw "service build failed with exit $LASTEXITCODE" }
            [ordered]@{ project = $ServiceProject }
        } | Out-Null

        Invoke-Step "build-cli" {
            & dotnet build $CliProject --no-restore
            if ($LASTEXITCODE -ne 0) { throw "cli build failed with exit $LASTEXITCODE" }
            [ordered]@{ project = $CliProject }
        } | Out-Null
    }

    if (-not $SkipPackage) {
        Invoke-Step "package-portable-service" {
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $PackageScript -OutputZip $ServicePackage
            if ($LASTEXITCODE -ne 0) { throw "service package failed with exit $LASTEXITCODE" }
            $hash = Get-FileHash -Algorithm SHA256 -LiteralPath $ServicePackage
            Add-Artifact -Name "service_package" -Path $ServicePackage
            [ordered]@{
                path = $ServicePackage
                length = (Get-Item -LiteralPath $ServicePackage).Length
                sha256 = $hash.Hash
            }
        } | Out-Null
    }

    Invoke-Step "preflight-network" {
        $remoteNetwork = Get-Remote24Network -Address $RemoteIp
        $routeFix = Ensure-HostRoute `
            -Network $remoteNetwork `
            -Mask "255.255.255.0" `
            -Gateway $LabGateway `
            -InterfaceAlias $LabInterfaceAlias
        $hostPrecleanup = Invoke-HostManualSaPrecleanup -Local $LocalIp -Remote $RemoteIp
        [ordered]@{
            local_ping_ok = $(Test-PingQuiet -Address $LocalIp)
            remote_ping_ok = $(Test-PingQuiet -Address $RemoteIp)
            u13_service_port_open = $(Test-TcpPort -Address $RemoteIp -Port $U13Port -TimeoutMs 1200)
            host_port_open = $(Test-TcpPort -Address "127.0.0.1" -Port $HostPort -TimeoutMs 500)
            route_fix = $routeFix
            host_manual_sa_precleanup = $hostPrecleanup
        }
    } | Out-Null

    if ($PrepareOnly) {
        Write-Host "live_prepare_only=true"
        $exitCode = 0
        return
    }

    if ((-not (Test-IsAdmin)) -and (-not $SkipHostService) -and (-not $NoConnect)) {
        throw "Administrator shell is required for host-side local_install/manual-SA lifecycle"
    }

    if (-not $SkipU13Deploy) {
        Invoke-Step "deploy-u13-service" {
            $u13Result = Join-Path $RunDir "u13-start-service-result.txt"
            try {
                & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $InvokeU13Job `
                    -ScriptPath $U13StartScript `
                    -HelperListenHost $HelperListenHost `
                    -HelperPort $HelperPort `
                    -WaitSeconds $U13DeployWaitSeconds `
                    -JobId "Windows11-U13-start-service-$RunId" `
                    -ResultPath $u13Result `
                    -ServicePackage $ServicePackage
                if ($LASTEXITCODE -ne 0) {
                    throw "U13 deploy job failed with exit $LASTEXITCODE"
                }
                if (Test-Path -LiteralPath $u13Result) {
                    $u13Text = Get-Content -LiteralPath $u13Result -Raw
                    if ($u13Text -match 'job_ok=False') {
                        throw "U13 deploy job returned job_ok=False: $u13Text"
                    }
                }
            } catch {
                Invoke-Step "collect-u13-status-after-deploy-failure" { Invoke-U13StatusCollector } -ContinueOnError | Out-Null
                throw
            }
            Add-Artifact -Name "u13_start_result" -Path $u13Result
            [ordered]@{
                result_path = $u13Result
                exists = $(Test-Path -LiteralPath $u13Result)
            }
        } | Out-Null
    }

    Invoke-Step "verify-u13-service-port" {
        $ready = Wait-TcpPort -Address $RemoteIp -Port $U13Port -Seconds 30
        if (-not $ready) {
            Invoke-Step "collect-u13-status-after-port-failure" { Invoke-U13StatusCollector } -ContinueOnError | Out-Null
            throw "U13 service port $RemoteIp`:$U13Port is not reachable"
        }
        [ordered]@{
            remote = "$RemoteIp`:$U13Port"
            ready = $ready
        }
    } | Out-Null

    if (-not $SkipHostService) {
        Invoke-Step "start-host-service" { Start-HostService } | Out-Null
        $hostStatus = Invoke-Step "host-service-status-initial" {
            Invoke-CliJson -Args @("status", "--config", $HostConfig, "--port", [string] $HostPort, "--json")
        }
        $hostPublicIdentityKey = [string]$hostStatus.data.identity.local_public_identity_key
        if (-not $hostPublicIdentityKey.StartsWith("ed25519:", [StringComparison]::Ordinal)) {
            throw "Host service did not expose ed25519 local_public_identity_key"
        }

        $u13IdentityPin = Invoke-Step "pin-u13-service-identities" {
            Invoke-U13IdentityPin -HostPublicIdentityKey $hostPublicIdentityKey
        }
        $script:HostPinnedPublicIdentityKey = $hostPublicIdentityKey
        $script:U13PinnedPublicIdentityKey = [string]$u13IdentityPin.u13_public_identity_key
        if (-not $script:U13PinnedPublicIdentityKey.StartsWith("ed25519:", [StringComparison]::Ordinal)) {
            throw "U13 identity pin did not return ed25519 public identity key"
        }

        Invoke-Step "restart-host-service-with-pins" {
            Stop-HostService | Out-Null
            Start-Sleep -Milliseconds 500
            Start-HostService
        } | Out-Null

        Invoke-Step "host-service-status-pinned" {
            Invoke-CliJson -Args @("status", "--config", $HostConfig, "--port", [string] $HostPort, "--json")
        } | Out-Null
    }

    if (-not $NoConnect) {
        Invoke-Step "connect-host-to-u13" {
            Invoke-CliJson -Args @("connect", "Windows11-U13", "--config", $HostConfig, "--port", [string] $HostPort, "--json")
        } | Out-Null

        Invoke-Step "ping-through-installed-sa" {
            $output = & ping.exe "-n" "4" "-w" "1000" $RemoteIp 2>&1
            $text = ($output -join "`n")
            $path = Join-Path $RunDir "ping-after-connect.txt"
            $text | Set-Content -LiteralPath $path -Encoding UTF8
            Add-Artifact -Name "ping_after_connect" -Path $path
            if ($LASTEXITCODE -ne 0 -and @($output | Where-Object { $_.ToString() -match 'TTL=' }).Count -eq 0) {
                throw "ping after connect failed: $text"
            }
            [ordered]@{
                output_path = $path
                exit_code = $LASTEXITCODE
            }
        } | Out-Null

        if (-not $KeepSa) {
            Invoke-Step "cleanup-host-to-u13" {
                Invoke-CliJson -Args @("cleanup", "Windows11-U13", "--config", $HostConfig, "--port", [string] $HostPort, "--json")
            } | Out-Null
        }
    }
} catch {
    $failure = $_.Exception.Message
    $exitCode = 1
} finally {
    if ((-not $KeepHostService) -and ($null -ne $HostProcess -or (Test-Path -LiteralPath $HostPid))) {
        Invoke-Step "stop-host-service" { Stop-HostService } -ContinueOnError | Out-Null
    }

    if ($SleepHeld) {
        Set-NoSleep -Enabled $false
        Write-Host "live_no_sleep=disabled"
    }

    $reportOk = ([int] $exitCode) -eq 0
    $reportError = if ($null -eq $failure) { "" } else { [string] $failure }
    Write-LiveReport -Ok $reportOk -ErrorMessage $reportError -ExitCode ([int] $exitCode)
}

exit $exitCode
