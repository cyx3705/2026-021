# HistoryMercury

HistoryMercury is the HistoryVulcan desktop project dock. Its source namespace and command domain are `Mercury`; its module identity and assembly name are `HistoryMercury`.

Commands are registered only through `IModuleContext.RegisterCommands`. The full public set uses `mercury.status`, `mercury.proj.*`, `mercury.explorer.*`, `mercury.dock.*`, `mercury.app.open`, and `mercury.usage.*`. Legacy `dock.*` and `mercury.dock.open` names are intentionally not registered.

## Development

```powershell
dotnet build .\HistoryMercury.csproj -c Release -p:NuGetAudit=false
dotnet run --project .\tests\Smoke\Smoke.csproj -c Release -p:NuGetAudit=false
dotnet format .\HistoryMercury.csproj --verify-no-changes --no-restore
```

The source manifest is `module.manifest.json`. `z-HistoryMercury` is the formal consumer snapshot and is refreshed only by the project publish script.
