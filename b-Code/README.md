# HistoryMercury Build Tools

`Build-HistoryMercuryPackage.ps1` builds HistoryMercury and creates a hash-verified candidate package in `z-Publish`.

The script reads the authoritative source manifest at `b-Code-MercuryDock/module.manifest.json`. It never treats the formal `z-Publish` snapshot as a source of version metadata.

Formal promotion and project-contract validation both live in the host's in-process pipeline from HistoryVulcan 5.0
(`vulcan.dev.submit` / `finish`; since host 5.8.0 the publish descriptor lives in this repo's `project.manifest.json` → `publish`).
`Test-ProjectContract.ps1` was removed with the shared Diana script it forwarded to.
