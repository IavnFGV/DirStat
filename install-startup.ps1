param([string]$ExePath = (Join-Path $PSScriptRoot 'publish/win-x64/DiskSpaceMonitor.exe'))
$ErrorActionPreference = 'Stop'
$resolved = (Resolve-Path -LiteralPath $ExePath).Path
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
New-Item -Path $key -Force | Out-Null
New-ItemProperty -Path $key -Name 'DiskSpaceMonitor' -Value ('"' + $resolved + '"') -PropertyType String -Force | Out-Null
Write-Host "Автозапуск включён: $resolved"
