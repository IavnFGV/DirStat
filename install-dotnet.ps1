#Requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$runtimePackage = 'Microsoft.DotNet.DesktopRuntime.8'
$downloadPage = 'https://dotnet.microsoft.com/download/dotnet/8.0'

function Test-DesktopRuntime {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) { return $false }
    $runtimes = @(dotnet --list-runtimes 2>$null)
    return [bool]($runtimes | Where-Object { $_ -match '^Microsoft\.WindowsDesktop\.App 8\.' })
}

if (Test-DesktopRuntime) {
    Write-Host '.NET 8 Desktop Runtime is already installed.' -ForegroundColor Green
    exit 0
}

$winget = Get-Command winget -ErrorAction SilentlyContinue
if (-not $winget) {
    Write-Host 'WinGet is unavailable. Opening the official Microsoft download page.' -ForegroundColor Yellow
    Start-Process $downloadPage
    exit 1
}

winget install --id $runtimePackage --exact --accept-package-agreements --accept-source-agreements
if ($LASTEXITCODE -ne 0) {
    Write-Host 'Installation did not complete. Opening the official Microsoft download page.' -ForegroundColor Yellow
    Start-Process $downloadPage
    exit $LASTEXITCODE
}

if (-not (Test-DesktopRuntime)) {
    throw '.NET 8 Desktop Runtime was not found after installation.'
}
Write-Host 'Done. You can now run DiskSpaceMonitor.exe.' -ForegroundColor Green
