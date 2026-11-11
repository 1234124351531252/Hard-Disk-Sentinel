$ErrorActionPreference = 'Stop'
$releaseRoot = Join-Path $PSScriptRoot '╥╒╡╪╟Ф'
$app = Get-ChildItem -LiteralPath $releaseRoot -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like '╢еелиз╠Ь*' } |
    ForEach-Object { Join-Path $_.FullName '╢еелиз╠Ь.exe' } |
    Where-Object { Test-Path -LiteralPath $_ } |
    Sort-Object { (Get-Item -LiteralPath $_).LastWriteTime } -Descending |
    Select-Object -First 1
if (-not $app) {
    Write-Host 'Published app not found. Build the app before launching.' -ForegroundColor Yellow
    Read-Host 'Press Enter to exit'
    exit 1
}
Start-Process -FilePath $app
