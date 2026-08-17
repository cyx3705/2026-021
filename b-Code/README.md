# HistoryMercury Build Tools

`Build-HistoryMercuryPackage.ps1` builds HistoryMercury and creates a hash-verified candidate package in `z-Publish`.

The script reads the authoritative source manifest at `b-Code-MercuryDock/module.manifest.json`. It never treats the formal `z-Publish` snapshot as a source of version metadata. Formal promotion is performed only by Diana.

`Test-ProjectContract.ps1` validates project identity, manifest paths, current documents, and local Markdown links. Run it with `-Instantiation` for this non-template project.
