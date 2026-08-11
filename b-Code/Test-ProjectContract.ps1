[CmdletBinding()]
param(
    [switch]$Instantiation
)

# HistoryMercury 的合同校验入口。
#
# 规则本体不在这里。体系内所有模块共用 HistoryDiana 持有的那一份
# （b-Code/OneHistory.ModuleContract.ps1），本文件只负责定位并转发。
#
# 本文件此前是那份脚本的逐字副本。副本一旦存在就会以「本项目有点特殊」的名义漂移，
# 因此这里不保留任何规则、也不提供扩展点：需要项目差异时改 project.manifest.json
# 的 contract 节；无法用清单表达的差异，说明它要么该进内核，要么该被消除。

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dianaEntry = [IO.Path]::GetFullPath(
    (Join-Path $projectRoot '..\2026-019-HistoryDiana\b-Code\OneHistory.ModuleContract.ps1'))

if (-not (Test-Path -LiteralPath $dianaEntry -PathType Leaf)) {
    Write-Host "Shared module contract is missing: $dianaEntry" -ForegroundColor Red
    Write-Host 'Expected HistoryDiana to be checked out alongside this project.' -ForegroundColor Red
    exit 2
}

& $dianaEntry -ProjectRoot $projectRoot -Instantiation:$Instantiation
exit $LASTEXITCODE
