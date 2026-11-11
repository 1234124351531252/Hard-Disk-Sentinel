$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$output = [System.IO.Path]::GetFullPath((Join-Path $projectRoot '发布版\磁盘哨兵'))
$prefix = $projectRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $output.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "发布目录超出项目目录，拒绝清理：$output"
}

if (Test-Path -LiteralPath $output) {
    $existing = (Resolve-Path -LiteralPath $output).Path
    if (-not $existing.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "发布目录解析到项目外，拒绝清理：$existing"
    }
    Remove-Item -LiteralPath $existing -Recurse -Force
}

$localSdk = Join-Path $projectRoot '.dotnet\dotnet.exe'
if (Test-Path -LiteralPath $localSdk) {
    $dotnet = $localSdk
} else {
    $command = Get-Command dotnet -ErrorAction Stop
    $dotnet = $command.Source
}

Push-Location $projectRoot
try {
    & $dotnet publish 'DiskSentinel.csproj' -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=false -p:IncludeNativeLibrariesForSelfExtract=false `
        -o $output
    if ($LASTEXITCODE -ne 0) { throw "发布失败，退出代码：$LASTEXITCODE" }
} finally {
    Pop-Location
}

$exe = Join-Path $output '磁盘哨兵.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "没有生成启动程序：$exe" }
Get-ChildItem -LiteralPath $output -Filter '*.pdb' -File | Remove-Item -Force
Write-Host "发布完成：$exe" -ForegroundColor Green
