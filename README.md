# HistoryMercury

> 项目与入口的调度台：桌面项目坞、全局快捷键与资源管理器入口

## 定位

HistoryMercury 为 `C:\OneHistory\HistoryClio` 项目库提供桌面项目坞（磁贴 `HC`）、全局快捷键、
资源管理器托管入口（`HistoryClio 项目`）与使用历史。

- 桌面坞是 Mercury 自己的窗口，跑在模块自有的 STA 线程上，前端在不在都照常显示。
- 坞上恒为 12 个项目 + 「主界面」+「+」共 14 格；点「+」弹出项目列表，搜索后点一行即固定到坞上。不往 Aurora 注册页面或场景。
- 不提供文件内容读写、Git 操作（归 Janus）或界面布局（归 Aurora）。

## 概况

| 项 | 值 |
| --- | --- |
| 编号 | `2026-021` |
| 角色 | 宿主模块（`kind=module`） |
| 指令域 | `mercury`（代码命名空间 `Mercury`，模块身份 `HistoryMercury`） |
| 界面 | 桌面坞与「+」项目列表（均为自有窗口） |
| MCP 投影 | `standard` |
| 版本与宿主下限 | [`HistoryMercury.csproj`](./b-Code-MercuryDock/HistoryMercury.csproj)；最低宿主 HistoryVulcan 6.0.0（见[现行约定](./b-Office/current/现行约定.md)） |

## 能力

| 类 | 指令 | 用途 |
| --- | --- | --- |
| `proj` | `list` / `open` / `pin` / `unpin` / `exclude` / `include` / `add` / `refresh` | 活动项目清单与固定（最多 12 个） |
| `dock` | `show` / `hide` / `add` / `remove` / `policy` | 项目坞、常驻指令与半衰期 |
| `shortcut` | `add` / `open` / `pick` / `wakeconsole` | 快捷文件与唤起控制台 |
| `hotkey` | `list` / `register` / `unregister` | 全局快捷键 |
| `app` / `explorer` | `app.open` / `app.status` / `explorer.register` / `explorer.remove` | 主程序与资源管理器入口 |
| `usage` | `list` / `forget` | 使用历史 |
| — | `mercury.go` | 控制台域聚焦（转发 `aurora.log.source`） |

完整命令目录与参数是注册时的自描述：`diana.docs.read domain=mercury`（宿主 6.1.0 起没有消费文档）。

## 入口

| 入口 | 用途 |
| --- | --- |
| [`AGENTS.md`](./AGENTS.md) | AI 工作合同：读取顺序、真值判定、边界 |
| [`project.manifest.json`](./project.manifest.json) | 项目身份、活动目录、文档与命令 |
| [现行约定](./b-Office/现行约定.md) | 唯一的长期文档：真相在哪、取舍、真机才知道的、工程约定 |

## 目录

| 路径 | 职责 |
| --- | --- |
| `b-Code-MercuryDock/` | 模块源码：`Module`、`Commands`、`Dock`、`Explorer`、`State`、`Ui`、`Input`、`Diagnostics` |
| `b-Code-Tests/` | `HistoryMercury.Smoke` |
| `b-Code/` | 候选构建脚本 |
| `b-Office/` | `current/现行约定.md`，仅此一份 |
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
- 项目扫描优先 `proj.libraryroot`（默认 `C:\OneHistory\HistoryClio`）；配置的目录不存在时回落到缺省库根。
- 坞上磁贴右键：固定 / 取消固定、刷新；其余管理走 `mercury.*` 指令。

## 保留内容

### 学习许可与权利保留

- 本项目允许个人、学校和研究者进行非商业性的学习、研究和引用。
- 未经著作权人书面许可，不得将项目内容用于商业开发、产品制造、商业服务、商业宣传、投标交付、毕业设计成品交付或再授权。
- 不得删除作者、来源、版本历史和第三方许可证声明，不得将本项目或其衍生成果宣称为与作者无关的原创项目。
- 软件源码适用 [PolyForm Noncommercial 1.0.0](https://polyformproject.org/licenses/noncommercial/1.0.0/)；机械模型、PCB、原理图、图片、文档、Unity 工程和其他非软件资料适用 [CC BY-NC-ND 4.0](https://creativecommons.org/licenses/by-nc-nd/4.0/)。
- SE、SW、STEP/STP 及其他三维模型仅允许个人或学校进行非商业学习、评审和研究；未经著作权人书面许可，不得用于加工、打样、制造、销售、量产、投标、产品交付、商业宣传、改作或再授权。
- 第三方组件、模板、元件库、外购件模型和工具仍受其原始许可证约束；本声明不扩大作者对第三方内容的权利。

商业使用、产品化、公司内部长期使用、转让或排他性授权需另行取得著作权人的书面许可。