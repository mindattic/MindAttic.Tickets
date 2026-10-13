<#
.SYNOPSIS
  Build and run one console-menu spike, or list them.
.EXAMPLE
  ./spikes/run.ps1              # list spikes in suggested order
  ./spikes/run.ps1 FlickerFree  # build + run one
  ./spikes/run.ps1 -All         # run each in turn; quit one to start the next
#>
param(
    [Parameter(Position = 0)][string]$Name,
    [switch]$All
)

$ErrorActionPreference = 'Stop'
$order = 'Baseline', 'FlickerFree', 'SpectreLive', 'RazorConsole', 'XenoAtom', 'TerminalGui'

function Invoke-Spike([string]$spike) {
    $proj = Join-Path $PSScriptRoot "Spike.$spike"
    if (-not (Test-Path $proj)) { throw "No spike named '$spike'. Choose one of: $($order -join ', ')" }
    Write-Host "`n=== Spike.$spike ===  (build...)" -ForegroundColor Cyan
    dotnet build $proj -v q -nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Build failed for Spike.$spike" }
    dotnet run --project $proj --no-build
}

if ($All) { foreach ($s in $order) { Invoke-Spike $s } ; return }
if ($Name) { Invoke-Spike $Name; return }

Write-Host 'Spikes (suggested order):'
$i = 1
foreach ($s in $order) { Write-Host ("  {0}. {1}" -f $i++, $s) }
Write-Host "`nRun one:  ./spikes/run.ps1 <Name>     Run all in turn:  ./spikes/run.ps1 -All"
