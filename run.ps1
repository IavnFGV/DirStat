$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'src/DiskSpaceMonitor/bin/Release/net8.0-windows/DiskSpaceMonitor.exe'
if (-not (Test-Path -LiteralPath $exe)) { & (Join-Path $PSScriptRoot 'build.ps1') }
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Start-Process -FilePath $exe
