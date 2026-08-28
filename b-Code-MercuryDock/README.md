# HistoryMercury

HistoryMercury is the HistoryVulcan desktop project dock. Its source namespace and command domain are `Mercury`; its module identity and assembly name are `HistoryMercury`.

Commands are registered only through `IModuleContext.RegisterCommands`. The full public set uses `mercury.go`, `mercury.app.*`, `mercury.proj.*`, `mercury.explorer.*`, `mercury.dock.*`, `mercury.shortcut.*`, `mercury.usage.*`, `mercury.hotkey.*`, and the three in-frontend `mercury.ui.*`. Legacy `dock.*` and `mercury.dock.open` names are intentionally not registered.

From 5.0.0 the module builds no frontend control: pages are declared as data for HistoryAurora to render. The desktop dock is not a page — it is Mercury's own window and is code-built.

## Development

```powershell
dotnet build .\b-Code-MercuryDock\HistoryMercury.csproj -c Release -p:NuGetAudit=false
dotnet run --project .\b-Code-Tests\HistoryMercury.Smoke\HistoryMercury.Smoke.csproj -c Release -p:NuGetAudit=false
dotnet format .\b-Code-MercuryDock\HistoryMercury.csproj --verify-no-changes --no-restore
```

The source manifest is `module.manifest.json`. `z-Publish` is the formal consumer snapshot and is refreshed only by the host release pipeline.

## Source layout

- `Module/`: module identity and lifecycle composition.
- `Commands/`: command registration, handlers, and host launch fallback.
- `Dock/`: desktop dock window, layout, policy, theme, weighting, and icon behavior.
- `Explorer/`: managed shortcut folder and Explorer namespace integration.
- `State/`: data roots, project discovery, persisted state, and recent-folder input.
- `Ui/`: page descriptions, action declarations, and page data — JSON only, no controls.
- `Input/`, `Diagnostics/`, `Properties/`: global input, the module-owned log, and assembly metadata.

Smoke tests live in `../b-Code-Tests/HistoryMercury.Smoke`; historical version documents live directly in
`../b-Office/history`. Production source directories do not own tests or project history.
