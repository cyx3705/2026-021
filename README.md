# HistoryMercury

HistoryMercury owns the HistoryVulcan desktop project dock and Explorer entry for the `HistoryClio` project library.

Current source: `4.6.0`. The dock tile reads `HC`, the Explorer namespace entry is `HistoryClio 项目`, and the project scan prefers `proj.libraryroot` (default `C:\OneHistory\HistoryClio`). Configured `HistoryVesta` roots are rewritten to Clio. 4.5.2 pins the module's host references to the published `z-HistoryVulcan` snapshot and builds the desktop dock before the in-shell extensions, so a failing extension can no longer take the dock off the desktop. 4.5.1 makes the Explorer entry and the managed shortcut folder converge on actual changes: the registration is idempotent, `SHCNE_ASSOCCHANGED` is reserved for namespace-entry changes, shortcut files are written only when a managed field differs, and both processes follow `state.json` so neither reads the other's writes as user intent.

The module is developed in `b-Code-MercuryDock`. Its code namespace and command domain are `Mercury`; its module identity, assembly and consumer snapshot are `HistoryMercury`, `HistoryMercury.dll` and `z-HistoryMercury`.

## Commands

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code\Test-ProjectContract.ps1 -Instantiation
dotnet run --project .\b-Code-MercuryDock\tests\Smoke\Smoke.csproj -c Release -p:NuGetAudit=false
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code\Build-HistoryMercuryPackage.ps1
```

The final command creates and verifies a candidate package in `z-Publish/current`. Formal publication to `z-HistoryMercury`
and the Diana consumer-document mirror is owned by `2026-019-HistoryDiana/b-Code/Publish-OneHistoryModule.ps1`.
