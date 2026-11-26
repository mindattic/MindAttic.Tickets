#Requires -Version 5.1
<#
.SYNOPSIS
    MindAttic.Tickets' run menu: 1) Redeploy  2) Run Debug  3) Run Release  (the MindAttic standard, MAT-T279).

.DESCRIPTION
    A thin wrapper over the shared deploy-lib.ps1 (Invoke-AppRun, MAT-T177). run.bat beside it (written by
    deploy-lib.ps1's Write-RunBat) opens the same menu on a double-click.
      1) Redeploy     builds Tickets Desktop to R:\MindAttic\Apps\MindAttic.Tickets\{Debug,Release}
                      Debug   framework-dependent (fast rebuilds, the dev/test copy)
                      Release self-contained single-file win-x64 (no .NET runtime needed)
      2) Run Debug    R:\...\Debug\MindAttic.Tickets.Desktop.exe in this console; extra arguments pass through
      3) Run Release  R:\...\Release\MindAttic.Tickets.Desktop.exe, started the way a double-click would start it
    Picks combine: 13 is Deploy & Run.

    Tickets' own differences, in the override block below:
      - Plan names the Desktop project; the repo's other exes (the tickets CLI, the old Workflow apps) aren't deployed.
      - PostBuild runs the exe with --write-settings-manifest so MindAttic Vault shows this build's settings (LAMP step 7,
        M.TIX-T271). After the Release build it also reinstalls the tickets CLI (tools\install.ps1, unless -SkipCli) and
        points the Desktop and Start-menu shortcuts at this repo's run.bat, with the app's icon.

    Callers from before the shared menu: the old C:\Apps\MindAttic.Tickets.Desktop\run.bat passes -Republish -Launch;
    -Republish means -Redeploy and -Launch means -RunRelease, so it still deploys and opens the app.

.EXAMPLE
    .\run.ps1                    the menu
    .\run.ps1 -Redeploy -SkipCli build and deploy both configs, leave the tickets CLI alone
    .\run.ps1 -RunDebug --ticket MAT-T1
    .\run.ps1 -Redeploy -RunRelease -DryRun
#>
param(
    [switch]$Redeploy,
    [switch]$RunDebug,
    [switch]$RunRelease,
    [switch]$DryRun,
    [switch]$SkipCli,
    [switch]$Republish,
    [switch]$Launch,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$AppArgs = @()
)
$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$installCli = -not $SkipCli
$Override = @{
    Exe       = 'MindAttic.Tickets.Desktop.exe'
    Plan      = @{ Kind = 'dotnet'; Projects = @("$repoRoot\src\MindAttic.Tickets.Desktop\MindAttic.Tickets.Desktop.csproj") }
    PostBuild = {
        param($Config, $Out)
        $exe = Join-Path $Out 'MindAttic.Tickets.Desktop.exe'
        $manifest = Start-Process -FilePath $exe -ArgumentList '--write-settings-manifest' -WorkingDirectory $Out -Wait -PassThru -WindowStyle Hidden
        if ($manifest.ExitCode -ne 0) { Write-Host "  (--write-settings-manifest exited $($manifest.ExitCode); Tickets Desktop writes it on its next start)" -ForegroundColor DarkYellow }
        if ($Config -ne 'Release') { return }
        if ($installCli) {
            Write-Host '  Installing the tickets CLI' -ForegroundColor DarkGray
            & (Join-Path $repoRoot 'tools\install.ps1')
        }
        $shell = New-Object -ComObject WScript.Shell
        foreach ($lnkPath in (Join-Path ([Environment]::GetFolderPath('Desktop')) 'MindAttic Tickets.lnk'),
                             (Join-Path ([Environment]::GetFolderPath('Programs')) 'MindAttic\MindAttic Tickets.lnk')) {
            New-Item -ItemType Directory -Force -Path (Split-Path $lnkPath) | Out-Null
            $lnk = $shell.CreateShortcut($lnkPath)
            $lnk.TargetPath = Join-Path $repoRoot 'run.bat'
            $lnk.WorkingDirectory = $repoRoot
            $lnk.WindowStyle = 1
            $lnk.IconLocation = "$exe,0"
            $lnk.Description = 'Every MindAttic repo''s tickets: queue, approve, reject, reorder.'
            $lnk.Save()
            Write-Host "  Shortcut $lnkPath" -ForegroundColor DarkGray
        }
    }.GetNewClosure()
}

function Find-DeployLib {
    if ($env:MINDATTIC_DEPLOY_LIB -and (Test-Path -LiteralPath $env:MINDATTIC_DEPLOY_LIB)) { return $env:MINDATTIC_DEPLOY_LIB }
    $roots = @()
    $d = $PSScriptRoot
    while ($d) { $roots += $d; $d = Split-Path -Parent $d }
    $roots += 'D:\Projects\MindAttic'
    foreach ($r in $roots) {
        foreach ($rel in 'MindAttic.Agency\shared\deploy-lib.ps1', 'mindattic-agent-standard\shared\deploy-lib.ps1') {
            $p = Join-Path $r $rel
            if (Test-Path -LiteralPath $p) { return $p }
        }
    }
    throw 'deploy-lib.ps1 not found: clone MindAttic.Agency beside this repo, or set MINDATTIC_DEPLOY_LIB to its path.'
}

. (Find-DeployLib)
Invoke-AppRun -Repo $PSScriptRoot -Redeploy:($Redeploy -or $Republish) -RunDebug:$RunDebug -RunRelease:($RunRelease -or $Launch) `
    -DryRun:$DryRun -AppArgs $AppArgs -Override $Override
