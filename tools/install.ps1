<#
.SYNOPSIS
    Packs MindAttic.Workflow (library) and MindAttic.Workflow.Cli (the `workflow` global tool) into
    C:\LocalNuGet, then installs or updates the tool so `workflow` is on PATH.
.DESCRIPTION
    The version comes from the csproj (HOUSE-LAW-1: whole numbers, major-only bumps). Packing the
    same version again overwrites the local package, so the tool is updated in place.
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\install.ps1
#>
$ErrorActionPreference = 'Stop'
$repo   = Resolve-Path (Join-Path $PSScriptRoot '..')
$feed   = 'C:\LocalNuGet'

dotnet test (Join-Path $repo 'MindAttic.Workflow.slnx') --nologo
if ($LASTEXITCODE -ne 0) { throw 'Tests failed; not packing.' }

foreach ($proj in 'src\MindAttic.Workflow\MindAttic.Workflow.csproj', 'src\MindAttic.Workflow.Cli\MindAttic.Workflow.Cli.csproj') {
    dotnet pack (Join-Path $repo $proj) --configuration Release --output $feed --nologo
    if ($LASTEXITCODE -ne 0) { throw "Pack failed: $proj" }
}

# A same-version repack isn't picked up from the NuGet cache, so drop the cached copies first
# (the library's too, so consumers like the Launcher restore the new build).
foreach ($id in 'mindattic.workflow.cli', 'mindattic.workflow') {
    $cache = Join-Path $env:USERPROFILE ".nuget\packages\$id"
    if (Test-Path $cache) { Remove-Item $cache -Recurse -Force }
}

$installed = dotnet tool list --global | Select-String -Pattern '^mindattic\.workflow\.cli\s'
if ($installed) { dotnet tool update --global MindAttic.Workflow.Cli --add-source $feed }
else            { dotnet tool install --global MindAttic.Workflow.Cli --add-source $feed }
if ($LASTEXITCODE -ne 0) { throw 'Tool install/update failed.' }

Write-Host "Installed: $(Get-Command workflow -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source)"
