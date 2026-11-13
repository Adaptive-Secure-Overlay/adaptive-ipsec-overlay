param(
    [int] $ProgressPercent = 58
)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $PSCommandPath
$RunId = Get-Date -Format "yyyyMMdd-HHmmss"
$LogDir = Join-Path $ScriptDir "bin\regression"
$LogPath = Join-Path $LogDir "local-install-elevated-$RunId.log"

New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

$devLoop = Join-Path $ScriptDir "run-windows-exe-dev-loop.ps1"
$regression = Join-Path $ScriptDir "run-windows-exe-regression.ps1"
if (-not (Test-Path -LiteralPath $devLoop))
{
    throw "run-windows-exe-dev-loop.ps1 not found: $devLoop"
}
if (-not (Test-Path -LiteralPath $regression))
{
    throw "run-windows-exe-regression.ps1 not found: $regression"
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
$isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin)
{
    $launcher = Join-Path $LogDir "run-local-install-elevated-child-$RunId.ps1"
    @"
Set-Location -LiteralPath '$($ScriptDir.Replace("'", "''"))'
& '$($regression.Replace("'", "''"))' -Quick -LocalInstallSmoke -Checkpoint *>&1 | Tee-Object -FilePath '$($LogPath.Replace("'", "''"))'
exit `$LASTEXITCODE
"@ | Set-Content -LiteralPath $launcher -Encoding UTF8

    Write-Host "local_install_elevated_log=$LogPath"
    Write-Host "local_install_uac=starting"
    $process = Start-Process -FilePath "powershell.exe" `
        -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $launcher) `
        -Verb RunAs `
        -Wait `
        -PassThru

    Write-Host "local_install_uac_exit_code=$($process.ExitCode)"
    if (Test-Path -LiteralPath $LogPath)
    {
        Get-Content -LiteralPath $LogPath | Select-Object -Last 80
    }
    exit $process.ExitCode
}

Set-Location -LiteralPath $ScriptDir
& $regression -Quick -LocalInstallSmoke -Checkpoint *>&1 | Tee-Object -FilePath $LogPath
exit $LASTEXITCODE
