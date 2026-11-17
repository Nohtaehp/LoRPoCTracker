---
name: gitee-release
description: 发布新版本后把 GitHub Release 安装包上传到 Gitee 发行版。当用户说「发版」「发布新版本」「打个更新」「同步到 Gitee」「更新 Gitee 发行版」时使用。
---

# Gitee 发行版上传

## 触发场景

- 用户要求「发版」「发布新版本」「打个更新」
- GitHub Release 已生成，需要把安装包同步到 Gitee

## 发版流程

1. 确认代码已提交并推送到 `main`。

2. 打标签并推送，触发 GitHub Actions（构建 self-contained 包、创建 GitHub Release、同步代码/tag 并创建 Gitee 发行版）：

   ```powershell
   git tag v1.0.5
   git push origin v1.0.5
   ```

3. 等待 GitHub Actions 的 `Build & Release` 完成（约 2 分钟），确认：

   - GitHub Release 已有 `LoRPoCTracker-<版本>-win-x64.zip`（约 71 MB）
   - Gitee 发行版已创建（附件为空是正常的，CI 不上传大文件）

4. 在本地执行 bat，把安装包上传到 Gitee 发行版：

   ```powershell
   .\upload-gitee-release.bat 1.0.5
   ```

   或直接调用脚本（令牌放在环境变量 `GITEE_TOKEN`）：

   ```powershell
   pwsh tools/upload-gitee-release.ps1 -Version 1.0.5 -Token $env:GITEE_TOKEN
   ```

5. 验证附件已可下载：

   ```powershell
   Invoke-WebRequest -Uri "https://gitee.com/wuyi12138/LoRPoCTracker/releases/download/v1.0.5/LoRPoCTracker-1.0.5-win-x64.zip" -Method Head -UseBasicParsing
   ```

## 注意事项

- Gitee 令牌只通过环境变量 `GITEE_TOKEN` 或运行时输入提供，禁止写进仓库或提交历史。
- 安装包约 71 MB；Gitee 免费版单附件上限 100 MB、仓库附件总量上限 1 GB，版本多了需要清理旧附件。
- 不要在 GitHub Actions 里上传安装包到 Gitee：海外 runner 到 Gitee 会被限速卡住。
- 上传成功后，客户端更新检测会并发读取 Gitee / jsDelivr / GitHub 源并取最高版本，安装包优先从 Gitee 下载。
- 若 GitHub Release 尚未生成，先等待 Actions 完成再运行 bat，否则下载会 404。
