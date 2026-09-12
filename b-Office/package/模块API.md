# HistoryMercury 模块 API

模块版本：**5.0.3**；最低宿主：**HistoryVulcan 5.1.0**。

本文件是**总线面**合同：别的模块或远端通过命令总线调用 HistoryMercury 时，看这一篇就够。

- 代码面（CLR 类型、程序集引用、页面协议实现）在 `b-Office/current/技术合同.md`。
- AI 面（MCP 工具名与调用形状）由 MCP 服务封装，本文件不重复。

## 这个模块提供什么

Mercury 是**项目与入口的调度台**：活动项目清单、项目坞（Dock）、全局快捷键、
资源管理器托管入口、使用历史。指令域 `mercury`。

它**不**提供：文件内容读写、Git 操作（那是 Janus）、界面布局（那是 Aurora）。
它不构造任何前端控件——页面以 Aurora 页面注册协议 V1 声明。

## 指令目录

标注说明：**只读** = 不改状态，可安全重复调用；**确认** = 级别 `Ask`，执行前必须有人点头；
**内部** = 声明了 `HiddenReason`，不对远端暴露，跨模块也不要调。

### 项目

| 指令 | 参数 | 说明 |
| --- | --- | --- |
| `mercury.proj.list` | — | 列出活动项目。**只读** |
| `mercury.proj.open` | `name`(0，必填) | 打开项目目录。`name` 收项目名或序号 |
| `mercury.proj.add` | `name`(0，必填) | 添加并置顶项目 |
| `mercury.proj.pin` / `.unpin` | `name`(0，必填) | 置顶 / 取消置顶 |
| `mercury.proj.exclude` / `.include` | `name`(0，必填) | 从项目坞排除 / 重新纳入 |
| `mercury.proj.refresh` | — | 重新扫描活动项目 |

```
mercury.proj.open 2026-021-HistoryMercury
mercury.proj.open name=021
```

### 项目坞

| 指令 | 参数 | 说明 |
| --- | --- | --- |
| `mercury.dock.show` / `.hide` | — | 显隐项目坞 |
| `mercury.dock.policy` | `min` int、`max` int、`halflife` double | 查看或更新坞策略；全省略即查看 |
| `mercury.dock.add` | `command`(0，必填)、`label` | 把任意总线指令登记为常驻项 |
| `mercury.dock.remove` | `command`(0，必填) | 移除常驻项 |
| `mercury.dock.run` | `key`(0，必填) | **内部**。代执行坞条目；跨模块请直接调原指令 |

`mercury.dock.add` 收的是**完整指令文本**，所以任何模块都能把自己的入口挂上去，
不需要 Mercury 认识你的域：

```
mercury.dock.add command="janus.proj.open name=2026-020-HistoryJanus" label=Janus
```

### 快捷文件

| 指令 | 参数 | 说明 |
| --- | --- | --- |
| `mercury.shortcut.open` | `path`(0，必填) | 打开快捷文件、普通文件或目录 |
| `mercury.shortcut.add` | `path`(0，必填) | 把快捷文件登记为坞常驻项 |
| `mercury.shortcut.pick` | — | **内部**。要弹前端选择框；远端请用 `.add path=` |
| `mercury.shortcut.wakeconsole` | — | 兼容入口，转调宿主的唤出控制台 |

### 全局快捷键

| 指令 | 参数 | 说明 |
| --- | --- | --- |
| `mercury.hotkey.list` | — | 列出当前生效的注册。**只读** |
| `mercury.hotkey.register` | `id`(0)、`stroke`(1)、`command`(2) 必填；`owner`、`interval` | 注册「按键序列 → 指令」 |
| `mercury.hotkey.unregister` | `id`(0，必填) | 注销 |

- `id` 重复注册会覆盖旧的。
- `stroke` 逗号分隔多次击键，每次为「修饰键+主键」：`Ctrl+Alt+M`、`Slash,Slash`（连按两次 `/`）、`VK:0xBF`。
- `interval` 是多次击键之间的最大间隔毫秒数，省略为 350。
- `owner` 省略记为 `HistoryMercury`；**别的模块注册时请填自己的模块名**，便于排查抢键。

快捷键走指令而不走 CLR 契约：调用方只要知道指令名和参数名，Mercury 可以随意重构实现。

### 应用与资源管理器入口

| 指令 | 参数 | 说明 |
| --- | --- | --- |
| `mercury.app.status` | — | 查看托管的资源管理器入口状态。**只读** |
| `mercury.app.open` | — | 显示或启动 HistoryVulcan 前端 |
| `mercury.explorer.register` | — | 注册托管入口 |
| `mercury.explorer.remove` | — | 移除托管入口。**确认** |

### 使用历史

| 指令 | 参数 | 说明 |
| --- | --- | --- |
| `mercury.usage.list` | — | 列出项目使用记录。**只读** |
| `mercury.usage.forget` | `name`(0) | 清除使用历史；省略 `name` 清除全部。**确认** |

### 域聚焦

`mercury.go domain=<域>` 把控制台聚焦到某个指令域，省略 `domain` 退出聚焦。
因为 `mercury` 本身是已注册域，聚焦到任何域时都能直接输入它——**各域不必自备「退出聚焦」指令**。

### 页面协议（内部）

`mercury.ui.describe` / `.actions` / `.data` 是模块与界面之间的协议，三条都声明了 `HiddenReason`，
不对远端暴露。装了别的界面模块的宿主可以按同一协议渲染这些页面，不需要引用 HistoryMercury 的任何类型。

## 返回

- 只读指令把结果放 `CommandResult.Data`（结构化行）。
- 页面协议三条同一份 JSON 同时放 `Data` 与 `Message`：`Data` 是 `object?`，跨进程中继后
  结构化载荷不保证存活，消费方约定**优先读 `Data`、回退 `Message`**。
- 其余指令回执正文是一句人话结果。

## 数据与生命周期

运行包安装到 `%APPDATA%\HistoryVulcan\Modules\HistoryMercury` 后**保持不可变**。
受管快捷方式、状态、缓存和 Explorer 注册备份属于用户数据，统一在
`%APPDATA%\HistoryVulcan\HistoryMercury`，不属于发布包，升级不动它。
