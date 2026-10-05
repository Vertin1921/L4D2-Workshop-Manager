<#
  tools/Sign-Artifacts.ps1
  用代码签名证书给主程序与安装包签名（消除 SmartScreen 的"未知发布者"）。

  三种用法：
    1) 用开发自签名证书（先跑 New-DevCertificate.ps1）：
         pwsh -File .\tools\Sign-Artifacts.ps1 -Thumbprint <指纹>
    2) 用 PFX 文件（正式证书常见形式）：
         pwsh -File .\tools\Sign-Artifacts.ps1 -PfxPath .\certs\my.pfx -PfxPassword '***'
    3) 不传参数：自动在证书库里找 Subject 含 "L4D2 Mod Manager" 的代码签名证书

  说明：
    · 使用 PowerShell 内置的 Set-AuthenticodeSignature（SHA256 + 时间戳），无需安装 Windows SDK；
    · 自签名证书只在被信任的机器上消除提示；正式分发请用 OV/EV 代码签名证书。
#>
[CmdletBinding()]
param(
    [string]$Thumbprint = '',
    [string]$PfxPath = '',
    [string]$PfxPassword = '',
    [string]$TimestampUrl = 'http://timestamp.digicert.com',

    # 默认签名 artifacts 下的全部可执行文件
    [string]$Path = ''
)

$ErrorActionPreference = 'Stop'

function Find-CodeSigningCertificate {
    param([string]$Thumbprint)

    if (-not [string]::IsNullOrWhiteSpace($Thumbprint)) {
        $normalized = $Thumbprint.Replace(' ', '').ToUpperInvariant()
        $found = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
            Where-Object { $_.Thumbprint -eq $normalized } |
            Select-Object -First 1
        if ($found) { return $found }
        throw "在证书库里找不到指纹为 $Thumbprint 的证书。"
    }

    if (-not [string]::IsNullOrWhiteSpace($PfxPath)) {
        if (-not (Test-Path -LiteralPath $PfxPath)) { throw "找不到 PFX 文件：$PfxPath" }
        $secure = ConvertTo-SecureString -String $PfxPassword -Force -AsPlainText
        $imported = Import-PfxCertificate -FilePath $PfxPath -CertStoreLocation 'Cert:\CurrentUser\My' -Password $secure
        Write-Host "已导入 PFX 证书：$($imported.Thumbprint)" -ForegroundColor Cyan
        return $imported
    }

    $auto = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
        Where-Object { $_.Subject -like '*L4D2 Mod Manager*' } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
    if ($auto) { return $auto }

    throw '没有找到可用的代码签名证书。请先运行 tools\New-DevCertificate.ps1，或用 -PfxPath / -Thumbprint 指定证书。'
}

$certificate = Find-CodeSigningCertificate -Thumbprint $Thumbprint
Write-Host "使用证书：$($certificate.Subject)" -ForegroundColor Cyan
Write-Host "有效期至：$($certificate.NotAfter)" -ForegroundColor Cyan
Write-Host ""

if ([string]::IsNullOrWhiteSpace($Path)) {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $artifacts = Join-Path $repoRoot 'artifacts'
    if (-not (Test-Path $artifacts)) { throw "找不到 artifacts 目录，请先运行 build\build-release.ps1" }
    $targets = Get-ChildItem -LiteralPath $artifacts -Recurse -File -Include '*.exe' -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -in @('Setup.exe', 'L4D2ModManager.exe', 'L4D2ModManager.Setup.exe') }
}
else {
    $targets = Get-ChildItem -LiteralPath $Path -Recurse -File -Include '*.exe' -ErrorAction SilentlyContinue
}

if ($targets.Count -eq 0) { throw '没有找到需要签名的可执行文件。' }

$ok = 0
$failed = 0
foreach ($file in $targets) {
    try {
        $result = Set-AuthenticodeSignature -FilePath $file.FullName -Certificate $certificate `
            -HashAlgorithm SHA256 -TimestampServer $TimestampUrl
        if ($result.Status -eq 'Valid') {
            $ok++
            Write-Host "已签名：$($file.FullName)" -ForegroundColor Green
        }
        else {
            $failed++
            Write-Host "签名异常（$($result.Status)）：$($file.FullName) — $($result.StatusMessage)" -ForegroundColor Yellow
        }
    }
    catch {
        $failed++
        Write-Host "签名失败：$($file.FullName) — $($_.Exception.Message)" -ForegroundColor Red
    }
}

Write-Host ""
Write-Host "完成：成功 $ok 个，失败 $failed 个" -ForegroundColor $(if ($failed -eq 0) { 'Green' } else { 'Yellow' })
Write-Host "验证方式：右键 exe → 属性 → 数字签名；或运行 Get-AuthenticodeSignature <文件>" -ForegroundColor Cyan
