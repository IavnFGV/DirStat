$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'publish/win-x64/DiskSpaceMonitor.exe'
if (-not (Test-Path -LiteralPath $exe)) { & (Join-Path $PSScriptRoot 'build.ps1') -Publish }
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Start-Process -FilePath $exe
