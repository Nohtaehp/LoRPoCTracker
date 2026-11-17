# 一键发版：提交 → 打 tag → 等 GitHub Actions → 上传 Gitee → 验证
# 用法：pwsh tools/release.ps1 -Version 1.0.6
# 省略 -Version 时自动在最新 tag 基础上递增 patch 版本
param(
    [string]$Version,
    [string]$Token = $env:GITEE_TOKEN,
    [string]$Repo = "wuyi12138/LoRPoCTracker",
    [string]$GitHubRepo = "Nohtaehp/LoRPoCTracker"
)

$ErrorActionPreference = "Stop"

# ---------- 令牌 ----------
if (-not $Token) {
    $tokenFile = Join-Path $env:USERPROFILE ".lor-gitee-token"
    if (Test-Path $tokenFile) {
        $Token = (Get-Content $tokenFile -Raw).Trim()
    }
}
if (-not $Token) {
    throw "缺少 Gitee 令牌：请设置环境变量 GITEE_TOKEN，或写入 $env:USERPROFILE\.lor-gitee-token"
}

# ---------- 版本 ----------
git fetch --tags --quiet 2>$null
if (-not $Version) {
    $latest = (git describe --tags --abbrev=0 2>$null)
    if ($latest) {
        $parts = $latest.TrimStart('v').Split('.')
        $Version = "$($parts[0]).$($parts[1]).$([int]$parts[2] + 1)"
    }
    else {
        $Version = "1.0.0"
    }
}
$Version = $Version.TrimStart('v', 'V')
$tag = "v$Version"
Write-Host "=== 发版 $tag ===" -ForegroundColor Cyan

# ---------- 提交 ----------
$dirty = git status --porcelain
if ($dirty) {
    Write-Host "检测到未提交改动，自动提交..."
    git add -A
    git commit -m "chore: release $tag"
    if ($LASTEXITCODE -ne 0) { throw "提交失败" }
}

# ---------- 打 tag 并推送 ----------
$existing = git tag -l $tag
if ($existing) { throw "标签 $tag 已存在，请换一个版本号" }

git tag $tag
if ($LASTEXITCODE -ne 0) { throw "创建标签失败" }

git push origin HEAD:main
if ($LASTEXITCODE -ne 0) { throw "推送 main 失败" }

git push origin $tag
if ($LASTEXITCODE -ne 0) { throw "推送标签失败" }
Write-Host "标签 $tag 已推送，等待 GitHub Actions..."

# ---------- 等待 Actions ----------
$headers = @{ 'User-Agent' = 'lor-release' }
$run = $null
for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Seconds 10
    try {
        $runs = Invoke-RestMethod -Uri "https://api.github.com/repos/$GitHubRepo/actions/runs?per_page=10" -Headers $headers
        $run = $runs.workflow_runs |
            Where-Object { $_.head_branch -eq $tag -and $_.name -eq 'Build & Release' } |
            Select-Object -First 1
    }
    catch {
        Write-Warning "查询 Actions 失败：$($_.Exception.Message)"
    }
    if ($run) { break }
}
if (-not $run) { throw "未找到 $tag 的 Build & Release 运行记录" }

Write-Host "找到运行 #$($run.run_number)，等待完成..."
for ($i = 0; $i -lt 120; $i++) {
    Start-Sleep -Seconds 10
    $current = Invoke-RestMethod -Uri "https://api.github.com/repos/$GitHubRepo/actions/runs/$($run.id)" -Headers $headers
    if ($current.status -eq 'completed') {
        if ($current.conclusion -ne 'success') {
            throw "Actions 运行失败：$($current.conclusion)（$($current.html_url)）"
        }
        Write-Host "Actions 完成（success）"
        break
    }
}

# ---------- 上传 Gitee ----------
$uploadScript = Join-Path $PSScriptRoot "upload-gitee-release.ps1"
& $uploadScript -Version $Version -Token $Token -Repo $Repo

# ---------- 验证 ----------
$assetUrl = "https://gitee.com/$Repo/releases/download/$tag/LoRPoCTracker-$Version-win-x64.zip"
Write-Host "验证 Gitee 下载地址..."
$head = Invoke-WebRequest -Uri $assetUrl -Method Head -UseBasicParsing -TimeoutSec 60
Write-Host ("验证通过：HTTP {0}，大小 {1:N2} MB" -f [int]$head.StatusCode, ([double]$head.Headers['Content-Length'] / 1MB)) -ForegroundColor Green
Write-Host "发版完成：$assetUrl"
