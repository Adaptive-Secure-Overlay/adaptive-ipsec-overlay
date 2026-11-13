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
$RunScript = Join-Path $ScriptDir "run-live-service-lifecycle.ps1"
$LogDir = Join-Path $ScriptDir "bin\live-service-lifecycle"
$RunId = Get-Date -Format "yyyyMMdd-HHmmss"
$LogPath = Join-Path $LogDir "live-service-lifecycle-elevated-$RunId.log"

New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

function Test-IsAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Add-SwitchArg {
    param(
        [Parameter(Mandatory)] [System.Collections.Generic.List[string]] $Args,
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [bool] $Enabled
    )
    if ($Enabled) {
        $Args.Add($Name)
    }
}

if (-not (Test-Path -LiteralPath $RunScript)) {
    throw "run-live-service-lifecycle.ps1 not found: $RunScript"
}

$argsList = [System.Collections.Generic.List[string]]::new()
$argsList.Add("-NoProfile")
$argsList.Add("-ExecutionPolicy")
$argsList.Add("Bypass")
$argsList.Add("-File")
$argsList.Add($RunScript)
$argsList.Add("-LocalIp")
$argsList.Add($LocalIp)
$argsList.Add("-RemoteIp")
$argsList.Add($RemoteIp)
$argsList.Add("-HelperListenHost")
$argsList.Add($HelperListenHost)
$argsList.Add("-HelperPort")
$argsList.Add([string] $HelperPort)
$argsList.Add("-HostPort")
$argsList.Add([string] $HostPort)
$argsList.Add("-U13Port")
$argsList.Add([string] $U13Port)
$argsList.Add("-U13DeployWaitSeconds")
$argsList.Add([string] $U13DeployWaitSeconds)
$argsList.Add("-U13StatusWaitSeconds")
$argsList.Add([string] $U13StatusWaitSeconds)
$argsList.Add("-LabGateway")
$argsList.Add($LabGateway)
$argsList.Add("-LabInterfaceAlias")
$argsList.Add($LabInterfaceAlias)
$argsList.Add("-ProgressPercent")
$argsList.Add([string] $ProgressPercent)
Add-SwitchArg -Args $argsList -Name "-PrepareOnly" -Enabled ([bool] $PrepareOnly)
Add-SwitchArg -Args $argsList -Name "-SkipBuild" -Enabled ([bool] $SkipBuild)
Add-SwitchArg -Args $argsList -Name "-SkipPackage" -Enabled ([bool] $SkipPackage)
Add-SwitchArg -Args $argsList -Name "-SkipU13Deploy" -Enabled ([bool] $SkipU13Deploy)
Add-SwitchArg -Args $argsList -Name "-SkipHostService" -Enabled ([bool] $SkipHostService)
Add-SwitchArg -Args $argsList -Name "-NoConnect" -Enabled ([bool] $NoConnect)
Add-SwitchArg -Args $argsList -Name "-KeepSa" -Enabled ([bool] $KeepSa)
Add-SwitchArg -Args $argsList -Name "-KeepHostService" -Enabled ([bool] $KeepHostService)
Add-SwitchArg -Args $argsList -Name "-AllowSleep" -Enabled ([bool] $AllowSleep)

if (Test-IsAdmin) {
    & powershell.exe @argsList *>&1 | Tee-Object -FilePath $LogPath
    exit $LASTEXITCODE
}

$launcher = Join-Path $LogDir "live-service-lifecycle-elevated-child-$RunId.ps1"
$quotedArgs = ($argsList | ForEach-Object { "'" + ($_.Replace("'", "''")) + "'" }) -join ", "
@"
`$argsList = @($quotedArgs)
& powershell.exe @argsList *>&1 | Tee-Object -FilePath '$($LogPath.Replace("'", "''"))'
exit `$LASTEXITCODE
"@ | Set-Content -LiteralPath $launcher -Encoding UTF8

Write-Host "live_service_lifecycle_elevated_log=$LogPath"
Write-Host "live_service_lifecycle_uac=starting"
$process = Start-Process -FilePath "powershell.exe" `
    -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $launcher) `
    -Verb RunAs `
    -Wait `
    -PassThru

Write-Host "live_service_lifecycle_uac_exit_code=$($process.ExitCode)"
if (Test-Path -LiteralPath $LogPath) {
    Get-Content -LiteralPath $LogPath | Select-Object -Last 120
}
exit $process.ExitCode
