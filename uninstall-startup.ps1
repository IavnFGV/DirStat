$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
Remove-ItemProperty -Path $key -Name 'DiskSpaceMonitor' -ErrorAction SilentlyContinue
Write-Host 'Автозапуск отключён'
