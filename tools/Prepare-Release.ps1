<#
  tools/Prepare-Release.ps1
  把 artifacts 目录整理成"可以直接发给别人"的分发包。

  它会做四件事：
    1. 清除所有产物的下载标记（Mark of the Web），避免对方解压后又触发 SmartScreen；
    2. 可选：用代码签名证书签名（-Sign / -PfxPath，强烈建议对外分发时使用）；
    3. 生成 dist\L4D2ModManager-<版本>-win-x64\ 目录，内含安装包、便携包、
       「先读我.txt」（写明如何解除锁定 / 加白名单 / 反馈误报）以及 Unblock-Files.ps1；
    4. 压缩为 dist\L4D2ModManager-<版本>-win-x64.zip，并输出每个文件的 SHA256
       （提交误报申诉时需要附上哈希）。

  用法：
    pwsh -File .\tools\Prepare-Release.ps1
    pwsh -File .\tools\Prepare-Release.ps1 -Sign                    # 用本机开发证书签名
    pwsh -File .\tools\Prepare-Release.ps1 -PfxPath .\certs\a.pfx -PfxPassword '***'
    pwsh -File .\tools\Prepare-Release.ps1 -Version 1.0.1 -PortableOnly
#>
[CmdletBinding()]
param(
    [string]$Version = '1.0.0',

    # 使用 PFX 正式证书签名
    [string]$PfxPath = '',
    [string]$PfxPassword = '',

    # 自动使用本机 Subject 含 "L4D2 Mod Manager" 的证书签名
    [switch]$Sign,

    # 指定证书指纹
    [string]$Thumbprint = '',

    # 只打包便携版（不打安装包）
    [switch]$PortableOnly
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $repoRoot 'artifacts'
$distRoot = Join-Path $repoRoot 'dist'
$packageName = "L4D2ModManager-$Version-win-x64"
$packageDir = Join-Path $distRoot $packageName

if (-not (Test-Path $artifacts)) {
    throw "找不到 artifacts 目录，请先运行 build\build-release.ps1"
}

# ---------------------------------------------------------------- 1. 清理旧分发包
if (Test-Path $packageDir) { Remove-Item $packageDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $packageDir | Out-Null

Write-Host "== 整理分发包 ==" -ForegroundColor Cyan

# ---------------------------------------------------------------- 2. 解除下载标记
Write-Host "清除产物上的下载标记…"
Get-ChildItem -LiteralPath $artifacts -Recurse -File -ErrorAction SilentlyContinue |
    ForEach-Object { Unblock-File -LiteralPath $_.FullName -ErrorAction SilentlyContinue }

# ---------------------------------------------------------------- 3. 可选签名
$signScript = Join-Path $PSScriptRoot 'Sign-Artifacts.ps1'
if ($Sign -or -not [string]::IsNullOrWhiteSpace($PfxPath) -or -not [string]::IsNullOrWhiteSpace($Thumbprint)) {
    Write-Host "签名产物…" -ForegroundColor Cyan
    try {
        $signArgs = @{}
        if (-not [string]::IsNullOrWhiteSpace($PfxPath)) {
            $signArgs['PfxPath'] = $PfxPath
            $signArgs['PfxPassword'] = $PfxPassword
        }
        elseif (-not [string]::IsNullOrWhiteSpace($Thumbprint)) { $signArgs['Thumbprint'] = $Thumbprint }

        & $signScript @signArgs
    }
    catch {
        Write-Warning "签名失败：$($_.Exception.Message)"
        Write-Warning "继续生成未签名的分发包（对方首次运行会看到 SmartScreen 提示）。"
    }
}
else {
    Write-Warning "未签名：对方首次运行会看到 SmartScreen「发行者：发布者未知」。"
    Write-Warning "彻底解决：pwsh -File .\tools\New-DevCertificate.ps1 然后 -Sign；对外分发建议用正式代码签名证书。"
}

# ---------------------------------------------------------------- 4. 拷贝安装包 / 便携包
$setupSource = Join-Path $artifacts 'Setup'
$setupZip = Join-Path $artifacts 'Setup.zip'
$portableZip = Join-Path $artifacts 'L4D2ModManager-win-x64-portable.zip'

if (-not $PortableOnly) {
    if (Test-Path $setupSource) {
        Copy-Item $setupSource (Join-Path $packageDir 'Setup') -Recurse -Force
        Write-Host "  已包含安装程序目录 Setup\（Setup.exe + payload.zip）"
    }
    elseif (Test-Path $setupZip) {
        Copy-Item $setupZip (Join-Path $packageDir 'Setup.zip') -Force
        Write-Host "  已包含 Setup.zip"
    }
}

if (Test-Path $portableZip) {
    Copy-Item $portableZip (Join-Path $packageDir 'L4D2ModManager-portable.zip') -Force
    Write-Host "  已包含便携版压缩包（免安装）"
}

# 附带解除锁定脚本，方便对方一键处理
Copy-Item (Join-Path $PSScriptRoot 'Unblock-Files.ps1') (Join-Path $packageDir 'Unblock-Files.ps1') -Force

# ---------------------------------------------------------------- 5. 写入「先读我.txt」
$readme = @"
L4D2 Mod Manager v$Version  (Windows 10/11 x64)
================================================

【一、怎么装】
  推荐：安装版
    1) 进入 Setup 文件夹
    2) 右键 Setup.exe -> 以管理员身份运行（会弹一次 UAC，属正常）
    3) 默认安装到 Program Files，自动创建开始菜单/桌面快捷方式
  或者：便携版
    解压 L4D2ModManager-portable.zip，直接运行里面的 L4D2ModManager.exe（不写注册表）

【二、如果 Windows 提示「已保护你的电脑 / 发布者：未知发布者」】
  这是 SmartScreen 对"网络下载 + 未签名程序"的标准提示，不是病毒。
  任选一种处理：
    A. 在提示窗口点「更多信息」-> 「仍要运行」（最快）
    B. 先解除文件锁定再解压/运行：
         · 右键压缩包 -> 属性 -> 勾选「解除锁定」-> 确定，然后再解压
         · 或者在本文件夹运行：
             右键 Unblock-Files.ps1 -> 使用 PowerShell 运行
             或：pwsh -File .\Unblock-Files.ps1 -Path .
    C. 让发布者用代码签名证书签名（能同时消除该提示与杀软疑虑）

【三、如果杀毒软件拦截】
  1) 先确认文件哈希与本说明末尾列出的一致（可用 Get-FileHash 校验）
  2) 把安装目录加入杀软白名单/排除项
  3) 到微软误报提交页反馈（附源码地址与文件哈希）：
       https://www.microsoft.com/en-us/wdsi/filesubmission
  4) 本程序是开源的，可自行用 .NET 8 SDK 从源码编译，编译脚本见仓库 build 目录

【四、本程序会做什么、不会做什么】
  会：读取 Steam 的 addons 与创意工坊目录、读写自己的配置/数据库（%AppData%\L4D2ModManager）、
      通过「重命名 xxx.vpk <-> xxx.vpk.disabled」来启用/禁用 Mod、可选下载创意工坊内容。
  不会：不会修改 VPK 内部内容；不会删除你的 Mod（删除必须手动确认）；
        不会开机自启、不联网上传任何数据（仅访问 Steam 官方接口与创意工坊页面）。

【五、第一次使用】
  启动后会自动探测 Steam 并扫描 addons 目录；
  在「设置」里可以添加额外目录（例如 D:\Games\L4D2\addons）。
  管理员权限下无法拖放文件（Windows 限制），请用「添加 Mod 文件」按钮。

【六、文件哈希】
"@

$hashLines = @()
# 只列出关键文件（运行时 DLL 有 200 多个，对申诉与校验没有帮助）
$keyNames = @('Setup.exe', 'L4D2ModManager.Setup.exe', 'L4D2ModManager.exe', 'payload.zip',
    'L4D2ModManager-portable.zip', 'Setup.zip')
foreach ($file in (Get-ChildItem -LiteralPath $packageDir -Recurse -File -ErrorAction SilentlyContinue |
                       Where-Object { $keyNames -contains $_.Name } |
                       Sort-Object Name)) {
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    $relative = '.' + $file.FullName.Substring($packageDir.Length)
    $hashLines += ("  SHA256  {0}  {1}" -f $hash, $relative)
}

if ($hashLines.Count -eq 0) { $hashLines += "  （本分发包中没有可执行文件）" }

$readme += ($hashLines -join [Environment]::NewLine)
$readme += [Environment]::NewLine + [Environment]::NewLine + "生成时间：" + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')

$readmePath = Join-Path $packageDir '先读我.txt'
$readme | Set-Content -LiteralPath $readmePath -Encoding UTF8

# ---------------------------------------------------------------- 6. 打包
$zipPath = Join-Path $distRoot "$packageName.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $packageDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

# ---------------------------------------------------------------- 7. 输出结果
Write-Host ""
Write-Host "== 分发包已生成 ==" -ForegroundColor Green
Write-Host "  目录：$packageDir"
Write-Host "  压缩包：$zipPath（$([math]::Round((Get-Item $zipPath).Length / 1MB, 1)) MB）"
Write-Host ""
Write-Host "  发给别人时建议直接发这个 zip（不要发裸 exe，即时通讯工具可能二次扫描/改名）"
Write-Host "  对方解压后先看「先读我.txt」；遇到 SmartScreen 提示按里面的说明处理。"
Write-Host ""
Write-Host "== 提交误报申诉时需要附上的哈希 ==" -ForegroundColor Cyan
$hashLines | ForEach-Object { Write-Host $_ }
Write-Host ""
Write-Host "微软误报提交：https://www.microsoft.com/en-us/wdsi/filesubmission" -ForegroundColor Cyan
