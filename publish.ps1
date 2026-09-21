#Requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$project = Join-Path $root 'src/DiskSpaceMonitor/DiskSpaceMonitor.csproj'
$portable = Join-Path $root 'publish/win-x64'
$compact = Join-Path $root 'release/compact'
$release = Join-Path $root 'release'

& (Join-Path $root 'build.ps1') -Publish
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet publish $project -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:PublishReadyToRun=false -p:PublishTrimmed=false `
    -p:DebugType=none -o $compact
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Copy-Item -LiteralPath (Join-Path $root 'install-dotnet.ps1') -Destination $compact -Force
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $compact -Force
Copy-Item -LiteralPath (Join-Path $root 'config.example.json') -Destination $compact -Force
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $portable -Force
Copy-Item -LiteralPath (Join-Path $root 'config.example.json') -Destination $portable -Force

New-Item -ItemType Directory -Path $release -Force | Out-Null
Compress-Archive -Path (Join-Path $portable '*') -DestinationPath (Join-Path $release 'DiskSpaceMonitor-win-x64.zip') -Force
Compress-Archive -Path (Join-Path $compact '*') -DestinationPath (Join-Path $release 'DiskSpaceMonitor-win-x64-compact.zip') -Force
Write-Host 'Release archives are ready in release\.' -ForegroundColor Green
