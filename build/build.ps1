<#
  build/build.ps1 —— 日常开发编译（Debug / Release，快速）

  用法：
      pwsh -File .\build\build.ps1                 # Release 编译整个解决方案
      pwsh -File .\build\build.ps1 -Configuration Debug
      pwsh -File .\build\build.ps1 -RunTests       # 编译后运行自检程序
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$RunTests
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'env.ps1')

$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot 'L4D2ModManager.sln'

Write-Host "== 使用 SDK: $env:DOTNET_EXE" -ForegroundColor Cyan
& $env:DOTNET_EXE build $solution -c $Configuration --nologo -m:1
if ($LASTEXITCODE -ne 0) { throw "编译失败（$Configuration）" }

if ($RunTests) {
    $testProj = Join-Path $repoRoot 'tests\L4D2ModManager.Tests\L4D2ModManager.Tests.csproj'
    & $env:DOTNET_EXE run --project $testProj -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { throw '自检失败' }
}

Write-Host "== 编译完成: $Configuration" -ForegroundColor Green
