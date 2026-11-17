# LoR 英雄之路记牌器（LoRPoCTracker）

《英雄联盟：符文之地传说》英雄之路（Path of Champions / PoC）模式的 Windows 桌面悬浮窗记牌器。在对局中实时显示己方剩余牌库、对手已出牌、本局已见牌与大地图 Boss 牌库，并支持 Boss 牌库的录制、编辑、导入导出与多数据源管理。

English description: A Windows desktop overlay deck tracker for *Legends of Runeterra*'s Path of Champions (PoC). It shows your remaining deck, cards played by the opponent, cards seen this run, and map Boss decks in real time, with Boss deck recording, editing, import/export, and multi-source management.

---

## 中文文档

### 主要功能

- **实时记牌**：通过本地客户端 API（`http://127.0.0.1:21337`）与游戏日志跟踪对局，自动扣减已出牌；对手出牌按实例 ID 去重；支持右键标记衍生牌、已去除/撤销。
- **大地图 Boss 牌库**：进入大地图时按节点显示 Boss 牌库（含头像），可排除本局已击败的 Boss；结合「已录制牌库 − 本局已出牌」推算剩余牌。
- **牌库录制与管理**（管理员模式）：将每场 Boss 战出过的牌合并保存到本地 `data/encounters.json`，支持定档、重命名、导出/导入 `.lorboss.json`，以及从云端只读数据源增量合并。
- **多数据源**：本地源（唯一可写）、jsDelivr 云源、GitHub raw 备用源；网络源只读，支持多个直链与断网缓存回退。
- **卡牌数据**：使用 Riot Data Dragon（set1–set9 等）与 PvE `tpoc` 数据；从游戏安装目录提取 99 系列原画/卡框并本地渲染；卡图按需下载并带 wiki 兜底。
- **在线更新**：启动时检查 `update/manifest.json`，并发读取 Gitee / jsDelivr / GitHub 多个源并取最高版本，多源回退、SHA256 校验后下载安装。
- **单实例**：重复启动不会开出第二个进程，会自动唤出已在托盘或隐藏的悬浮窗。
- **界面与交互**：浅色/深色主题跟随 Windows；窗口可拖拽、自动贴边、透明度可调；菜单中可切换数据源、牌库显示、点击穿透等。

### 快捷键

| 快捷键 | 功能 |
| --- | --- |
| `Ctrl + Shift + O` | 切换点击穿透（鼠标事件穿透悬浮窗） |
| `Ctrl + Shift + R` | 重置本局记录（仅管理员模式） |

### 运行环境与安装

- 操作系统：Windows 10 / 11
- 运行时：无需安装 .NET，发布包已包含 .NET 9 运行时（self-contained），解压即用
- 从 [Releases](https://github.com/Nohtaehp/LoRPoCTracker/releases) 下载 `LoRPoCTracker-<版本>-win-x64.zip`，解压后运行 `LoRPoCTracker.exe`

### 从源码构建

```powershell
dotnet publish src/LoRPoCTracker/LoRPoCTracker.csproj -c Release -r win-x64 --self-contained true -o publish
```

命令行参数：

- `--import-assets`：导入并校验当前版本卡牌资源后退出
- `--show-version`：解析 Data Dragon 版本后退出

配置文件位于程序目录下 `data/config.json`（API 端口、区域语言、游戏目录、轮询间隔、窗口外观、录制选项等）。

### 更新与国内镜像

更新检测会并发读取所有源并取版本号最高的清单：**Gitee 镜像 → jsDelivr CDN → GitHub raw → ghfast / gh-proxy 加速镜像**。更新包优先从 Gitee Release 下载，失败后自动回退 GitHub Releases 及加速镜像。

Gitee 镜像仓库：[wuyi12138/LoRPoCTracker](https://gitee.com/wuyi12138/LoRPoCTracker)。代码在 `main` 分支 push 后由 `Mirror to Gitee` workflow 实时同步；发布时由 `Build & Release` workflow 创建 Gitee 发行版（安装包附件不在 CI 上传，避免跨网上传超时）。

发版后在本机运行一次 `upload-gitee-release.bat`（或 `pwsh tools/upload-gitee-release.ps1 -Version <版本>`）把安装包上传到 Gitee 发行版；令牌通过环境变量 `GITEE_TOKEN` 或运行时输入提供。项目内置 `.codebuddy/skills/gitee-release` skill，AI 发版时会自动执行该步骤。

GitHub 仓库 Secrets 需配置 `GITEE_TOKEN`（用于代码/tag 同步与发行版创建，需 `projects` 权限）；可选 `GITEE_USER`、`GITEE_REPO`（默认 `wuyi12138/LoRPoCTracker`）。Gitee 免费版限制：单附件不超过 100 MB、仓库附件总量不超过 1 GB。

### 数据来源与版权

卡牌名称、图片等游戏数据来自 Riot Games 的 Data Dragon 与本地游戏客户端。应用图标（悠米）取自 Riot Data Dragon 的官方英雄头像；`COPYRIGHT` 内容为 `Copyright Riot Games, Inc. 2019`；补充图标来源见 `tools/icons/README.md`（CC BY 3.0）。本仓库 `docs/` 目录中的 99 系列卡牌清单与统计表为本地核对资料，不纳入版本库、不随发布包分发。

---

## English Documentation

### Features

- **Real-time deck tracking**: follows the local game client API (`http://127.0.0.1:21337`) and log files, automatically removes played cards, de-duplicates opponent plays by instance ID, and supports right-click actions for derived cards / "removed" toggling.
- **Map Boss decks**: shows Boss decks (with avatars) by node on the world map, hides defeated Bosses, and estimates remaining cards from `recorded deck − cards played this run`.
- **Deck recording & management (Admin Mode)**: merges cards seen in each Boss encounter into the local `data/encounters.json`; supports finalizing, renaming, exporting/importing `.lorboss.json`, and incremental merging from read-only cloud sources.
- **Multiple sources**: a local source (the only writable one), a jsDelivr cloud source, and a GitHub raw fallback; network sources are read-only, accept multiple URLs, and fall back to cached data offline.
- **Card data**: Riot Data Dragon (set1–set9, etc.) plus PvE `tpoc` data; extracts 99-series illustrations/frames from the game installation for local rendering; downloads card images on demand with a wiki fallback.
- **Online updates**: checks `update/manifest.json` on startup, reads all Gitee / jsDelivr / GitHub sources concurrently and picks the highest version, then falls back across mirrors with SHA256 verification before installing.
- **Single instance**: launching again brings the existing overlay back from the tray instead of starting a second process.
- **UI**: Windows light/dark theme awareness; draggable window, auto-docking, adjustable opacity, and menu toggles for data sources, deck display, click-through, etc.

### Shortcuts

| Shortcut | Action |
| --- | --- |
| `Ctrl + Shift + O` | Toggle click-through |
| `Ctrl + Shift + R` | Reset the current run (Admin Mode only) |

### Requirements & installation

- OS: Windows 10 / 11
- Runtime: none required — the release package is self-contained and bundles .NET 9
- Download `LoRPoCTracker-<version>-win-x64.zip` from [Releases](https://github.com/Nohtaehp/LoRPoCTracker/releases), extract it, and run `LoRPoCTracker.exe`

### Build from source

```powershell
dotnet publish src/LoRPoCTracker/LoRPoCTracker.csproj -c Release -r win-x64 --self-contained true -o publish
```

Command-line options:

- `--import-assets`: import and verify card assets for the current game version, then exit
- `--show-version`: resolve the Data Dragon version, then exit

Settings live in `data/config.json` next to the executable (API port, locale, game directory, poll interval, appearance, recording options, etc.).

### Updates & mirrors

Update checks read all sources concurrently and pick the highest version: **Gitee mirror → jsDelivr CDN → GitHub raw → ghfast / gh-proxy mirrors**. Packages are downloaded from Gitee Releases first, then fall back to GitHub Releases and proxies.

Gitee mirror: [wuyi12138/LoRPoCTracker](https://gitee.com/wuyi12138/LoRPoCTracker). Code is mirrored in near real time by the `Mirror to Gitee` workflow on every push to `main`; the `Build & Release` workflow creates the Gitee release (the package is not uploaded from CI to avoid cross-border upload timeouts).

After a release, run `upload-gitee-release.bat` locally (or `pwsh tools/upload-gitee-release.ps1 -Version <version>`) to upload the package to the Gitee release; provide the token via the `GITEE_TOKEN` environment variable or on prompt. The `.codebuddy/skills/gitee-release` skill automates this step during releases.

Add a `GITEE_TOKEN` secret (Gitee personal access token with `projects` scope) to the GitHub repository for code/tag sync and release creation; `GITEE_USER` and `GITEE_REPO` are optional and default to `wuyi12138/LoRPoCTracker`. Gitee free-tier limits: 100 MB per attachment and 1 GB total attachments per repository.

### Data sources & copyright

Card names and images come from Riot Games' Data Dragon and the local game client. The app icon (Yuumi) is taken from Riot Data Dragon's official champion portrait. See `COPYRIGHT` (`Copyright Riot Games, Inc. 2019`) and `tools/icons/README.md` (CC BY 3.0) for details. The 99-series card checklists under `docs/` are local reference data and are intentionally excluded from this repository and release packages.
