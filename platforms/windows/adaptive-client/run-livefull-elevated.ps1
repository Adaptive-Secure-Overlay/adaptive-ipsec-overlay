param(
    [int] $ProgressPercent = 60,
    [switch] $KeepSa
)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $PSCommandPath
$RunId = Get-Date -Format "yyyyMMdd-HHmmss"
$LogDir = Join-Path $ScriptDir "bin\regression"
$LogPath = Join-Path $LogDir "livefull-elevated-$RunId.log"

New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

$devLoop = Join-Path $ScriptDir "run-windows-exe-dev-loop.ps1"
if (-not (Test-Path -LiteralPath $devLoop))
{
    throw "run-windows-exe-dev-loop.ps1 not found: $devLoop"
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
$isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

$keepSaArg = if ($KeepSa) { " -KeepSa" } else { "" }

if (-not $isAdmin)
{
    $launcher = Join-Path $LogDir "run-livefull-elevated-child-$RunId.ps1"
    @"
Set-Location -LiteralPath '$($ScriptDir.Replace("'", "''"))'
& '$($devLoop.Replace("'", "''"))' -Mode LiveFull -ProgressPercent $ProgressPercent$keepSaArg *>&1 | Tee-Object -FilePath '$($LogPath.Replace("'", "''"))'
exit `$LASTEXITCODE
"@ | Set-Content -LiteralPath $launcher -Encoding UTF8

    Write-Host "livefull_elevated_log=$LogPath"
    Write-Host "livefull_uac=starting"
    $process = Start-Process -FilePath "powershell.exe" `
        -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $launcher) `
        -Verb RunAs `
        -Wait `
        -PassThru

    Write-Host "livefull_uac_exit_code=$($process.ExitCode)"
    if (Test-Path -LiteralPath $LogPath)
    {
        Get-Content -LiteralPath $LogPath | Select-Object -Last 100
    }
    exit $process.ExitCode
}

Set-Location -LiteralPath $ScriptDir
$args = @("-Mode", "LiveFull", "-ProgressPercent", [string] $ProgressPercent)
if ($KeepSa)
{
    $args += "-KeepSa"
}
& $devLoop @args *>&1 | Tee-Object -FilePath $LogPath
exit $LASTEXITCODE
