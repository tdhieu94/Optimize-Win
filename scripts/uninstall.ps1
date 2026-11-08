#Requires -Version 5.1
<#
  Gỡ Optimize Win. Nhật ký và vùng cách ly trong C:\ProgramData\OptimizeWin được GIỮ LẠI
  để không mất file nào bạn có thể muốn khôi phục.
#>
$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    exit
}

$serviceName = 'OptimizeWinService'
$target = Join-Path $env:ProgramFiles 'OptimizeWin'

Get-Process -Name 'OptimizeWin' -ErrorAction SilentlyContinue | Stop-Process -Force
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -ne 'Stopped') { Stop-Service -Name $serviceName -Force }
    sc.exe delete $serviceName | Out-Null
}

Unregister-ScheduledTask -TaskName 'OptimizeWin Health Check' -Confirm:$false -ErrorAction SilentlyContinue

Remove-Item (Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'Optimize Win.lnk') -ErrorAction SilentlyContinue

# This script may itself live in $target, so remove the folder after it exits.
Start-Process cmd.exe -WindowStyle Hidden -ArgumentList '/c', "timeout /t 2 >nul & rmdir /s /q `"$target`""

Write-Host 'Đã gỡ Optimize Win. Dữ liệu trong C:\ProgramData\OptimizeWin được giữ lại.' -ForegroundColor Green
Read-Host 'Nhấn Enter để đóng'
