# HistoryMercury

HistoryMercury owns the HistoryVulcan desktop project dock and Explorer entry for the `HistoryVesta` project library.

The module is developed in `b-Code-MercuryDock`. Its code namespace and command domain are `Mercury`; its module identity, assembly and consumer snapshot are `HistoryMercury`, `HistoryMercury.dll` and `z-HistoryMercury`.

## Commands

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code\Test-ProjectContract.ps1 -Instantiation
dotnet run --project .\b-Code-MercuryDock\tests\Smoke\Smoke.csproj -c Release -p:NuGetAudit=false
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code\Build-HistoryMercuryPackage.ps1
```

The final command creates and verifies a candidate package in `b-Publish/current`. Formal publication to `z-HistoryMercury`
and the Diana consumer-document mirror is owned by `2026-019-HistoryDiana/b-Code/Publish-OneHistoryModule.ps1`.
