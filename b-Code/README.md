# HistoryMercury Build Tools

`Publish-MercuryModules.ps1` builds HistoryMercury and creates a hash-verified candidate package in `b-Publish/current/HistoryMercury`.

The script reads the authoritative source manifest at `b-Code-MercuryDock/module.manifest.json`. It never treats the formal `z-HistoryMercury` snapshot as a source of version metadata. Use `-Publish` only for an explicitly approved formal release.

`Test-ProjectContract.ps1` validates project identity, manifest paths, current documents, and local Markdown links. Run it with `-Instantiation` for this non-template project.
