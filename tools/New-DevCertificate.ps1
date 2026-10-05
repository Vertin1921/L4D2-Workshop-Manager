<#
  tools/New-DevCertificate.ps1
  生成一张**自签名代码签名证书**并加入本机信任（用于消除本机的
  「Windows 已保护你的电脑 / 发行者：发布者未知」提示）。

  重要说明（务必理解）：
    · 自签名证书只在你自己的电脑上有效（因为只在这台机器被信任）；
      分发给别人时，对方仍会看到"未知发布者"，除非购买正式代码签名证书（OV/EV）。
    · 本机安装信任后，SmartScreen 与应用属性里的"发行者"会显示这里的 Subject。

  用法（不需要管理员权限，证书装在"当前用户"存储区）：
    pwsh -File .\tools\New-DevCertificate.ps1
    pwsh -File .\tools\New-DevCertificate.ps1 -Subject "CN=Your Name" -Years 5
    然后：pwsh -File .\tools\Sign-Artifacts.ps1 -Thumbprint <输出的指纹>
#>
[CmdletBinding()]
param(
    [string]$Subject = 'CN=L4D2 Mod Manager Project, O=L4D2 Mod Manager, C=CN',
    [int]$Years = 3,
    [string]$OutDir = '',

    # PFX 导出密码（不传则交互式询问）
    [string]$PfxPassword = '',

    # 只生成证书与导出文件，不写入「受信任的根证书颁发机构 / 受信任的发布者」
    [switch]$NoTrust
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $OutDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'certs'
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Write-Host "正在生成自签名代码签名证书…" -ForegroundColor Cyan

# 代码签名用途的 EKU：1.3.6.1.5.5.7.3.3
$cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject $Subject `
    -KeyAlgorithm RSA `
    -KeyLength 2048 `
    -KeyUsage DigitalSignature `
    -KeyExportPolicy Exportable `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -NotAfter (Get-Date).AddYears($Years) `
    -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3')

Write-Host "证书指纹：$($cert.Thumbprint)" -ForegroundColor Green

# 导出 CER（公钥）与 PFX（含私钥，用于其他机器签名）
$cerPath = Join-Path $OutDir 'L4D2ModManager-Dev.cer'
$pfxPath = Join-Path $OutDir 'L4D2ModManager-Dev.pfx'
Export-Certificate -Cert $cert -FilePath $cerPath -Force | Out-Null

$password = $PfxPassword
if ([string]::IsNullOrWhiteSpace($password)) {
    $password = Read-Host -Prompt '为导出的 PFX 设置一个密码（留空则使用默认密码 l4d2mm）'
}
if ([string]::IsNullOrWhiteSpace($password)) { $password = 'l4d2mm' }
$secure = ConvertTo-SecureString -String $password -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $secure -Force | Out-Null

# 加入本机信任：受信任的根证书颁发机构 + 受信任的发布者
# （这一步决定了签名后 SmartScreen 是否还会提示「未知发布者」）
# 优先用 .NET 证书存储 API；在无交互会话中导入 Root 需要用户确认，
# 因此失败时回退到 certutil（非交互、免弹窗）。
if (-not $NoTrust) {
    function Add-ToStore {
        param([string]$StoreName)

        try {
            Import-Certificate -FilePath $cerPath -CertStoreLocation "Cert:\CurrentUser\$StoreName" -ErrorAction Stop | Out-Null
            Write-Host "  已加入 $StoreName（.NET API）"
            return
        }
        catch {
            Write-Host "  .NET API 导入 $StoreName 失败（$($_.Exception.Message)），改用 certutil…" -ForegroundColor DarkGray
        }

        $certUtil = Join-Path $env:SystemRoot 'System32\certutil.exe'
        if (Test-Path $certUtil) {
            & $certUtil -user -addstore -f $StoreName $cerPath | Out-Null
            Write-Host "  已加入 $StoreName（certutil）"
        }
        else {
            Write-Warning "  无法把证书加入 $StoreName，请手动导入 $cerPath"
        }
    }

    Add-ToStore -StoreName 'Root'
    Add-ToStore -StoreName 'TrustedPublisher'
}

Write-Host ""
Write-Host "完成：" -ForegroundColor Green
if ($NoTrust) {
    Write-Host "  · 未修改信任设置（-NoTrust）：签名后校验状态会是 UnknownError（证书未被信任的正常表现）"
}
else {
    Write-Host "  · 已把证书加入「当前用户 → 受信任的根证书颁发机构 / 受信任的发布者」"
}
Write-Host "  · 公钥：$cerPath"
Write-Host "  · 私钥：$pfxPath（密码：$password，请妥善保管）"
Write-Host ""
Write-Host "下一步：签名程序与安装包" -ForegroundColor Cyan
Write-Host "  pwsh -File .\tools\Sign-Artifacts.ps1 -Thumbprint $($cert.Thumbprint)"
Write-Host ""
Write-Host "如果只是想马上打开被拦截的安装包，也可以直接：" -ForegroundColor Cyan
Write-Host "  pwsh -File .\tools\Unblock-Files.ps1"
