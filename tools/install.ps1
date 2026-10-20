<#
.SYNOPSIS
    Packs MindAttic.Tickets (library) and MindAttic.Tickets.Cli (the `tickets` global tool) into
    C:\LocalNuGet, then installs or updates the tool so `tickets` is on PATH.
.DESCRIPTION
    The version comes from the csproj (HOUSE-LAW-1: whole numbers, major-only bumps). Packing the
    same version again overwrites the local package, so the tool is updated in place.
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\install.ps1
#>
$ErrorActionPreference = 'Stop'
$repo   = Resolve-Path (Join-Path $PSScriptRoot '..')
$feed   = 'C:\LocalNuGet'

dotnet test (Join-Path $repo 'MindAttic.Tickets.slnx') --nologo
if ($LASTEXITCODE -ne 0) { throw 'Tests failed; not packing.' }

foreach ($proj in 'src\MindAttic.Tickets\MindAttic.Tickets.csproj', 'src\MindAttic.Tickets.Cli\MindAttic.Tickets.Cli.csproj') {
    dotnet pack (Join-Path $repo $proj) --configuration Release --output $feed --nologo
    if ($LASTEXITCODE -ne 0) { throw "Pack failed: $proj" }
}

# A same-version repack isn't picked up from the NuGet cache, so drop the cached copies first
# (the library's too, so consumers like the Launcher restore the new build).
foreach ($id in 'mindattic.tickets.cli', 'mindattic.tickets') {
    $cache = Join-Path $env:USERPROFILE ".nuget\packages\$id"
    if (Test-Path $cache) { Remove-Item $cache -Recurse -Force }
}

# `tool update` skips a same-version repack ("already installed"), so reinstall instead. The tool was
# MindAttic.Workflow.Cli (command `workflow`) before the rename (M.TIX-T120); that one goes too.
$tools = dotnet tool list --global
if ($tools | Select-String -Pattern '^mindattic\.workflow\.cli\s') { dotnet tool uninstall --global MindAttic.Workflow.Cli | Out-Null }
if ($tools | Select-String -Pattern '^mindattic\.tickets\.cli\s') { dotnet tool uninstall --global MindAttic.Tickets.Cli | Out-Null }
dotnet tool install --global MindAttic.Tickets.Cli --add-source $feed
if ($LASTEXITCODE -ne 0) { throw 'Tool install/update failed.' }

# Temporary: a `workflow` shim beside the tool forwards to `tickets`, so callers not yet moved to the new name keep
# working (workflow.cmd for PowerShell and cmd, an extensionless script for Git Bash and the commit-msg hook).
$toolDir = Join-Path $env:USERPROFILE '.dotnet	ools'
Set-Content -Path (Join-Path $toolDir 'workflow.cmd') -Encoding ascii -Value "@echo off`r`nrem Temporary shim: MindAttic.Workflow was renamed MindAttic.Tickets (M.TIX-T120). Use 'tickets'.`r`ntickets %*"
[IO.File]::WriteAllText((Join-Path $toolDir 'workflow'), "#!/bin/sh`n# Temporary shim: MindAttic.Workflow was renamed MindAttic.Tickets (M.TIX-T120). Use 'tickets'.`nexec tickets `"`$@`"`n")

Write-Host "Installed: $(Get-Command tickets -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source) (and the temporary workflow shim)"
