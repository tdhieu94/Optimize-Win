#Requires -Version 5.1
<#
  Cài Optimize Win (bản thử nghiệm, giai đoạn 0).
  - Chép chương trình vào C:\Program Files\OptimizeWin (chỉ admin ghi được).
  - Đăng ký dịch vụ "OptimizeWinService" chạy nền với quyền admin.
  - Tạo lối tắt trong Start Menu.
  Chạy qua file "Cai-dat.cmd" để Windows tự hỏi quyền admin.
#>
param([switch]$Quiet)
$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    exit
}

$source = Split-Path -Parent $PSCommandPath
$target = Join-Path $env:ProgramFiles 'OptimizeWin'
$serviceName = 'OptimizeWinService'
$serviceExe = Join-Path $target 'Service\OptimizeWin.Service.exe'

if (-not (Test-Path (Join-Path $source 'OptimizeWin.exe')) -or -not (Test-Path (Join-Path $source 'Service\OptimizeWin.Service.exe'))) {
    throw "Không tìm thấy file chương trình cạnh script. Hãy giải nén cả file zip rồi chạy lại."
}

Write-Host 'Đang dừng bản cũ (nếu có)...'
Get-Process -Name 'OptimizeWin' -ErrorAction SilentlyContinue | Stop-Process -Force
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -ne 'Stopped') { Stop-Service -Name $serviceName -Force }
    sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 2
}

Write-Host "Đang chép chương trình vào $target ..."
New-Item -ItemType Directory -Force -Path $target | Out-Null
robocopy $source $target /MIR /NFL /NDL /NJH /NJS /NP /XF 'install.ps1' 'uninstall.ps1' 'update.ps1' 'Cai-dat.cmd' 'Go-cai-dat.cmd' | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Chép file thất bại (robocopy mã $LASTEXITCODE)." }
Copy-Item (Join-Path $source 'uninstall.ps1') $target -Force
Copy-Item (Join-Path $source 'Go-cai-dat.cmd') $target -Force

Write-Host 'Đang đăng ký dịch vụ OW...'
New-Service -Name $serviceName `
    -BinaryPathName "`"$serviceExe`"" `
    -DisplayName 'Optimize Win Service' `
    -Description 'Dịch vụ nền của Optimize Win: thực hiện dọn dẹp an toàn, ghi nhật ký và cho phép hoàn tác.' `
    -StartupType Automatic | Out-Null
sc.exe failure $serviceName reset= 86400 actions= restart/60000/restart/60000// | Out-Null
Start-Service -Name $serviceName

Write-Host 'Đang tạo lối tắt trong Start Menu...'
$shortcutPath = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'Optimize Win.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = Join-Path $target 'OptimizeWin.exe'
$shortcut.WorkingDirectory = $target
$shortcut.Save()

Write-Host 'Đang đặt lịch kiểm tra ổ đĩa (khi đăng nhập và mỗi thứ Hai)...'
# Runs "OptimizeWin.exe --check" as whoever is signed in: it only reads disk info and shows a
# Windows notification when a drive is nearly full or a disk looks unhealthy. It never cleans anything.
$taskName = 'OptimizeWin Health Check'
try {
    $users = (New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-545').Translate([Security.Principal.NTAccount]).Value
    $action = New-ScheduledTaskAction -Execute (Join-Path $target 'OptimizeWin.exe') -Argument '--check' -WorkingDirectory $target
    $atLogon = New-ScheduledTaskTrigger -AtLogOn
    $atLogon.Delay = 'PT3M'
    $weekly = New-ScheduledTaskTrigger -Weekly -DaysOfWeek Monday -At '10:00'
    $taskPrincipal = New-ScheduledTaskPrincipal -GroupId $users -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -ExecutionTimeLimit (New-TimeSpan -Minutes 10) -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger @($atLogon, $weekly) -Principal $taskPrincipal -Settings $settings `
        -Description 'Optimize Win: kiểm tra dung lượng và sức khỏe ổ đĩa, chỉ báo, không tự dọn.' -Force | Out-Null
} catch {
    # The app works without it; only the automatic warnings are missing.
    Write-Host "Không đặt được lịch kiểm tra: $_" -ForegroundColor Yellow
}

# Lets the app (and the updater) see which version is installed.
$version = (Get-Item $serviceExe).VersionInfo.ProductVersion
Set-Content -Path (Join-Path $target 'installed.txt') -Value $version -Encoding ASCII

Write-Host ''
Write-Host 'Cài đặt xong. Mở "Optimize Win" từ Start Menu.' -ForegroundColor Green
if (-not $Quiet) { Read-Host 'Nhấn Enter để đóng' }
