<#
  build/build-release.ps1 —— 一键生成可发布的 Release x64 产物

  执行内容：
    1. 逐项目还原 NuGet 包（使用仓库内 NuGet.config）
    2. 编译整个解决方案（Release）
    3. 运行自检程序（tests\L4D2ModManager.Tests）
    4. 发布主程序为 win-x64（默认自包含，无需预装 .NET 运行时）
    5. 打包为 payload.zip 并生成单文件安装程序 Setup.exe
    6. 输出到 artifacts\ 目录

  用法：
    pwsh -File .\build\build-release.ps1
    pwsh -File .\build\build-release.ps1 -SkipTests
    pwsh -File .\build\build-release.ps1 -FrameworkDependent   # 生成体积更小的依赖版
    pwsh -File .\build\build-release.ps1 -RealVpkDirectory "D:\...\left4dead2\addons"  # 用真实 Mod 验证
#>
[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$FrameworkDependent,
    [string]$RealVpkDirectory = '',

    # 打开安装程序二次压缩（仅单文件模式有效；体积约 -40%，但更容易被杀软误报）
    [switch]$CompressInstaller,

    # 生成单文件安装包（载荷内嵌，分发方便但误报风险更高）
    [switch]$SingleFileInstaller,

    # 构建完成后自动用代码签名证书签名（消除 SmartScreen "未知发布者"）
    [switch]$Sign,

    # 指定签名证书指纹（留空则自动查找 Subject 含 "L4D2 Mod Manager" 的证书）
    [string]$SignThumbprint = '',

    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'env.ps1')

$repoRoot = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $repoRoot 'artifacts'
$nugetConfig = Join-Path $repoRoot 'NuGet.config'
$solution = Join-Path $repoRoot 'L4D2ModManager.sln'

$coreProject = Join-Path $repoRoot 'src\L4D2ModManager.Core\L4D2ModManager.Core.csproj'
$appProject = Join-Path $repoRoot 'src\L4D2ModManager.App\L4D2ModManager.App.csproj'
$setupProject = Join-Path $repoRoot 'src\L4D2ModManager.Setup\L4D2ModManager.Setup.csproj'
$testProject = Join-Path $repoRoot 'tests\L4D2ModManager.Tests\L4D2ModManager.Tests.csproj'

$publishDir = Join-Path $artifacts 'L4D2ModManager-win-x64'
$payloadZip = Join-Path $repoRoot 'src\L4D2ModManager.Setup\payload.zip'
$setupOutDir = Join-Path $artifacts 'Setup'

$selfContained = -not $FrameworkDependent

function Write-Step([string]$text) {
    Write-Host ""
    Write-Host "==== $text" -ForegroundColor Cyan
}

function Invoke-Dotnet([string[]]$arguments) {
    & $env:DOTNET_EXE @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet 执行失败（退出码 $LASTEXITCODE）：dotnet $($arguments -join ' ')"
    }
}

Write-Host "== L4D2 Mod Manager 发布构建 ==" -ForegroundColor Green
Write-Host "   SDK      : $env:DOTNET_EXE"
Write-Host "   仓库      : $repoRoot"
Write-Host "   产物目录  : $artifacts"
Write-Host "   运行模式  : $(if ($selfContained) { '自包含（无需 .NET 运行时）' } else { '依赖 .NET 8 桌面运行时' })"

if (Test-Path $artifacts) {
    Write-Host "   清理旧的 artifacts…"
    Remove-Item $artifacts -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

# ---------------------------------------------------------------- 1. 还原
Write-Step "还原 NuGet 包"
foreach ($project in @($coreProject, $appProject, $testProject, $setupProject)) {
    Invoke-Dotnet @('restore', $project, '--configfile', $nugetConfig, '--nologo')
}

# ---------------------------------------------------------------- 2. 编译
Write-Step "编译解决方案（Release）"
# 单进程 MSBuild（-m:1）：避免并行编译争用同一输出文件（受限环境/杀毒软件下可能静默失败）
Invoke-Dotnet @('build', $solution, '-c', 'Release', '--no-restore', '--nologo', '-m:1')

# ---------------------------------------------------------------- 3. 自检
if (-not $SkipTests) {
    Write-Step "运行自检程序"
    if (-not [string]::IsNullOrWhiteSpace($RealVpkDirectory)) {
        $env:L4D2MM_TEST_VPK_DIR = $RealVpkDirectory
        Write-Host "   使用真实 Mod 目录：$RealVpkDirectory"
    }

    $testExe = Join-Path $repoRoot 'tests\L4D2ModManager.Tests\bin\Release\net8.0-windows\L4D2ModManager.Tests.exe'
    if (-not (Test-Path $testExe)) { throw "找不到自检程序：$testExe" }

    & $testExe
    if ($LASTEXITCODE -ne 0) { throw '自检未通过，终止发布' }
}

# ---------------------------------------------------------------- 4. 发布主程序
Write-Step "发布主程序（$Runtime）"
$publishArgs = @(
    'publish', $appProject,
    '-c', 'Release',
    '-r', $Runtime,
    '--self-contained', $selfContained.ToString().ToLowerInvariant(),
    '-p:PublishSingleFile=false',
    '-p:DebugType=none',
    '-o', $publishDir,
    '--nologo'
)
Invoke-Dotnet $publishArgs

$publishedExe = Join-Path $publishDir 'L4D2ModManager.exe'
if (-not (Test-Path $publishedExe)) { throw "发布结果中缺少 L4D2ModManager.exe" }

# ---------------------------------------------------------------- 5. 打包载荷
Write-Step "打包安装载荷 payload.zip"
if (Test-Path $payloadZip) { Remove-Item $payloadZip -Force }
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $payloadZip -CompressionLevel Optimal
$payloadSizeMb = [math]::Round((Get-Item $payloadZip).Length / 1MB, 1)
Write-Host "   payload.zip: $payloadSizeMb MB"

# ---------------------------------------------------------------- 6. 生成安装程序
# 默认：文件夹形式安装包（Setup.exe + payload.zip 同级），杀软误报风险最低。
# -SingleFileInstaller：把载荷嵌入单个 exe（分发方便，但更容易被误报）。
Write-Step "生成安装程序 Setup"
$setupProjectDir = Split-Path -Parent $setupProject
$projectPayload = Join-Path $setupProjectDir 'payload.zip'

if ($SingleFileInstaller) {
    Write-Host "   模式：单文件（载荷内嵌）" -ForegroundColor Yellow
    if (-not [string]::Equals($payloadZip, $projectPayload, [System.StringComparison]::OrdinalIgnoreCase)) {
        Copy-Item $payloadZip $projectPayload -Force
    }
    try {
        Invoke-Dotnet @(
            'publish', $setupProject,
            '-c', 'Release',
            '-r', $Runtime,
            '--self-contained', $selfContained.ToString().ToLowerInvariant(),
            '-p:PublishSingleFile=true',
            '-p:EmbedPayload=true',
            "-p:EnableCompressionInSingleFile=$($CompressInstaller.IsPresent.ToString().ToLowerInvariant())",
            '-p:DebugType=none',
            '-o', $setupOutDir,
            '--nologo'
        )
    }
    finally {
        Remove-Item $projectPayload -Force -ErrorAction SilentlyContinue
    }

    # 单文件版本不需要外部载荷
    Remove-Item (Join-Path $setupOutDir 'payload.zip') -Force -ErrorAction SilentlyContinue
}
else {
    Write-Host "   模式：文件夹（Setup.exe + payload.zip，误报最少）" -ForegroundColor Green
    Invoke-Dotnet @(
        'publish', $setupProject,
        '-c', 'Release',
        '-r', $Runtime,
        '--self-contained', $selfContained.ToString().ToLowerInvariant(),
        '-p:PublishSingleFile=false',
        '-p:EmbedPayload=false',
        '-p:DebugType=none',
        '-o', $setupOutDir,
        '--nologo'
    )

    Copy-Item $payloadZip (Join-Path $setupOutDir 'payload.zip') -Force
}

$setupExe = Join-Path $setupOutDir 'L4D2ModManager.Setup.exe'
if (-not (Test-Path $setupExe)) { throw "未生成安装程序：$setupExe" }

# 便捷副本：artifacts\Setup.exe（文件夹模式时必须与 payload.zip 放在一起）
$finalSetup = Join-Path $artifacts 'Setup.exe'
Copy-Item $setupExe $finalSetup -Force
if (-not $SingleFileInstaller) {
    Copy-Item $payloadZip (Join-Path $artifacts 'payload.zip') -Force
}

# 安装包压缩包（推荐的分发形式：解压后运行 Setup.exe）
$setupZip = Join-Path $artifacts 'Setup.zip'
if (Test-Path $setupZip) { Remove-Item $setupZip -Force }
Compress-Archive -Path (Join-Path $setupOutDir '*') -DestinationPath $setupZip -CompressionLevel Optimal

# 便携版压缩包，方便直接解压使用
$portableZip = Join-Path $artifacts 'L4D2ModManager-win-x64-portable.zip'
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $portableZip -CompressionLevel Optimal

# ---------------------------------------------------------------- 7. 代码签名（可选）
if ($Sign) {
    Write-Step "代码签名"
    $signScript = Join-Path $repoRoot 'tools\Sign-Artifacts.ps1'
    try {
        & $signScript -Thumbprint $SignThumbprint
        Write-Host "   签名完成（SmartScreen 不再显示「未知发布者」）" -ForegroundColor Green
    }
    catch {
        Write-Warning "签名失败：$($_.Exception.Message)"
        Write-Warning "提示：先运行 tools\New-DevCertificate.ps1 生成本机开发证书，或购买正式代码签名证书后用 -PfxPath 指定。"
    }
}
else {
    Write-Host ""
    Write-Host "提示：产物未签名，别人首次运行会看到 SmartScreen「发行者：发布者未知」。" -ForegroundColor Yellow
    Write-Host "      签发证书并签名： tools\New-DevCertificate.ps1 → ./build/build-release.ps1 -Sign" -ForegroundColor DarkGray
    Write-Host "      只想去掉下载标记，直接打开： tools\Unblock-Files.ps1" -ForegroundColor DarkGray
}

# 顺手清掉产物可能带的"下载标记"（Mark of the Web），避免解压后又触发 SmartScreen
try {
    Get-ChildItem -LiteralPath $artifacts -Recurse -File -ErrorAction SilentlyContinue |
        ForEach-Object { Unblock-File -LiteralPath $_.FullName -ErrorAction SilentlyContinue }
}
catch {
    # 忽略
}

# ---------------------------------------------------------------- 8. 汇总
Write-Step "构建结果"
$installerRow = if ($SingleFileInstaller) {
    [pscustomobject]@{ 文件 = 'artifacts\Setup.exe'; 说明 = '安装程序（单文件，双击安装）'; 大小 = "$([math]::Round((Get-Item $finalSetup).Length / 1MB, 1)) MB" }
}
else {
    [pscustomobject]@{ 文件 = 'artifacts\Setup.zip'; 说明 = '安装包（解压后运行 Setup.exe，误报最少）'; 大小 = "$([math]::Round((Get-Item $setupZip).Length / 1MB, 1)) MB" }
}

$rows = @(
    $installerRow,
    [pscustomobject]@{ 文件 = 'artifacts\Setup\L4D2ModManager.Setup.exe'; 说明 = '安装程序本体（需与同目录 payload.zip 一起使用）'; 大小 = "$([math]::Round((Get-Item $setupExe).Length / 1MB, 2)) MB" },
    [pscustomobject]@{ 文件 = 'artifacts\L4D2ModManager-win-x64\L4D2ModManager.exe'; 说明 = '便携版主程序（可直接运行）'; 大小 = "$([math]::Round((Get-Item $publishedExe).Length / 1MB, 2)) MB" },
    [pscustomobject]@{ 文件 = 'artifacts\L4D2ModManager-win-x64-portable.zip'; 说明 = '便携版压缩包'; 大小 = "$([math]::Round((Get-Item $portableZip).Length / 1MB, 1)) MB" }
)
$rows | Format-Table -AutoSize

Write-Host "全部完成 ✔" -ForegroundColor Green
Write-Host "提示：Setup.exe 支持静默安装：  Setup.exe /S /dir=D:\L4D2MM" -ForegroundColor DarkGray
