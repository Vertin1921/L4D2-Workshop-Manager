<#
  tools/Add-DefenderExclusion.ps1
  为 L4D2 Mod Manager 添加 / 移除 Windows Defender 排除项。

  为什么需要它？
    本程序是自编译、未签名的 .NET 应用，并且会重命名 Steam 目录里的 .vpk 文件，
    Defender 的启发式规则有时会误报。把程序目录加入排除项即可正常使用。
    （更彻底的做法是代码签名 + 向微软提交误报，见 README 的"杀软误报"一节。）

  用法（需要管理员权限）：
    pwsh -File .\tools\Add-DefenderExclusion.ps1                 # 添加默认排除项
    pwsh -File .\tools\Add-DefenderExclusion.ps1 -Path "D:\L4D2MM"  # 指定安装目录
    pwsh -File .\tools\Add-DefenderExclusion.ps1 -Remove         # 移除排除项
    pwsh -File .\tools\Add-DefenderExclusion.ps1 -Show           # 查看当前排除项
#>
[CmdletBinding()]
param(
    [string]$Path = '',
    [switch]$Remove,
    [switch]$Show
)

$ErrorActionPreference = 'Stop'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw '需要管理员权限：请右键以管理员身份运行 PowerShell 后重试。'
    }
}

function Get-TargetPaths {
    $paths = @()

    if (-not [string]::IsNullOrWhiteSpace($Path)) {
        $paths += [System.IO.Path]::GetFullPath($Path)
    }
    else {
        # 1) 安装程序的常见位置
        foreach ($candidate in @(
                (Join-Path $env:ProgramFiles 'L4D2 Mod Manager'),
                (Join-Path $env:LOCALAPPDATA 'Programs\L4D2 Mod Manager'),
                (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\Setup'),
                (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\L4D2ModManager-win-x64')
            )) {
            if (Test-Path $candidate) { $paths += $candidate }
        }

        # 2) 程序数据目录
        $data = Join-Path $env:APPDATA 'L4D2ModManager'
        if (Test-Path $data) { $paths += $data }
    }

    return $paths | Select-Object -Unique
}

if ($Show) {
    Write-Host '== 当前 Defender 排除目录 ==' -ForegroundColor Cyan
    (Get-MpPreference).ExclusionPath | ForEach-Object { Write-Host "  $_" }
    Write-Host '== 当前 Defender 排除进程 ==' -ForegroundColor Cyan
    (Get-MpPreference).ExclusionProcess | ForEach-Object { Write-Host "  $_" }
    return
}

Assert-Administrator

$targets = Get-TargetPaths
if ($targets.Count -eq 0) {
    throw '没有找到需要排除的目录。请用 -Path 指定安装目录，例如：-Path "D:\L4D2MM"'
}

if ($Remove) {
    foreach ($target in $targets) {
        Remove-MpPreference -ExclusionPath $target -ErrorAction SilentlyContinue
        Write-Host "已移除排除目录: $target" -ForegroundColor Yellow
    }
    Remove-MpPreference -ExclusionProcess 'L4D2ModManager.exe' -ErrorAction SilentlyContinue
    Remove-MpPreference -ExclusionProcess 'L4D2ModManager.Setup.exe' -ErrorAction SilentlyContinue
    Write-Host '已移除排除进程: L4D2ModManager.exe / L4D2ModManager.Setup.exe' -ForegroundColor Yellow
    return
}

foreach ($target in $targets) {
    Add-MpPreference -ExclusionPath $target
    Write-Host "已添加排除目录: $target" -ForegroundColor Green
}

Add-MpPreference -ExclusionProcess 'L4D2ModManager.exe'
Add-MpPreference -ExclusionProcess 'L4D2ModManager.Setup.exe'
Write-Host '已添加排除进程: L4D2ModManager.exe / L4D2ModManager.Setup.exe' -ForegroundColor Green

Write-Host ''
Write-Host '完成。如果文件已被隔离，请在「Windows 安全中心 → 病毒和威胁防护 → 保护历史记录」中恢复。' -ForegroundColor Cyan
Write-Host '建议同时向微软提交误报（附本仓库源码地址）：https://www.microsoft.com/en-us/wdsi/filesubmission' -ForegroundColor Cyan
