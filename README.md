# HistoryMercury

> 项目与入口的调度台：桌面项目坞、全局快捷键与资源管理器入口

## 定位

HistoryMercury 为 `C:\OneHistory\HistoryClio` 项目库提供桌面项目坞（磁贴 `HC`）、全局快捷键、
资源管理器托管入口（`HistoryClio 项目`）与使用历史。

- 桌面坞是 Mercury 自己的窗口，跑在模块自有的 STA 线程上，前端在不在都照常显示。
- 模块不构造任何前端控件；「扩展坞管理」页以描述化协议声明，由 HistoryAurora 渲染。
- 不提供文件内容读写、Git 操作（归 Janus）或界面布局（归 Aurora）。

## 概况

| 项 | 值 |
| --- | --- |
| 编号 | `2026-021` |
| 角色 | 宿主模块（`kind=module`） |
| 指令域 | `mercury`（代码命名空间 `Mercury`，模块身份 `HistoryMercury`） |
| 界面 | 桌面坞（自有窗口）+ Aurora 描述化页面「扩展坞管理」 |
| MCP 投影 | `standard` |
| 版本与宿主下限 | [`HistoryMercury.csproj`](./b-Code-MercuryDock/HistoryMercury.csproj)；宿主下限见 [模块 API](./b-Office/package/模块API.md) |

## 能力

| 类 | 指令 | 用途 |
| --- | --- | --- |
| `proj` | `list` / `open` / `pin` / `exclude` / `add` / `refresh` | 活动项目清单 |
| `dock` | `show` / `add` / `remove` / `policy` / `run` | 项目坞与收录策略 |
| `shortcut` | `add` / `open` / `pick` / `wakeconsole` | 快捷文件与唤起控制台 |
| `hotkey` | `list` / `register` / `unregister` | 全局快捷键 |
| `app` / `explorer` | `app.open` / `app.status` / `explorer.register` / `explorer.remove` | 主程序与资源管理器入口 |
| `usage` | `list` / `forget` | 使用历史 |
| — | `mercury.go` | 控制台域聚焦（转发 `aurora.log.source`） |

完整参数与返回见 [模块 API](./b-Office/package/模块API.md)。

## 入口

| 入口 | 用途 |
| --- | --- |
| [`AGENTS.md`](./AGENTS.md) | AI 工作合同：读取顺序、真值判定、边界 |
| [`project.manifest.json`](./project.manifest.json) | 项目身份、活动目录、文档与命令 |
| [文档中心](./b-Office/文档中心.md) | 文档索引与读取顺序 |
| [项目概览](./b-Office/current/项目概览.md) | 目标、范围与状态 |
| [技术合同](./b-Office/current/技术合同.md) | 现行需求与架构 |
| [有效决策](./b-Office/current/有效决策.md) | 仍然有效的关键决策 |
| [验证合同](./b-Office/current/验证合同.md) | 验证层级、命令与证据 |
| [模块 API](./b-Office/package/模块API.md) | 跨模块消费合同 |

## 目录

| 路径 | 职责 |
| --- | --- |
| `b-Code-MercuryDock/` | 模块源码：`Module`、`Commands`、`Dock`、`Explorer`、`State`、`Ui`、`Input`、`Diagnostics` |
| `b-Code-Tests/` | `HistoryMercury.Smoke` |
| `b-Code/` | 候选构建脚本 |
| `b-Office/` | 项目文档：`current/` 现行合同、`package/` 消费合同、`history/` 只读归档 |
| `z-Publish/` | 正式快照与 `history/` 归档，由宿主管线写入 |

## 构建与验证

```powershell
dotnet run --project .\b-Code-Tests\HistoryMercury.Smoke\HistoryMercury.Smoke.csproj -c Release -p:NuGetAudit=false
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code\Build-HistoryMercuryPackage.ps1
```

构建脚本在 `z-Publish/HistoryMercury-vX.Y.Z` 生成并校验候选包。

## 开发与发布

改动只进 `vulcan.dev.start` 创建的工作区，经宿主 Console CLI 走
`vulcan.dev.start` → `vulcan.dev.submit`（候选构建并热装送审）→ `vulcan.dev.finish`（批准后并回并写入 `z-Publish`）。
本仓不自行发布。

## 要点

- 快捷方式、状态与日志放在 `%APPDATA%\HistoryVulcan\HistoryMercury`，运行包目录 `Modules\HistoryMercury` 只放不可变、可校验的文件。
- 项目扫描优先 `proj.libraryroot`（默认 `C:\OneHistory\HistoryClio`）；指向旧 `HistoryVesta` 的配置会被改写到 Clio。
- 管理页的行操作只在右键菜单里（`rowActions` 一律 `inline: false`），「加入扩展坞」也是右键弹出。

## 保留内容
- 本模板项目介绍：此为最初的准备的项目模板
    每个分支项目都会由他去继承
- 作者：Pinavia - 2025

![logo](./Logo.png)
