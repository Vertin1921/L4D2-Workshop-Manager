<#
  tools/Unblock-Files.ps1
  解除"下载文件"的安全标记（Mark of the Web / Zone.Identifier）。

  为什么需要它？
    Windows 会给"从网络下载"的文件打上 Zone.Identifier 备用数据流。
    带这个标记的 exe 只要没有数字签名，SmartScreen 就会弹
    「Windows 已保护你的电脑 —— 发行者：发布者未知」。
    解除标记后即可正常打开（这是 Windows 的标准机制，不是程序有问题）。

  用法（不需要管理员权限）：
    pwsh -File .\tools\Unblock-Files.ps1                       # 处理本仓库 artifacts 目录
    pwsh -File .\tools\Unblock-Files.ps1 -Path "D:\L4D2MM"      # 处理指定目录（或单个文件）
    pwsh -File .\tools\Unblock-Files.ps1 -Show                  # 只查看哪些文件带标记
#>
[CmdletBinding()]
param(
    [string]$Path = '',
    [switch]$Show
)

$ErrorActionPreference = 'Stop'

function Get-TargetPath {
    if (-not [string]::IsNullOrWhiteSpace($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    # 默认：仓库的 artifacts 目录（脚本位于 tools\ 下）
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $artifacts = Join-Path $repoRoot 'artifacts'
    if (Test-Path $artifacts) { return $artifacts }

    return $repoRoot
}

function Test-HasMarkOfTheWeb {
    param([string]$FilePath)
    try {
        return $null -ne (Get-Item -LiteralPath $FilePath -Stream Zone.Identifier -ErrorAction Stop)
    }
    catch {
        return $false
    }
}

$target = Get-TargetPath
if (-not (Test-Path -LiteralPath $target)) {
    throw "路径不存在：$target"
}

$files = if ((Get-Item -LiteralPath $target).PSIsContainer) {
    Get-ChildItem -LiteralPath $target -Recurse -File -ErrorAction SilentlyContinue
}
else {
    @(Get-Item -LiteralPath $target)
}

Write-Host "检查目录：$target" -ForegroundColor Cyan

$blocked = @()
foreach ($file in $files) {
    if (Test-HasMarkOfTheWeb -FilePath $file.FullName) { $blocked += $file }
}

if ($blocked.Count -eq 0) {
    Write-Host "没有发现带「下载标记」的文件，无需处理。" -ForegroundColor Green
    return
}

Write-Host "发现 $($blocked.Count) 个带下载标记的文件：" -ForegroundColor Yellow
foreach ($file in $blocked) {
    Write-Host ("  " + $file.FullName.Replace($target, '.'))
}

if ($Show) { return }

$done = 0
$failed = 0
foreach ($file in $blocked) {
    try {
        Unblock-File -LiteralPath $file.FullName
        $done++
    }
    catch {
        $failed++
    }
}

Write-Host ""
Write-Host "已解除 $done 个文件的下载标记" -ForegroundColor Green
if ($failed -gt 0) { Write-Host "有 $failed 个文件处理失败（可能被占用）" -ForegroundColor Yellow }

Write-Host ""
Write-Host "提示：" -ForegroundColor Cyan
Write-Host "  · 也可以在资源管理器里右键文件 → 属性 → 勾选「解除锁定」→ 确定"
Write-Host "  · 对压缩包先解除锁定，再解压，里面的文件就不会带上标记"
Write-Host "  · 想彻底不再出现该提示：用代码签名证书签名（见 tools\Sign-Artifacts.ps1）"
