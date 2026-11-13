param(
    [switch] $Json,
    [int] $DefaultProgressPercent = 58
)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $PSCommandPath
$WorkspaceRoot = (Resolve-Path (Join-Path $ScriptDir "..\..")).Path
$ReportDir = Join-Path $ScriptDir "bin\regression"
$CheckpointsDir = Join-Path $WorkspaceRoot "checkpoints"
$DevLoopSummaryPath = Join-Path $ReportDir "windows-exe-dev-loop-latest.json"
$RegressionLatestPath = Join-Path $ReportDir "windows-exe-regression-latest.json"
$LiveLifecycleLatestPath = Join-Path $ScriptDir "bin\live-service-lifecycle\live-service-lifecycle-latest.json"

function Read-JsonFile
{
    param([Parameter(Mandatory)] [string] $Path)

    if (-not (Test-Path -LiteralPath $Path))
    {
        return $null
    }

    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function Get-LatestCheckpoint
{
    if (-not (Test-Path -LiteralPath $CheckpointsDir))
    {
        return $null
    }

    return Get-ChildItem -LiteralPath $CheckpointsDir -Filter "windows-exe-regression-*.zip" |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
}

$devLoop = Read-JsonFile -Path $DevLoopSummaryPath
$regression = Read-JsonFile -Path $RegressionLatestPath
$liveLifecycle = Read-JsonFile -Path $LiveLifecycleLatestPath
$checkpoint = Get-LatestCheckpoint
$failedStep = $null
$failedError = $null
$rawProgress = if ($devLoop -and $devLoop.estimated_product_progress_percent) { [int] $devLoop.estimated_product_progress_percent } else { $DefaultProgressPercent }
$liveProgress = if ($liveLifecycle -and $liveLifecycle.ok -eq $true -and $liveLifecycle.estimated_product_progress_percent) { [int] $liveLifecycle.estimated_product_progress_percent } else { $null }
$effectiveProgress = if ($devLoop -and $devLoop.ok -eq $false) { [Math]::Min($rawProgress, $DefaultProgressPercent) } else { $rawProgress }
if ($liveProgress -ne $null)
{
    $effectiveProgress = [Math]::Max($effectiveProgress, $liveProgress)
}

if ($regression)
{
    $failed = @($regression.steps | Where-Object { $_.ok -ne $true } | Select-Object -First 1)
    if ($failed.Count -gt 0)
    {
        $failedStep = [string] $failed[0].name
        $failedError = [string] $failed[0].error
    }
}

$status = [ordered]@{
    estimated_product_progress_percent = $effectiveProgress
    last_requested_product_progress_percent = if ($devLoop -and $devLoop.requested_product_progress_percent) { [int] $devLoop.requested_product_progress_percent } else { $null }
    last_dev_loop_mode = if ($devLoop) { [string] $devLoop.mode } else { $null }
    last_dev_loop_ok = if ($devLoop) { [bool] $devLoop.ok } else { $null }
    last_dev_loop_duration_ms = if ($devLoop) { [int64] $devLoop.duration_ms } else { $null }
    last_regression_ok = if ($regression) { [bool] $regression.ok } else { $null }
    last_regression_report = if ($regression) { [string] (Join-Path $ReportDir "windows-exe-regression-latest.json") } else { $null }
    last_live_service_ok = if ($liveLifecycle) { [bool] $liveLifecycle.ok } else { $null }
    last_live_service_report = if ($liveLifecycle) { [string] $LiveLifecycleLatestPath } else { $null }
    failed_step = $failedStep
    failed_error = $failedError
    service_identity_proof_connect = if ($devLoop) { [string] $devLoop.service_to_service_identity_proof_connect } else { $null }
    service_identity_proof_rekey = if ($devLoop) { [string] $devLoop.service_to_service_identity_proof_rekey } else { $null }
    data_plane_esp_confirmed = if ($devLoop) { $devLoop.data_plane_esp_confirmed } else { $null }
    control_plane_bypass_confirmed = if ($devLoop) { $devLoop.control_plane_bypass_confirmed } else { $null }
    latest_checkpoint = if ($checkpoint) { [string] $checkpoint.FullName } else { $null }
    latest_checkpoint_size = if ($checkpoint) { [int64] $checkpoint.Length } else { $null }
    next_action = if ($devLoop) { [string] $devLoop.next_action } else { "Run .\windows-agent\adaptive-client\run-windows-exe-dev-loop.ps1 -Mode Service" }
}

if ($Json)
{
    $status | ConvertTo-Json -Depth 10
    return
}

Write-Host "Windows EXE dev status"
Write-Host "progress_percent=$($status.estimated_product_progress_percent)"
Write-Host "last_dev_loop_mode=$($status.last_dev_loop_mode)"
Write-Host "last_dev_loop_ok=$($status.last_dev_loop_ok)"
Write-Host "last_regression_ok=$($status.last_regression_ok)"
Write-Host "last_live_service_ok=$($status.last_live_service_ok)"
if ($status.failed_step)
{
    Write-Host "failed_step=$($status.failed_step)"
    Write-Host "failed_error=$($status.failed_error)"
}

if ($status.service_identity_proof_connect)
{
    Write-Host "identity_proof_connect=$($status.service_identity_proof_connect)"
    Write-Host "identity_proof_rekey=$($status.service_identity_proof_rekey)"
}

if ($status.data_plane_esp_confirmed -ne $null)
{
    Write-Host "data_plane_esp_confirmed=$($status.data_plane_esp_confirmed)"
    Write-Host "control_plane_bypass_confirmed=$($status.control_plane_bypass_confirmed)"
}

if ($status.latest_checkpoint)
{
    Write-Host "latest_checkpoint=$($status.latest_checkpoint)"
    Write-Host "latest_checkpoint_size=$($status.latest_checkpoint_size)"
}

Write-Host "next_action=$($status.next_action)"
