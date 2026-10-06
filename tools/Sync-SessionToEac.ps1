[CmdletBinding()]
param(
    # 源数据目录（默认当前 Harness 的 DSH_HOME）
    [string]$Source = $(if ($env:DSH_HOME) { $env:DSH_HOME } else { Join-Path $env:USERPROFILE '.dsh' }),

    # EAC 的 dsh-home（dpx 沙箱环境）；留空则自动侦测"最近使用过的那个环境"
    [string]$Target = '',

    # 复制前先备份目标（体积可能上 GB，默认关闭；用 -Backup 打开）
    [switch]$Backup
)

$ErrorActionPreference = 'Stop'

# 自动侦测 EAC 的 dsh-home：在 dpx\dsh-environments 下找"最近修改"的那个环境
if ([string]::IsNullOrWhiteSpace($Target)) {
    $envRoot = Join-Path $env:LOCALAPPDATA 'Deepseek Harness EAC\dpx\dsh-environments'
    if (-not (Test-Path $envRoot)) { throw "找不到 EAC 环境目录：$envRoot（请先启动一次 EAC）" }

    $candidate = Get-ChildItem $envRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-Path (Join-Path $_.FullName 'dsh-home') } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if (-not $candidate) { throw "在 $envRoot 下没有找到任何 dsh-home（请先启动一次 EAC 让它生成）" }
    $Target = Join-Path $candidate.FullName 'dsh-home'
    Write-Host "自动侦测到 EAC 环境：$($candidate.Name)"
}

Write-Host "源   ：$Source"
Write-Host "目标 ：$Target"
Write-Host ""

if (-not (Test-Path $Source)) { throw "源数据目录不存在：$Source" }
if (-not (Test-Path $Target)) { throw "EAC 数据目录不存在：$Target（先启动一次 EAC 让它生成）" }

$free = (Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='$($Target.Substring(0,2))'").FreeSpace
Write-Host ("目标所在盘可用空间：{0} GB" -f [math]::Round($free / 1GB, 2))
if ($free -lt 1GB) { throw "目标盘可用空间不足 1 GB，请先清理（EAC 会因为 ENOSPC 写不进去）" }

if ($Backup) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $bk = Join-Path (Split-Path $Target -Parent) "dsh-home.backup-$stamp"
    Write-Host "备份目标到：$bk"
    Copy-Item $Target $bk -Recurse -Force
}

# 1) 会话（按工作区目录逐个文件覆盖为最新快照）
$copied = 0
$srcSessions = Get-ChildItem (Join-Path $Source 'sessions') -Recurse -File -ErrorAction SilentlyContinue
foreach ($f in $srcSessions) {
    $rel = $f.FullName.Substring((Join-Path $Source 'sessions').Length).TrimStart('\')
    $dst = Join-Path (Join-Path $Target 'sessions') $rel
    New-Item -ItemType Directory -Force -Path (Split-Path $dst -Parent) | Out-Null
    Copy-Item $f.FullName $dst -Force
    $copied++
}
Write-Host ("会话文件已同步：{0} 个（覆盖为最新快照）" -f $copied)

# 2) 附件
if (Test-Path (Join-Path $Source 'attachments')) {
    New-Item -ItemType Directory -Force -Path (Join-Path $Target 'attachments') | Out-Null
    Copy-Item (Join-Path $Source 'attachments\*') (Join-Path $Target 'attachments') -Recurse -Force -ErrorAction SilentlyContinue
    $n = (Get-ChildItem (Join-Path $Target 'attachments') -Recurse -File -ErrorAction SilentlyContinue | Measure-Object).Count
    Write-Host ("附件已同步，目标现有：{0} 个" -f $n)
}

# 3) 附件索引（缺失时复制）
$idxSrc = Join-Path $Source 'llm-deepseek\files-v3.json'
$idxDst = Join-Path $Target 'llm-deepseek\files-v3.json'
if ((Test-Path $idxSrc) -and -not (Test-Path $idxDst)) {
    New-Item -ItemType Directory -Force -Path (Split-Path $idxDst -Parent) | Out-Null
    Copy-Item $idxSrc $idxDst -Force
    Write-Host "附件索引已复制：llm-deepseek\files-v3.json"
}

# 4) 工作区注册：把源里的会话挂到目标中同路径的工作区（不新建重复工作区）
$wSrcPath = Join-Path $Source 'storages\workspace.json'
$wDstPath = Join-Path $Target 'storages\workspace.json'
if ((Test-Path $wSrcPath) -and (Test-Path $wDstPath)) {
    Copy-Item $wDstPath ($wDstPath + '.bak-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) -Force

    $src = [System.IO.File]::ReadAllText($wSrcPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    $dst = [System.IO.File]::ReadAllText($wDstPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    $srcWs = @($src.tables.workspaces.PSObject.Properties | ForEach-Object { $_.Value })
    $added = 0

    foreach ($prop in $dst.tables.workspaces.PSObject.Properties) {
        $w = $prop.Value
        $match = $srcWs | Where-Object { $_.path -eq $w.path } | Select-Object -First 1
        if (-not $match) { continue }

        $ids = New-Object System.Collections.ArrayList
        foreach ($id in $w.sessionIds) { [void]$ids.Add($id) }
        foreach ($id in $match.sessionIds) { if (-not $ids.Contains($id)) { [void]$ids.Add($id); $added++ } }
        $w.sessionIds = $ids.ToArray()
        $w.updatedAt = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
    }

    $utf8Bom = New-Object System.Text.UTF8Encoding($true)
    [System.IO.File]::WriteAllText($wDstPath, ($dst | ConvertTo-Json -Depth 12), $utf8Bom)
    Write-Host ("工作区注册已合并，新增会话引用：{0} 个" -f $added)
}

Write-Host ""
Write-Host "完成。请完全退出并重新打开 EAC，即可在对应工作区看到同步过来的会话。" -ForegroundColor Green
