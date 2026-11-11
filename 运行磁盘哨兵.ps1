$ErrorActionPreference = 'Stop'
$app = Join-Path $PSScriptRoot '发布版\磁盘哨兵\磁盘哨兵.exe'
if (-not (Test-Path -LiteralPath $app)) {
    Write-Host 'Published app not found. Run 发布程序.bat first.' -ForegroundColor Yellow
    Read-Host 'Press Enter to exit'
    exit 1
}
Start-Process -FilePath $app
