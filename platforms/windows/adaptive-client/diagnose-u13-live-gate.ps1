param(
    [string] $Local = "192.168.95.1",
    [string] $Remote = "192.168.113.199",
    [string[]] $GatewayCandidates = @("192.168.95.130", "192.168.95.129", "192.168.95.134"),
    [int] $HelperPort = 18091,
    [switch] $Json
)

$ErrorActionPreference = "Continue"

$ScriptDir = Split-Path -Parent $PSCommandPath
$ReportDir = Join-Path $ScriptDir "bin\regression"
$RunId = Get-Date -Format "yyyyMMdd-HHmmss"
$ReportPath = Join-Path $ReportDir "u13-live-gate-diagnostic-$RunId.json"
New-Item -ItemType Directory -Force -Path $ReportDir | Out-Null

function Test-PingQuiet
{
    param([Parameter(Mandatory)] [string] $Address)
    $output = & ping.exe "-n" "3" "-w" "1000" $Address 2>&1
    if ($LASTEXITCODE -eq 0)
    {
        return $true
    }
    return @($output | Where-Object { $_.ToString() -match 'TTL=' }).Count -gt 0
}

function Get-RoutePrint
{
    param([Parameter(Mandatory)] [string] $Address)
    try
    {
        $output = & route.exe PRINT $Address 2>&1
        return [pscustomobject]@{
            exit_code = $LASTEXITCODE
            output_tail = (($output | Select-Object -Last 35) -join "`n")
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

function Get-FindNetRoute
{
    param([Parameter(Mandatory)] [string] $Address)
    try
    {
        $route = Find-NetRoute -RemoteIPAddress $Address -ErrorAction Stop | Select-Object -First 1
        return [pscustomobject]@{
            ok = $null -ne $route
            next_hop = if ($route) { [string] $route.NextHop } else { $null }
            interface_alias = if ($route) { [string] $route.InterfaceAlias } else { $null }
            route_metric = if ($route) { [int] $route.RouteMetric } else { $null }
        }
    }
    catch
    {
        return [pscustomobject]@{
            ok = $false
            error = $_.Exception.Message
        }
    }
}

$helperListeners = @(Get-NetTCPConnection -LocalPort $HelperPort -State Listen -ErrorAction SilentlyContinue |
    ForEach-Object {
        [pscustomobject]@{
            local_address = $_.LocalAddress
            local_port = $_.LocalPort
            owning_process = $_.OwningProcess
        }
    })

$gatewayChecks = @($GatewayCandidates | ForEach-Object {
    [pscustomobject]@{
        address = $_
        ping_ok = Test-PingQuiet -Address $_
    }
})

$report = [ordered]@{
    created_at = (Get-Date).ToString("O")
    local = $Local
    remote = $Remote
    local_ping_ok = Test-PingQuiet -Address $Local
    remote_ping_ok = Test-PingQuiet -Address $Remote
    route = Get-FindNetRoute -Address $Remote
    route_print = Get-RoutePrint -Address $Remote
    gateway_candidates = $gatewayChecks
    helper_port = $HelperPort
    helper_port_free = $helperListeners.Count -eq 0
    helper_port_listeners = $helperListeners
    recommendation = ""
}

if (-not $report.remote_ping_ok)
{
    $reachableGateway = @($gatewayChecks | Where-Object { $_.ping_ok } | Select-Object -First 1)
    if ($reachableGateway.Count -gt 0)
    {
        $report.recommendation = "remote_not_reachable_but_gateway_candidate_alive: verify U13 is powered and route 192.168.113.0/24 points to the correct lab gateway."
    }
    else
    {
        $report.recommendation = "remote_and_gateway_candidates_unreachable: check VPN/split-tunnel, VMnet, and EVE lab power state."
    }
}
elseif (-not $report.helper_port_free)
{
    $report.recommendation = "helper_port_busy: stop stale helper or choose another helper port."
}
else
{
    $report.recommendation = "connectivity_preflight_ok: LiveFull may start once U13 peer agent is installed/running."
}

$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ReportPath -Encoding UTF8

if ($Json)
{
    $report | ConvertTo-Json -Depth 8
}
else
{
    Write-Host "u13_diagnostic_report=$ReportPath"
    Write-Host "local_ping_ok=$($report.local_ping_ok)"
    Write-Host "remote_ping_ok=$($report.remote_ping_ok)"
    Write-Host "route_ok=$($report.route.ok)"
    Write-Host "route_next_hop=$($report.route.next_hop)"
    Write-Host "route_interface=$($report.route.interface_alias)"
    Write-Host "helper_port_free=$($report.helper_port_free)"
    Write-Host "recommendation=$($report.recommendation)"
}
