# 本地把 GitHub Release 安装包上传到 Gitee 发行版
# 用法：pwsh tools/upload-gitee-release.ps1 -Version 1.0.4 -Token <Gitee私人令牌>
# 或先设置环境变量 GITEE_TOKEN 后省略 -Token
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Token = $env:GITEE_TOKEN,
    [string]$Repo = "wuyi12138/LoRPoCTracker"
)

$ErrorActionPreference = "Stop"
if (-not $Token) { throw "缺少 Gitee 令牌：请设置环境变量 GITEE_TOKEN 或传 -Token" }

$Version = $Version.TrimStart('v', 'V')
$tag = "v$Version"
$pkg = "LoRPoCTracker-$Version-win-x64.zip"
$tmp = Join-Path $env:TEMP "lor-gitee-$Version"
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
$zip = Join-Path $tmp $pkg

if (-not (Test-Path $zip)) {
    $direct = "https://github.com/Nohtaehp/LoRPoCTracker/releases/download/$tag/$pkg"
    $sources = @(
        $direct,
        "https://ghfast.top/$direct",
        "https://gh-proxy.com/$direct",
        "https://ghproxy.net/$direct"
    )

    $downloaded = $false
    foreach ($src in $sources) {
        try {
            Write-Host "下载安装包：$src"
            Invoke-WebRequest -Uri $src -OutFile $zip -UseBasicParsing -TimeoutSec 1800
            $downloaded = $true
            break
        }
        catch {
            Write-Warning "下载失败：$($_.Exception.Message)"
            if (Test-Path $zip) { Remove-Item $zip -Force }
        }
    }

    if (-not $downloaded) { throw "所有下载源均失败，请检查网络" }
}
Write-Host ("安装包大小：{0:N2} MB" -f ((Get-Item $zip).Length / 1MB))

$api = "https://gitee.com/api/v5/repos/$Repo"
$body = @{
    access_token     = $Token
    tag_name         = $tag
    name             = $tag
    target_commitish = "main"
    body             = "详见 Release 页面：https://github.com/Nohtaehp/LoRPoCTracker/releases/tag/$tag"
} | ConvertTo-Json

try {
    $release = Invoke-RestMethod -Method Post -Uri "$api/releases" -Body $body `
        -ContentType "application/json" -TimeoutSec 120
    Write-Host "已创建 Gitee 发行版 $tag"
}
catch {
    $release = Invoke-RestMethod -Uri "$api/releases/tags/$tag`?access_token=$Token" -TimeoutSec 60
    Write-Host "Gitee 发行版已存在，复用（ID $($release.id)）"
}

Write-Host "上传附件（约 71MB，请耐心等待）..."
# 用系统自带 curl.exe 上传，兼容 Windows PowerShell 5.1 与 PowerShell 7
& curl.exe -# --max-time 1800 -X POST "$api/releases/$($release.id)/attach_files" `
    -F "access_token=$Token" -F "file=@$zip"
if ($LASTEXITCODE -ne 0) { throw "附件上传失败（curl 退出码 $LASTEXITCODE）" }

Write-Host "完成：https://gitee.com/$Repo/releases/tag/$tag"
