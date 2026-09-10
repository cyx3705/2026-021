# HistoryMercury

HistoryMercury owns the HistoryVulcan desktop project dock and Explorer entry for the `HistoryClio` project library.

Current source: `5.0.2`, built against host **HistoryVulcan 5.1.0** and rendered by **HistoryAurora 1.9.2**
(page-registration protocol V1; panel protocol V3 is a hard requirement). The dock tile reads `HC`, the Explorer namespace entry is `HistoryClio 项目`, and the project scan prefers
`proj.libraryroot` (default `C:\OneHistory\HistoryClio`). Configured `HistoryVesta` roots are rewritten to Clio.
Mutable shortcut files, state and logs live under `%APPDATA%\HistoryVulcan\HistoryMercury`, outside the
manifest-verified runtime module package.

5.0.0 follows the host's 5.0 removal of the module UI SDK. The module entry is `MercuryModule`
(`IModuleContextAware` + `IDisposable`) and it constructs **no frontend control**: its two pages — the dock manager and
the command set — are declared as data through `mercury.ui.describe` / `mercury.ui.actions` / `mercury.ui.data` and
rendered by HistoryAurora. The desktop dock is unaffected: it is Mercury's own window, code-built on its own STA
thread, and it stays on screen whether or not the frontend is running. The command workbench (catalog session,
completion, detail page) left with the host mount point it was attached to; `mercury.go` now relays
`aurora.log.source`.

5.0.1 brings the page description back in line with the frontend. It had been written against Aurora 1.7, and
Aurora has since retired page-level `button` / `input` / `select` (1.8.14), stopped scrolling tool pages (1.9.0) and
booked table `view` options as a no-op declaration (1.9.0). The visible result was that the dock manager's whole row
of actions rendered as retirement notices — the buttons were still there and still did nothing, with no error on
either side. Row actions now go through the table's `rowActions` (inline buttons and the right-click menu from one
declaration), the retention policy and refresh collapse into a single horizontal toolbar, and "加入扩展坞" moved off
the page entirely into a right-click popup. That last one needed a capability Aurora did not have; it was added to
Aurora's public component area as `popup.trigger` (1.9.1 / REQ-UI-056) by giving the existing flyout a second way to
open, not by building a second flyout. The dock's pinned marker also changed from a `·` after the project number to a
bright yellow ring on the tile.

5.0.2 finishes that move and ships it. The 5.0.1 source had already been rewritten for Aurora 1.9.2's panel protocol
V3 (`widgets` → `rows`) but was never published, so the running module still declared flat `widgets` and both panels —
the toolbar at the top of the dock manager and the right-click "加入扩展坞" flyout — rendered as empty boards.
On top of that the dock manager page is trimmed: the caption line is gone, so is the `lastopened` column, and every
row action now declares `inline: false`, which removes the trailing 「操作」 column Aurora adds for in-row buttons.
The page does not scroll, so each line and column removed is a table row or a column width the entries table gets
back. The trade-off is that nothing on the page advertises the right-click: adding an entry and acting on a row are
both right-click only.

The module is developed in `b-Code-MercuryDock`. Its code namespace and command domain are `Mercury`; its module identity, assembly and consumer snapshot are `HistoryMercury`, `HistoryMercury.dll` and `z-Publish/HistoryMercury-vX.Y.Z`.

## Commands

```powershell
dotnet run --project .\b-Code-Tests\HistoryMercury.Smoke\HistoryMercury.Smoke.csproj -c Release -p:NuGetAudit=false
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code\Build-HistoryMercuryPackage.ps1
```

The final command creates and verifies a candidate package in `z-Publish/HistoryMercury-vX.Y.Z`. Formal publication is owned by the host development pipeline (`HistoryVulcan.Cli.exe --cli vulcan.dev.submit name=HistoryMercury`). Diana does not publish.
