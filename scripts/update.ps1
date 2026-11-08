#Requires -Version 5.1
<#
  Cập nhật Optimize Win. Chỉ được dịch vụ OW (quyền SYSTEM) chạy, sau khi đã kiểm tra mã SHA-256 của gói.
  1. Sao lưu bản đang cài.
  2. Chạy install.ps1 của bản mới.
  3. Nếu lỗi: chép lại bản sao lưu và khởi động lại dịch vụ cũ.
#>
param(
    [Parameter(Mandatory = $true)][string]$Backup,
    [Parameter(Mandatory = $true)][string]$Log,
    [Parameter(Mandatory = $true)][string]$Version
)
$ErrorActionPreference = 'Stop'
$target = Join-Path $env:ProgramFiles 'OptimizeWin'
$serviceName = 'OptimizeWinService'

function Write-Log([string]$text) {
    Add-Content -Path $Log -Value ("{0:yyyy-MM-dd HH:mm:ss}  {1}" -f (Get-Date), $text) -Encoding UTF8
}

Write-Log "Bắt đầu cập nhật lên $Version"
try {
    if (Test-Path $target) {
        New-Item -ItemType Directory -Force -Path $Backup | Out-Null
        robocopy $target $Backup /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "Sao lưu thất bại (robocopy mã $LASTEXITCODE)." }
        Write-Log 'Đã sao lưu bản cũ'
    }

    & (Join-Path $PSScriptRoot 'install.ps1') -Quiet *>&1 | ForEach-Object { Write-Log "$_" }

    $svc = Get-Service -Name $serviceName -ErrorAction Stop
    if ($svc.Status -ne 'Running') { throw 'Dịch vụ không chạy sau khi cài.' }
    Write-Log "Cập nhật xong: $Version"
}
catch {
    Write-Log "LỖI: $_ . Đang khôi phục bản cũ..."
    try {
        Get-Process -Name 'OptimizeWin' -ErrorAction SilentlyContinue | Stop-Process -Force
        $svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($svc -and $svc.Status -ne 'Stopped') { Stop-Service -Name $serviceName -Force }
        if (Test-Path $Backup) {
            robocopy $Backup $target /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
        }

        $serviceExe = Join-Path $target 'Service\OptimizeWin.Service.exe'
        if (-not (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) {
            New-Service -Name $serviceName -BinaryPathName "`"$serviceExe`"" -DisplayName 'Optimize Win Service' -StartupType Automatic | Out-Null
            sc.exe failure $serviceName reset= 86400 actions= restart/60000/restart/60000// | Out-Null
        }

        Start-Service -Name $serviceName
        Write-Log 'Đã khôi phục bản cũ.'
    }
    catch {
        Write-Log "LỖI khi khôi phục: $_"
    }
    exit 1
}
