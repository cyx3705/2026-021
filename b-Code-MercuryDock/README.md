# HistoryMercury

HistoryMercury is the HistoryVulcan desktop project dock. Its source namespace and command domain are `Mercury`; its module identity and assembly name are `HistoryMercury`.

Commands are registered only through `IModuleContext.RegisterCommands`. The full public set uses `mercury.status`, `mercury.proj.*`, `mercury.explorer.*`, `mercury.dock.*`, `mercury.app.open`, and `mercury.usage.*`. Legacy `dock.*` and `mercury.dock.open` names are intentionally not registered.

## Development

```powershell
dotnet build .\b-Code-MercuryDock\HistoryMercury.csproj -c Release -p:NuGetAudit=false
dotnet run --project .\b-Code-Tests\HistoryMercury.Smoke\HistoryMercury.Smoke.csproj -c Release -p:NuGetAudit=false
dotnet format .\b-Code-MercuryDock\HistoryMercury.csproj --verify-no-changes --no-restore
```

The source manifest is `module.manifest.json`. `z-Publish` is the formal consumer snapshot and is refreshed only by the centralized Diana publisher.

## Source layout

- `Module/`: module identity and lifecycle composition.
- `Commands/`: command registration, handlers, and host launch fallback.
- `Dock/`: desktop dock window, layout, policy, theme, weighting, and icon behavior.
- `Explorer/`: managed shortcut folder and Explorer namespace integration.
- `State/`: data roots, project discovery, persisted state, and recent-folder input.
- `Views/`: manager page and reusable view controls.
- `CommandSurface/`, `Input/`, `Properties/`: command workbench, global input, and assembly metadata.

Smoke tests live in `../b-Code-Tests/HistoryMercury.Smoke`; historical version documents live directly in
`../b-Office/history`. Production source directories do not own tests or project history.
