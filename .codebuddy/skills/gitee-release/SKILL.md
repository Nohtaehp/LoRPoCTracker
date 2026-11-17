---
name: gitee-release
description: 一键发版并同步到 Gitee。当用户说「发版」「发布新版本」「打个更新」「同步到 Gitee」「更新 Gitee 发行版」时使用。
---

# 一键发版（GitHub → Gitee）

## 最快方式

在仓库根目录双击或运行：

```cmd
release.bat
```

自动完成完整链路：

1. 提交未提交改动（commit message：`chore: release vX.Y.Z`）
2. 按最新 tag 自动递增 patch 版本（也可手动指定）
3. 打 tag 并推送 `main` 与 tag
4. 轮询 GitHub Actions `Build & Release`，等待构建完成
5. 调用 `tools/upload-gitee-release.ps1` 下载安装包并上传到 Gitee 发行版
6. 验证 Gitee 下载地址并输出最终 URL

指定版本：

```cmd
release.bat 1.0.6
```

## 令牌（只配置一次）

- 首次运行会提示输入 Gitee 私人令牌，并保存到 `%USERPROFILE%\.lor-gitee-token`
- 之后自动读取，无需再次输入
- 环境变量 `GITEE_TOKEN` 优先级最高（适合 CI 或临时覆盖）
- 令牌禁止提交进仓库

## 只上传附件（不重新发版）

GitHub Actions 已构建完成、只想补传 Gitee 附件时：

```cmd
upload-gitee-release.bat 1.0.5
```

省略版本号时自动取最新 tag。

## AI 手动执行

1. 检查工作区（`git status`），必要时先提交
2. 执行：

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File tools/release.ps1 -Version 1.0.6
   ```

3. 等待输出 `发版完成：<url>`；若失败，脚本会给出 Actions 运行链接

## 注意事项

- 安装包约 71 MB；Gitee 免费版单附件上限 100 MB、仓库附件总量上限 1 GB，版本多了需要清理旧附件。
- 不要在 GitHub Actions 里上传安装包到 Gitee：海外 runner 到 Gitee 会被限速卡住。
- GitHub Actions 查询使用匿名 API（60 次/小时），一次发版约消耗 30 次，不要频繁重跑。
- 上传成功后客户端会并发读取 Gitee / jsDelivr / GitHub 源取最高版本，安装包优先从 Gitee 下载。
