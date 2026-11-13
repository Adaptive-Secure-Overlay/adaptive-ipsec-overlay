param(
    [string] $LocalIp = "192.168.95.1",
    [string] $RemoteIp = "192.168.113.199",
    [string] $HelperListenHost = "192.168.95.1",
    [int] $HelperPort = 18091,
    [int] $EveNodeId = 25,
    [int] $BootWaitSeconds = 80,
    [string] $LabGateway = "192.168.95.130",
    [string] $LabInterfaceAlias = "VMware Network Adapter VMnet8",
    [switch] $AutoEve,
    [switch] $RestartU13OnAgentFailure,
    [switch] $AllowVncFallback,
    [switch] $RunLiveLifecycle,
    [int] $ProgressPercent = 70,
    [switch] $SelfCheckOnly
)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $PSCommandPath
$WorkspaceRoot = (Resolve-Path (Join-Path $ScriptDir "..\..")).Path
$ManualSaDir = Join-Path $WorkspaceRoot "windows-agent\experiments\manual-sa"
$RunId = Get-Date -Format "yyyyMMdd-HHmmss"
$OutDir = Join-Path $ScriptDir "bin\live-automation"
$RunDir = Join-Path $OutDir $RunId
$ReportPath = Join-Path $RunDir "ensure-u13-live-automation.json"
$MarkdownPath = Join-Path $RunDir "ensure-u13-live-automation.md"
$LatestReportPath = Join-Path $OutDir "ensure-u13-live-automation-latest.json"
$LatestMarkdownPath = Join-Path $OutDir "ensure-u13-live-automation-latest.md"

$StopStale = Join-Path $ManualSaDir "stop-live-gate-stale-processes.ps1"
$RepairAgent = Join-Path $ManualSaDir "repair-u13-peer-agent.ps1"
$InstallAgentViaVnc = Join-Path $ManualSaDir "install-u13-agent-via-vnc.ps1"
$DebugAgentState = Join-Path $ManualSaDir "u13-debug-agent-state.ps1"
$InvokeAgentJob = Join-Path $ManualSaDir "invoke-u13-agent-job.ps1"
$LiveLifecycle = Join-Path $ScriptDir "run-live-service-lifecycle-elevated.ps1"
$AddU13Route = Join-Path $ScriptDir "add-u13-route-elevated.ps1"
$EveNodeControl = Join-Path $WorkspaceRoot "eve_node_control.py"
$Steps = [System.Collections.Generic.List[object]]::new()

New-Item -ItemType Directory -Force -Path $RunDir | Out-Null

function Write-KeyValue {
    param([string]$Key, [object]$Value)
    Write-Host ("{0}={1}" -f $Key, $Value)
}

function Invoke-Step {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [scriptblock] $Body,
        [switch] $ContinueOnError
    )

    Write-KeyValue "automation_step_start" $Name
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
        Write-KeyValue "automation_step_ok" $Name
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
        Write-KeyValue "automation_step_failed" $Name
        Write-KeyValue "automation_step_error" $_.Exception.Message
        if (-not $ContinueOnError) {
            throw
        }
        return $null
    }
}

function Invoke-Native {
    param(
        [Parameter(Mandatory)] [string] $FilePath,
        [Parameter(Mandatory)] [string[]] $Arguments
    )

    $output = & $FilePath @Arguments 2>&1
    $exit = $LASTEXITCODE
    return [ordered]@{
        exit_code = [int]$exit
        output_tail = (($output | Select-Object -Last 80 | ForEach-Object { $_.ToString() }) -join "`n")
    }
}

function Test-PingQuiet {
    param([Parameter(Mandatory)] [string] $Address)

    $output = & ping.exe "-n" "2" "-w" "1000" $Address 2>&1
    if ($LASTEXITCODE -eq 0) { return $true }
    return @($output | Where-Object { $_.ToString() -match "TTL=" }).Count -gt 0
}

function Invoke-EveAction {
    param([Parameter(Mandatory)] [string] $Action)

    if (-not (Test-Path -LiteralPath $EveNodeControl)) {
        throw "eve_node_control.py not found: $EveNodeControl"
    }

    $python = Get-Command python -ErrorAction SilentlyContinue
    if (-not $python) {
        $python = Get-Command py -ErrorAction SilentlyContinue
    }
    if (-not $python) {
        throw "python/py not found"
    }

    $result = Invoke-Native -FilePath $python.Source -Arguments @($EveNodeControl, $Action, "--node", [string]$EveNodeId)
    if ($result.exit_code -ne 0) {
        throw "EVE $Action failed with exit $($result.exit_code): $($result.output_tail)"
    }
    return $result
}

function Invoke-AgentRepair {
    param([string] $AttemptName)

    $result = Invoke-Native -FilePath "powershell.exe" -Arguments @(
        "-NoProfile", "-ExecutionPolicy", "Bypass",
        "-File", $RepairAgent,
        "-HelperListenHost", $HelperListenHost,
        "-HelperPort", [string]$HelperPort,
        "-WaitSeconds", "180",
        "-VerifyAfterRepair"
    )
    if ($result.exit_code -ne 0) {
        throw "$AttemptName failed with exit $($result.exit_code): $($result.output_tail)"
    }
    return $result
}

function Invoke-AgentDebug {
    $resultPath = Join-Path $RunDir "u13-debug-after-automation.txt"
    $result = Invoke-Native -FilePath "powershell.exe" -Arguments @(
        "-NoProfile", "-ExecutionPolicy", "Bypass",
        "-File", $InvokeAgentJob,
        "-ScriptPath", $DebugAgentState,
        "-HelperListenHost", $HelperListenHost,
        "-HelperPort", [string]$HelperPort,
        "-WaitSeconds", "120",
        "-JobId", "Windows11-U13-automation-debug-$RunId",
        "-ResultPath", $resultPath
    )
    if ($result.exit_code -ne 0) {
        throw "debug job failed with exit $($result.exit_code): $($result.output_tail)"
    }
    return [ordered]@{
        command = $result
        result_path = $resultPath
        result_exists = Test-Path -LiteralPath $resultPath
    }
}

function Write-Report {
    param(
        [Parameter(Mandatory)] [bool] $Ok,
        [string] $ErrorMessage = ""
    )

    $stepSnapshot = New-Object object[] $Steps.Count
    $Steps.CopyTo($stepSnapshot, 0)
    $report = [ordered]@{
        ok = $Ok
        error = $ErrorMessage
        run_id = $RunId
        created_at = (Get-Date).ToString("O")
        local_ip = $LocalIp
        remote_ip = $RemoteIp
        helper_listen_host = $HelperListenHost
        helper_port = $HelperPort
        eve_node_id = $EveNodeId
        auto_eve = [bool]$AutoEve
        restart_u13_on_agent_failure = [bool]$RestartU13OnAgentFailure
        allow_vnc_fallback = [bool]$AllowVncFallback
        run_live_lifecycle = [bool]$RunLiveLifecycle
        steps = $stepSnapshot
        next_action = if ($Ok -and -not $RunLiveLifecycle) {
            ".\windows-agent\adaptive-client\run-live-service-lifecycle-elevated.ps1 -ProgressPercent $ProgressPercent"
        } elseif ($Ok) {
            "Live automation completed; inspect latest live-service-lifecycle report."
        } else {
            "Open this automation report and failed step output before rerunning."
        }
    }
    $report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $ReportPath -Encoding UTF8
    Copy-Item -LiteralPath $ReportPath -Destination $LatestReportPath -Force

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("# U13 live automation")
    $lines.Add("")
    $lines.Add("- ok: $Ok")
    $lines.Add("- error: $ErrorMessage")
    $lines.Add("- run_id: $RunId")
    $lines.Add("- progress_percent_target: $ProgressPercent")
    $lines.Add("")
    $lines.Add("## Steps")
    foreach ($step in $Steps) {
        $lines.Add("- $($step.name): ok=$($step.ok), duration_ms=$($step.duration_ms)")
        if ($step.error) { $lines.Add("  error: $($step.error)") }
    }
    $lines.Add("")
    $lines.Add("## Next")
    $lines.Add($report.next_action)
    $lines | Set-Content -LiteralPath $MarkdownPath -Encoding UTF8
    Copy-Item -LiteralPath $MarkdownPath -Destination $LatestMarkdownPath -Force

    Write-KeyValue "u13_live_automation_report" $ReportPath
    Write-KeyValue "u13_live_automation_markdown" $MarkdownPath
    Write-KeyValue "u13_live_automation_ok" $Ok
}

if ($SelfCheckOnly) {
    $missing = @($StopStale, $RepairAgent, $InstallAgentViaVnc, $DebugAgentState, $InvokeAgentJob, $LiveLifecycle, $AddU13Route, $EveNodeControl) |
        Where-Object { -not (Test-Path -LiteralPath $_) }
    foreach ($path in $missing) {
        Write-KeyValue "self_check_missing" $path
    }
    $ok = $missing.Count -eq 0
    Write-KeyValue "ensure_u13_live_automation_self_check_ok" $ok
    if (-not $ok) { exit 1 }
    exit 0
}

$failure = ""
$exitCode = 0
try {
    Invoke-Step "stop-stale-helpers" {
        $result = Invoke-Native -FilePath "powershell.exe" -Arguments @(
            "-NoProfile", "-ExecutionPolicy", "Bypass",
            "-File", $StopStale,
            "-HelperPort", [string]$HelperPort
        )
        if ($result.exit_code -ne 0) {
            throw "stop stale helpers failed with exit $($result.exit_code): $($result.output_tail)"
        }
        return $result
    } | Out-Null

    if ($AutoEve) {
        Invoke-Step "eve-status" { Invoke-EveAction -Action "status" } -ContinueOnError | Out-Null
        Invoke-Step "eve-start-u13-if-needed" { Invoke-EveAction -Action "start" } -ContinueOnError | Out-Null
        Start-Sleep -Seconds 5
    }

    Invoke-Step "host-route-preflight" {
        $remoteOctets = $RemoteIp -split "\."
        $remoteNetwork = if ($remoteOctets.Count -eq 4) {
            ($remoteOctets[0..2] -join ".") + ".0"
        } else {
            "192.168.113.0"
        }
        $routeResult = Invoke-Native -FilePath "powershell.exe" -Arguments @(
            "-NoProfile", "-ExecutionPolicy", "Bypass",
            "-File", $AddU13Route,
            "-Network", $remoteNetwork,
            "-Mask", "255.255.255.0",
            "-Gateway", $LabGateway,
            "-InterfaceAlias", $LabInterfaceAlias
        )
        if ($routeResult.exit_code -ne 0) {
            throw "route fix failed with exit $($routeResult.exit_code): $($routeResult.output_tail)"
        }
        [ordered]@{
            local_ping_ok = Test-PingQuiet -Address $LocalIp
            remote_ping_ok = Test-PingQuiet -Address $RemoteIp
            helper_port = $HelperPort
            remote_network = $remoteNetwork
            lab_gateway = $LabGateway
            lab_interface_alias = $LabInterfaceAlias
            route_fix = $routeResult
        }
    } | Out-Null

    $repairSucceeded = $false
    Invoke-Step "repair-agent-primary" {
        Invoke-AgentRepair -AttemptName "primary agent repair"
        $script:repairSucceeded = $true
    } -ContinueOnError | Out-Null

    if (-not $repairSucceeded -and $AutoEve -and $RestartU13OnAgentFailure) {
        Invoke-Step "eve-restart-u13-after-agent-failure" {
            Invoke-EveAction -Action "restart"
        } | Out-Null
        Write-KeyValue "u13_boot_wait_seconds" $BootWaitSeconds
        Start-Sleep -Seconds $BootWaitSeconds
        Invoke-Step "repair-agent-after-restart" {
            Invoke-AgentRepair -AttemptName "post-restart agent repair"
            $script:repairSucceeded = $true
        } -ContinueOnError | Out-Null
    }

    if (-not $repairSucceeded -and $AllowVncFallback) {
        Invoke-Step "install-agent-via-vnc-fallback" {
            $result = Invoke-Native -FilePath "powershell.exe" -Arguments @(
                "-NoProfile", "-ExecutionPolicy", "Bypass",
                "-File", $InstallAgentViaVnc,
                "-HelperListenHost", $HelperListenHost,
                "-HelperPort", [string]$HelperPort,
                "-WaitSeconds", "240"
            )
            if ($result.exit_code -ne 0) {
                throw "VNC fallback failed with exit $($result.exit_code): $($result.output_tail)"
            }
            return $result
        } | Out-Null

        Invoke-Step "repair-agent-after-vnc" {
            Invoke-AgentRepair -AttemptName "post-vnc agent repair"
            $script:repairSucceeded = $true
        } -ContinueOnError | Out-Null
    }

    if (-not $repairSucceeded) {
        throw "U13 peer-agent was not repaired; enable -AutoEve -RestartU13OnAgentFailure or -AllowVncFallback"
    }

    Invoke-Step "debug-agent-after-repair" { Invoke-AgentDebug } | Out-Null

    if ($RunLiveLifecycle) {
        Invoke-Step "run-live-service-lifecycle" {
            $result = Invoke-Native -FilePath "powershell.exe" -Arguments @(
                "-NoProfile", "-ExecutionPolicy", "Bypass",
                "-File", $LiveLifecycle,
                "-ProgressPercent", [string]$ProgressPercent
            )
            if ($result.exit_code -ne 0) {
                throw "live lifecycle failed with exit $($result.exit_code): $($result.output_tail)"
            }
            return $result
        } | Out-Null
    }
} catch {
    $failure = $_.Exception.Message
    $exitCode = 1
} finally {
    Write-Report -Ok ($exitCode -eq 0) -ErrorMessage $failure
}

exit $exitCode
