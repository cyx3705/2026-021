# OneHistory AI-Ready 项目模板

这是从 `0000-000-Template` 派生的 AI 优先项目模板。它保留 OneHistory 的多专业工程素材，
同时用稳定入口、结构化清单、现行文档和可执行验证规则，让人和 AI 都能快速判断项目目标、
真值、修改边界与完成标准。

![OneHistory Logo](./Logo.png)

## 模板原则

- 根目录只提供必要入口，不要求 AI 扫描整个仓库。
- `project.manifest.json` 声明活动目录、文档、命令、归档和生成物。
- 根目录的可见文件夹只使用 `a-*`、`b-*`、`z-*` 三种级别。
- `b-Office/current/` 只保存必要的项目现行文档；`b-Office/package/` 保存精简复用入口。
- `b-Office/history/` 默认不进入 AI 上下文。
- 任何构建、测试或验收命令都必须在 manifest 中明确声明；不适用时使用 `null`。

## 入口

| 入口 | 用途 |
| --- | --- |
| [`AGENTS.md`](./AGENTS.md) | AI 读取顺序、真值规则、工作边界与完成要求 |
| [`project.manifest.json`](./project.manifest.json) | 可机器读取的项目身份、路径、命令和上下文排除项 |
| [`b-Office/package/复用说明.md`](./b-Office/package/复用说明.md) | 文档包边界与建议读取顺序 |
| [`b-Office/文档中心.md`](./b-Office/文档中心.md#目录规范) | 文档索引及根目录 a/b/z 规范 |
| [`b-Office/current/项目概览.md`](./b-Office/current/项目概览.md) | 项目目标、范围、状态和交付物 |
| [`b-Office/current/技术合同.md`](./b-Office/current/技术合同.md) | 现行需求和系统架构 |
| [`b-Office/current/有效决策.md`](./b-Office/current/有效决策.md) | 当前仍然有效的关键决策 |
| [`b-Office/current/验证合同.md`](./b-Office/current/验证合同.md) | 分层验证方法与证据要求 |

## 从模板建立项目

1. 以本分支建立新的项目分支和工作树，不直接修改本模板。
2. 修改 `project.manifest.json` 中的项目身份、类型、活动目录和命令，并将
   `template.isTemplate` 改为 `false`。
3. 按项目需要创建 `a-*` 子项目、`b-*` 项目组件或 `z-*` 跨项目复用元目录，使用能表达
   职责的名称，并登记到 manifest。
4. 替换 `b-Office/current/` 中全部 `{{...}}` 占位内容，删除不适用的小节。
5. 更新根 README，使其描述真实项目，而不是模板。
6. 执行严格验收：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code\Test-ProjectContract.ps1 -Instantiation
```

严格验收通过只代表项目入口和文档合同完整；产品本身仍须执行 manifest 中声明的构建、
测试和验收命令。

## 模板验证

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code\Test-ProjectContract.ps1
```

作者：Pinavia
