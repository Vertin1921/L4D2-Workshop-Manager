[CmdletBinding()]
param(
    # 二选一：指定新版本号，或用开关自动递增
    [Alias('Set')]
    [string]$NewVersion,
    [switch]$Major,
    [switch]$Minor,
    [switch]$Patch,

    # 只改版本号、不重新构建
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$propsPath = Join-Path $repoRoot 'Directory.Build.props'

if (-not (Test-Path $propsPath)) { throw "找不到 Directory.Build.props：$propsPath" }

$text = [System.IO.File]::ReadAllText($propsPath, [System.Text.Encoding]::UTF8)
$match = [regex]::Match($text, '<Version>(\d+)\.(\d+)\.(\d+)</Version>')
if (-not $match.Success) { throw "无法在 Directory.Build.props 里找到 <Version>x.y.z</Version>" }

$major = [int]$match.Groups[1].Value
$minor = [int]$match.Groups[2].Value
$patch = [int]$match.Groups[3].Value

# 也支持用环境变量传入（GitHub Actions 里更稳妥，避免参数解析问题）
if ([string]::IsNullOrWhiteSpace($NewVersion) -and -not [string]::IsNullOrWhiteSpace($env:L4D2MM_NEW_VERSION)) {
    $NewVersion = $env:L4D2MM_NEW_VERSION
}

if (-not [string]::IsNullOrWhiteSpace($NewVersion)) {
    if ($NewVersion -notmatch '^\d+\.\d+\.\d+$') { throw "版本号格式应为 x.y.z，例如 -NewVersion 1.2.0" }
    $newVersion = $NewVersion
}
elseif ($Major) { $newVersion = "$($major + 1).0.0" }
elseif ($Minor) { $newVersion = "$major.$($minor + 1).0" }
else { $newVersion = "$major.$minor.$($patch + 1)" }   # 默认：修订号 +1

$newText = $text
$newText = [regex]::Replace($newText, '<Version>\d+\.\d+\.\d+</Version>', "<Version>$newVersion</Version>")
$newText = [regex]::Replace($newText, '<AssemblyVersion>\d+\.\d+\.\d+\.\d+</AssemblyVersion>', "<AssemblyVersion>$newVersion.0</AssemblyVersion>")
$newText = [regex]::Replace($newText, '<FileVersion>\d+\.\d+\.\d+\.\d+</FileVersion>', "<FileVersion>$newVersion.0</FileVersion>")

$utf8Bom = New-Object System.Text.UTF8Encoding($true)
[System.IO.File]::WriteAllText($propsPath, $newText, $utf8Bom)

Write-Host "版本号：$($match.Groups[1].Value).$($match.Groups[2].Value).$($match.Groups[3].Value)  ->  $newVersion" -ForegroundColor Green
Write-Host "已写入：$propsPath"

if ($NoBuild) { return }

Write-Host ""
Write-Host "开始重新构建安装包（build-release.ps1）…" -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'build-release.ps1') -SingleFileInstaller -Sign -SkipTests
Write-Host ""
Write-Host "完成。安装包版本：$newVersion" -ForegroundColor Green
