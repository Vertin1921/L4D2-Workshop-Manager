<#
  build/env.ps1 —— 编译环境准备脚本（供其它脚本 dot-source 使用）

  用法：
      . .\build\env.ps1
      & $env:DOTNET_EXE build .\L4D2ModManager.sln -c Release

  查找 dotnet.exe 的顺序：
      1) 仓库内的便携 SDK      <仓库>\.tooling\dotnet\dotnet.exe
      2) 上一级目录的便携 SDK  <仓库>\..\.tooling\dotnet\dotnet.exe
      3) 系统安装目录          C:\Program Files\dotnet\dotnet.exe
      4) PATH 中的 dotnet

  若存在 .tooling 便携目录，则把 NuGet 缓存与 CLI 主目录也指向仓库内部，
  这样整个构建过程不依赖系统环境，也不会污染用户目录。
#>

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot

$candidates = @(
    (Join-Path $repoRoot '.tooling\dotnet\dotnet.exe'),
    (Join-Path (Split-Path -Parent $repoRoot) '.tooling\dotnet\dotnet.exe'),
    'C:\Program Files\dotnet\dotnet.exe'
)

$dotnetExe = $null
foreach ($candidate in $candidates) {
    if (Test-Path $candidate) { $dotnetExe = $candidate; break }
}
if (-not $dotnetExe) {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $dotnetExe = $cmd.Source }
}
if (-not $dotnetExe) {
    throw '未找到 dotnet.exe。请安装 .NET 8 SDK（https://dotnet.microsoft.com/download/dotnet/8.0），或把便携版 SDK 放到 .tooling\dotnet\。'
}

$env:DOTNET_EXE = $dotnetExe
$env:DOTNET_ROOT = Split-Path -Parent $dotnetExe
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

# 便携模式：仓库内存在 .tooling 时，把 CLI 主目录 / NuGet 缓存也放进去
$portableRoot = $null
foreach ($probe in @((Join-Path $repoRoot '.tooling'), (Join-Path (Split-Path -Parent $repoRoot) '.tooling'))) {
    if (Test-Path $probe) { $portableRoot = $probe; break }
}
if ($portableRoot) {
    $env:DOTNET_CLI_HOME = Join-Path $portableRoot 'clihome'
    $env:NUGET_PACKAGES = Join-Path $portableRoot 'nuget'
    $env:TEMP = Join-Path $portableRoot 'temp'
    $env:TMP = $env:TEMP
    # 让 NuGet 的用户级配置也落在仓库内（便携模式完全自包含）
    $env:APPDATA = Join-Path $portableRoot 'appdata'
    New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES, $env:TEMP, $env:APPDATA | Out-Null
}

# 确保 SDK 版本可用
$sdks = & $env:DOTNET_EXE --list-sdks 2>$null
if (-not ($sdks | Select-String -SimpleMatch '8.0')) {
    Write-Warning "未检测到 .NET 8 SDK，当前已安装：`n$sdks"
}
