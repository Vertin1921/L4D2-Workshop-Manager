<#
  build/test.ps1 —— 运行自检程序（Core 层完整功能验证）

  用法：
    pwsh -File .\build\test.ps1
    pwsh -File .\build\test.ps1 -RealVpkDirectory "D:\Program Files (x86)\Steam\steamapps\common\Left 4 Dead 2\left4dead2\addons"
    pwsh -File .\build\test.ps1 -AutoDetectSteamDirectory
#>
[CmdletBinding()]
param(
    [string]$RealVpkDirectory = '',
    [switch]$AutoDetectSteamDirectory
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'env.ps1')

$repoRoot = Split-Path -Parent $PSScriptRoot
$testProject = Join-Path $repoRoot 'tests\L4D2ModManager.Tests\L4D2ModManager.Tests.csproj'
$nugetConfig = Join-Path $repoRoot 'NuGet.config'
$solution = Join-Path $repoRoot 'L4D2ModManager.sln'

if ($AutoDetectSteamDirectory) {
    $candidates = @(
        'C:\Program Files (x86)\Steam\steamapps\common\Left 4 Dead 2\left4dead2\addons',
        'D:\Program Files (x86)\Steam\steamapps\common\Left 4 Dead 2\left4dead2\addons',
        'C:\Program Files\Steam\steamapps\common\Left 4 Dead 2\left4dead2\addons',
        'D:\SteamLibrary\steamapps\common\Left 4 Dead 2\left4dead2\addons'
    )
    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) { $RealVpkDirectory = $candidate; break }
    }
}

& $env:DOTNET_EXE restore $testProject --configfile $nugetConfig --nologo | Out-Null
& $env:DOTNET_EXE build $solution -c Release --no-restore --nologo -m:1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw '编译失败' }

if (-not [string]::IsNullOrWhiteSpace($RealVpkDirectory)) {
    if (-not (Test-Path $RealVpkDirectory)) { throw "真实 Mod 目录不存在：$RealVpkDirectory" }
    $env:L4D2MM_TEST_VPK_DIR = $RealVpkDirectory
    Write-Host "使用真实 Mod 目录进行验证：$RealVpkDirectory" -ForegroundColor Cyan
}

$testExe = Join-Path $repoRoot 'tests\L4D2ModManager.Tests\bin\Release\net8.0-windows\L4D2ModManager.Tests.exe'
& $testExe
exit $LASTEXITCODE
