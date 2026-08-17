# HistoryMercury Tests

This root owns executable verification projects and is separate from production module source.

- `HistoryMercury.Smoke/`: module identity, command, UI lifecycle, Explorer, shortcut, state, and dock regressions.

Run from the repository root:

```powershell
dotnet run --project .\b-Code-Tests\HistoryMercury.Smoke\HistoryMercury.Smoke.csproj -c Release -p:NuGetAudit=false
```
