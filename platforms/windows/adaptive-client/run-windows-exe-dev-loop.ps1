param(
    [ValidateSet("Fast", "Service", "Checkpoint", "LiveGate", "LiveFull", "LocalInstall")]
    [string] $Mode = "Service",

    [switch] $KeepSa,
    [switch] $AllowSleep,
    [int] $ProgressPercent = 58
)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $PSCommandPath
$RegressionScript = Join-Path $ScriptDir "run-windows-exe-regression.ps1"
$ReportDir = Join-Path $ScriptDir "bin\regression"
$SummaryJson = Join-Path $ReportDir "windows-exe-dev-loop-latest.json"
$SummaryMarkdown = Join-Path $ReportDir "windows-exe-dev-loop-latest.md"

New-Item -ItemType Directory -Force -Path $ReportDir | Out-Null

function Enable-NoSleep
{
    if ("AdaptiveClient.DevLoop.NativeMethods" -as [type])
    {
        return
    }

    Add-Type -Namespace AdaptiveClient.DevLoop -Name NativeMethods -MemberDefinition @"
[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
public static extern uint SetThreadExecutionState(uint esFlags);
"@
}

function Set-NoSleep
{
    param([Parameter(Mandatory)] [bool] $Enabled)

    Enable-NoSleep
    $ES_CONTINUOUS = [uint32] 2147483648
    $ES_SYSTEM_REQUIRED = [uint32] 0x00000001
    $ES_DISPLAY_REQUIRED = [uint32] 0x00000002

    if ($Enabled)
    {
        [AdaptiveClient.DevLoop.NativeMethods]::SetThreadExecutionState($ES_CONTINUOUS -bor $ES_SYSTEM_REQUIRED -bor $ES_DISPLAY_REQUIRED) | Out-Null
        return
    }

    [AdaptiveClient.DevLoop.NativeMethods]::SetThreadExecutionState($ES_CONTINUOUS) | Out-Null
}

function Get-RegressionArgs
{
    param([Parameter(Mandatory)] [string] $SelectedMode)

    switch ($SelectedMode)
    {
        "Fast" { return @("-Quick") }
        "Service" { return @("-Quick", "-ServiceSmoke", "-DissertationReport") }
        "Checkpoint" { return @("-Quick", "-ServiceSmoke", "-DissertationReport", "-Checkpoint") }
        "LiveGate" { return @("-LivePreflight") }
        "LiveFull" { return @("-LiveFull") }
        "LocalInstall" { return @("-Quick", "-LocalInstallSmoke", "-Checkpoint") }
        default { throw "Unsupported dev-loop mode: $SelectedMode" }
    }
}

function Find-LatestRegressionReport
{
    param([object[]] $Output)

    foreach ($line in $Output)
    {
        $text = [string] $line
        if ($text.StartsWith("regression_report=", [StringComparison]::OrdinalIgnoreCase))
        {
            $candidate = $text.Substring("regression_report=".Length).Trim()
            if (Test-Path -LiteralPath $candidate)
            {
                return [System.IO.Path]::GetFullPath($candidate)
            }
        }
    }

    $latest = Join-Path $ReportDir "windows-exe-regression-latest.json"
    if (Test-Path -LiteralPath $latest)
    {
        return [System.IO.Path]::GetFullPath($latest)
    }

    return $null
}

function Get-StepResult
{
    param(
        [Parameter(Mandatory)] [object] $Report,
        [Parameter(Mandatory)] [string] $StepName
    )

    $step = @($Report.steps | Where-Object { $_.name -eq $StepName } | Select-Object -First 1)
    if ($step.Count -eq 0)
    {
        return $null
    }

    return $step[0].result
}

function Write-DevLoopSummary
{
    param(
        [Parameter(Mandatory)] [object] $Summary,
        [Parameter(Mandatory)] [string] $JsonPath,
        [Parameter(Mandatory)] [string] $MarkdownPath
    )

    $Summary | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $JsonPath -Encoding UTF8

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("# Windows EXE dev loop")
    $lines.Add("")
    $lines.Add("- mode: $($Summary.mode)")
    $lines.Add("- ok: $($Summary.ok)")
    $lines.Add("- exit_code: $($Summary.exit_code)")
    $lines.Add("- duration_ms: $($Summary.duration_ms)")
    $lines.Add("- estimated_product_progress_percent: $($Summary.estimated_product_progress_percent)")
    $lines.Add("- report_json: $($Summary.report_json)")
    $lines.Add("- report_markdown: $($Summary.report_markdown)")
    if ($Summary.failed_step)
    {
        $lines.Add("- failed_step: $($Summary.failed_step)")
        $lines.Add("- failed_error: $($Summary.failed_error)")
    }

    if ($Summary.service_to_service_identity_proof_connect)
    {
        $lines.Add("- service_identity_proof_connect: $($Summary.service_to_service_identity_proof_connect)")
        $lines.Add("- service_identity_proof_rekey: $($Summary.service_to_service_identity_proof_rekey)")
    }

    if ($Summary.data_plane_esp_confirmed -ne $null)
    {
        $lines.Add("- data_plane_esp_confirmed: $($Summary.data_plane_esp_confirmed)")
        $lines.Add("- control_plane_bypass_confirmed: $($Summary.control_plane_bypass_confirmed)")
    }

    if ($Summary.checkpoint_path)
    {
        $lines.Add("")
        $lines.Add("## Checkpoint")
        $lines.Add("- path: $($Summary.checkpoint_path)")
        $lines.Add("- length: $($Summary.checkpoint_length)")
        $lines.Add("- sha256: $($Summary.checkpoint_sha256)")
    }

    $lines.Add("")
    $lines.Add("## Next")
    $lines.Add($Summary.next_action)

    $lines | Set-Content -LiteralPath $MarkdownPath -Encoding UTF8
}

$regressionArgs = @(Get-RegressionArgs -SelectedMode $Mode)
if ($KeepSa -and $regressionArgs -notcontains "-KeepSa")
{
    $regressionArgs += "-KeepSa"
}

$started = Get-Date
$output = @()
$exitCode = 1
$sleepHeld = $false

try
{
    if (-not $AllowSleep)
    {
        Set-NoSleep -Enabled $true
        $sleepHeld = $true
        Write-Host "dev_loop_no_sleep=enabled"
    }

    Write-Host "dev_loop_mode=$Mode"
    Write-Host "dev_loop_command=$RegressionScript $($regressionArgs -join ' ')"

    $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $RegressionScript @regressionArgs 2>&1
    $exitCode = $LASTEXITCODE
    $output | ForEach-Object { Write-Host $_ }
}
finally
{
    if ($sleepHeld)
    {
        Set-NoSleep -Enabled $false
        Write-Host "dev_loop_no_sleep=disabled"
    }
}

$ended = Get-Date
$durationMs = [int64] (($ended - $started).TotalMilliseconds)
$reportJson = Find-LatestRegressionReport -Output $output
$report = $null
$reportMarkdown = $null
$failedStep = $null
$failedError = $null
$checkpoint = $null
$serviceToService = $null
$evidence = $null

if ($reportJson)
{
    $report = Get-Content -LiteralPath $reportJson -Raw | ConvertFrom-Json
    $reportMarkdownCandidate = [System.IO.Path]::ChangeExtension($reportJson, ".md")
    if (Test-Path -LiteralPath $reportMarkdownCandidate)
    {
        $reportMarkdown = [System.IO.Path]::GetFullPath($reportMarkdownCandidate)
    }

    $failed = @($report.steps | Where-Object { $_.ok -ne $true } | Select-Object -First 1)
    if ($failed.Count -gt 0)
    {
        $failedStep = [string] $failed[0].name
        $failedError = [string] $failed[0].error
    }

    $checkpoint = $report.checkpoint_result
    $serviceToService = Get-StepResult -Report $report -StepName "service-to-service-smoke"
    $evidence = Get-StepResult -Report $report -StepName "dissertation-evidence-report"
}

$ok = $exitCode -eq 0 -and ($null -eq $report -or $report.ok -eq $true)
$nextAction = if (-not $ok -and $failedStep) {
    "Fix failed step '$failedStep' first, then rerun: .\windows-agent\adaptive-client\run-windows-exe-dev-loop.ps1 -Mode $Mode"
} elseif ($Mode -eq "Fast") {
    "Fast loop is green. Next useful gate: .\windows-agent\adaptive-client\run-windows-exe-dev-loop.ps1 -Mode Service"
} elseif ($Mode -eq "Service") {
    "Service loop is green. Before a larger refactor or live test, run: .\windows-agent\adaptive-client\run-windows-exe-dev-loop.ps1 -Mode Checkpoint"
} elseif ($Mode -eq "Checkpoint") {
    "Checkpoint is green. Next live gate, when needed: .\windows-agent\adaptive-client\run-windows-exe-dev-loop.ps1 -Mode LiveGate"
} elseif ($Mode -eq "LiveGate") {
    "Live preflight is done. Next manual/admin step: run LocalInstall or LiveFull only when lab access is intentionally enabled."
} else {
    "Mode '$Mode' completed. Continue development from the generated report."
}

$effectiveProgressPercent = if ($ok) {
    $ProgressPercent
} else {
    [Math]::Min($ProgressPercent, 58)
}

$summary = [ordered]@{
    mode = $Mode
    ok = [bool] $ok
    exit_code = [int] $exitCode
    started_at = $started.ToString("O")
    ended_at = $ended.ToString("O")
    duration_ms = $durationMs
    requested_product_progress_percent = $ProgressPercent
    estimated_product_progress_percent = $effectiveProgressPercent
    report_json = $reportJson
    report_markdown = $reportMarkdown
    failed_step = $failedStep
    failed_error = $failedError
    service_to_service_identity_proof_connect = if ($serviceToService) { [string] $serviceToService.delivery_identity_proof_status } else { $null }
    service_to_service_identity_proof_rekey = if ($serviceToService) { [string] $serviceToService.rekey_identity_proof_status } else { $null }
    data_plane_esp_confirmed = if ($evidence) { [bool] $evidence.data_plane_esp_confirmed } else { $null }
    control_plane_bypass_confirmed = if ($evidence) { [bool] $evidence.control_plane_bypass_confirmed } else { $null }
    checkpoint_path = if ($checkpoint) { [string] $checkpoint.path } else { $null }
    checkpoint_length = if ($checkpoint) { [int64] $checkpoint.length } else { $null }
    checkpoint_sha256 = if ($checkpoint) { [string] $checkpoint.sha256 } else { $null }
    next_action = $nextAction
}

Write-DevLoopSummary -Summary $summary -JsonPath $SummaryJson -MarkdownPath $SummaryMarkdown

Write-Host "dev_loop_summary_json=$SummaryJson"
Write-Host "dev_loop_summary_markdown=$SummaryMarkdown"
Write-Host "dev_loop_ok=$ok"

if (-not $ok)
{
    exit 1
}
