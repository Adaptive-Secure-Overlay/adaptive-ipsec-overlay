param(
    [string] $OutputZip = ""
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $PSCommandPath
$root = Resolve-Path (Join-Path $scriptDir "..")
$serviceBin = Join-Path $scriptDir "AdaptiveClient.Service\bin\Debug\net9.0"
$cliBin = Join-Path $scriptDir "AdaptiveClient.Cli\bin\Debug\net9.0"
$manualSaExe = Join-Path $root "experiments\manual-sa\bin\manual-sa.exe"
$publishRoot = Join-Path $scriptDir "bin\live-service-package"
$stage = Join-Path $publishRoot "stage"

if ([string]::IsNullOrWhiteSpace($OutputZip)) {
    $OutputZip = Join-Path $publishRoot "adaptive-client-service-live.zip"
}

foreach ($path in @($serviceBin, $cliBin, $manualSaExe)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "required path missing: $path"
    }
}

$dotnetRoot = Join-Path $env:ProgramFiles "dotnet"
$dotnetExe = Join-Path $dotnetRoot "dotnet.exe"
$fxrRoot = Join-Path $dotnetRoot "host\fxr"
$netCoreRoot = Join-Path $dotnetRoot "shared\Microsoft.NETCore.App"
if (-not (Test-Path -LiteralPath $dotnetExe)) {
    throw "dotnet.exe not found: $dotnetExe"
}

$fxrVersion = Get-ChildItem -LiteralPath $fxrRoot -Directory |
    Where-Object { $_.Name -like "9.*" } |
    Sort-Object Name -Descending |
    Select-Object -First 1
$netCoreVersion = Get-ChildItem -LiteralPath $netCoreRoot -Directory |
    Where-Object { $_.Name -like "9.*" } |
    Sort-Object Name -Descending |
    Select-Object -First 1
if (-not $fxrVersion -or -not $netCoreVersion) {
    throw "portable .NET 9 runtime pieces were not found under $dotnetRoot"
}

if (Test-Path -LiteralPath $stage) {
    $stageFull = [System.IO.Path]::GetFullPath($stage)
    $publishRootFull = [System.IO.Path]::GetFullPath($publishRoot)
    if (-not $stageFull.StartsWith($publishRootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "refusing to clean stage outside publish root: $stageFull"
    }
    Remove-Item -LiteralPath $stage -Recurse -Force
}

New-Item -ItemType Directory -Force -Path (Join-Path $stage "app") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $stage "dotnet\host\fxr") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $stage "dotnet\shared\Microsoft.NETCore.App") | Out-Null

Copy-Item -Path (Join-Path $serviceBin "*") -Destination (Join-Path $stage "app") -Recurse -Force
Copy-Item -Path (Join-Path $cliBin "AdaptiveClient.Cli.*") -Destination (Join-Path $stage "app") -Force
Copy-Item -LiteralPath $manualSaExe -Destination (Join-Path $stage "app\manual-sa.exe") -Force

Copy-Item -LiteralPath $dotnetExe -Destination (Join-Path $stage "dotnet\dotnet.exe") -Force
Copy-Item -LiteralPath (Join-Path $dotnetRoot "LICENSE.txt") -Destination (Join-Path $stage "dotnet") -Force -ErrorAction SilentlyContinue
Copy-Item -LiteralPath (Join-Path $dotnetRoot "ThirdPartyNotices.txt") -Destination (Join-Path $stage "dotnet") -Force -ErrorAction SilentlyContinue
Copy-Item -LiteralPath $fxrVersion.FullName -Destination (Join-Path $stage "dotnet\host\fxr") -Recurse -Force
Copy-Item -LiteralPath $netCoreVersion.FullName -Destination (Join-Path $stage "dotnet\shared\Microsoft.NETCore.App") -Recurse -Force

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputZip) | Out-Null
Remove-Item -Force -LiteralPath $OutputZip -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $OutputZip -Force

$hash = Get-FileHash -Algorithm SHA256 -LiteralPath $OutputZip
Write-Host "live_service_package=$OutputZip"
Write-Host "live_service_package_size=$((Get-Item -LiteralPath $OutputZip).Length)"
Write-Host "live_service_package_sha256=$($hash.Hash)"
Write-Host "live_service_package_fxr=$($fxrVersion.Name)"
Write-Host "live_service_package_netcore=$($netCoreVersion.Name)"
