# ActiveDock AppShell 新宿主适配

ActiveDock 继续保留 OHS 模块槽兼容性，同时加入 AppShell 独立宿主的标准模块目录。

AppShell 独立宿主的模块位置为：

`%APPDATA%/AppShell/Modules/ActiveDock/`

其中包含 `ActiveDock.dll`、`ActiveDock.xml` 和 `module.manifest.json`。AppShell 启动时
显式启用 `ModuleHost` 与 UI 模块宿主，并扫描应用数据目录下的标准模块槽。其他 AppShell
消费方按自身应用名使用 `%AppData%/<应用名>/Modules`；只有明确配置
`ShellConfig.ModuleDirectory` 时才使用自定义目录。

ActiveDock 不复制 AppShell 内部代码，也不改变 `dock` 命令域、工具窗口 ID 或资源管理器
快捷方式双向同步规则。当前模块仍使用冻结的 `AppShell.Core` 3.0.3 程序集合同，和新宿主
3.1.x 的程序集兼容版本一致。
