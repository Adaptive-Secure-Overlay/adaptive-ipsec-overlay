param(
    [string] $Network = "192.168.113.0",
    [string] $Mask = "255.255.255.0",
    [string] $Gateway = "192.168.95.130",
    [string] $InterfaceAlias = "VMware Network Adapter VMnet8"
)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $PSCommandPath
$LogDir = Join-Path $ScriptDir "bin\regression"
$RunId = Get-Date -Format "yyyyMMdd-HHmmss"
$LogPath = Join-Path $LogDir "add-u13-route-$RunId.log"

New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
$isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin)
{
    $child = Join-Path $LogDir "add-u13-route-child-$RunId.ps1"
    @"
`$ErrorActionPreference = 'Continue'
`$interface = Get-NetIPInterface -AddressFamily IPv4 -InterfaceAlias '$($InterfaceAlias.Replace("'", "''"))' -ErrorAction Stop | Select-Object -First 1
`$ifIndex = `$interface.InterfaceIndex
'route_interface_alias=$($InterfaceAlias.Replace("'", "''"))' | Tee-Object -FilePath '$($LogPath.Replace("'", "''"))'
('route_interface_index=' + `$ifIndex) | Tee-Object -FilePath '$($LogPath.Replace("'", "''"))' -Append
route delete $Network *>&1 | Tee-Object -FilePath '$($LogPath.Replace("'", "''"))' -Append
route add $Network mask $Mask $Gateway IF `$ifIndex -p *>&1 | Tee-Object -FilePath '$($LogPath.Replace("'", "''"))' -Append
`$routeAddExitCode = `$LASTEXITCODE
route print $Network *>&1 | Tee-Object -FilePath '$($LogPath.Replace("'", "''"))' -Append
exit `$routeAddExitCode
"@ | Set-Content -LiteralPath $child -Encoding UTF8

    Write-Host "route_log=$LogPath"
    $process = Start-Process -FilePath "powershell.exe" `
        -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $child) `
        -Verb RunAs `
        -Wait `
        -PassThru
    Write-Host "route_uac_exit_code=$($process.ExitCode)"
    if (Test-Path -LiteralPath $LogPath)
    {
        Get-Content -LiteralPath $LogPath
    }
    exit $process.ExitCode
}

$interface = Get-NetIPInterface -AddressFamily IPv4 -InterfaceAlias $InterfaceAlias -ErrorAction Stop | Select-Object -First 1
$ifIndex = $interface.InterfaceIndex
"route_interface_alias=$InterfaceAlias" | Tee-Object -FilePath $LogPath
"route_interface_index=$ifIndex" | Tee-Object -FilePath $LogPath -Append
route delete $Network *>&1 | Tee-Object -FilePath $LogPath -Append
route add $Network mask $Mask $Gateway IF $ifIndex -p *>&1 | Tee-Object -FilePath $LogPath -Append
$routeAddExitCode = $LASTEXITCODE
route print $Network *>&1 | Tee-Object -FilePath $LogPath -Append
exit $routeAddExitCode
