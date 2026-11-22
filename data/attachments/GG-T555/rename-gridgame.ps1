# GG-T555: rename the local Unity.GridGame folder to GridGame.Unity, then re-key everything that names it.
# GitHub is already renamed (mindattic/GridGame.Unity) and the checkout's origin already points there.
#
# BEFORE running: close every CLI / Orchestrator tab working in Unity.GridGame, the Unity editor, any running
# GridGameGaiden player, Thumbnail Studio and VS/Rider on GridGameGaiden.slnx. Then, in a normal PowerShell:
#
#   powershell -ExecutionPolicy Bypass -File D:\Projects\MindAttic\MindAttic.Tickets\data\attachments\GG-T555\rename-gridgame.ps1
#
# Safe to rerun: each step skips itself when already done.
$ErrorActionPreference = 'Stop'
$root = 'D:\Projects\MindAttic'
$old = 'Unity.GridGame'; $new = 'GridGame.Unity'
$oldPath = Join-Path $root $old; $newPath = Join-Path $root $new

# 1. The folder.
if (Test-Path $newPath) {
    Write-Host "[1] $newPath already exists - folder step done."
} else {
    try { Rename-Item -LiteralPath $oldPath -NewName $new }
    catch {
        Write-Host "[1] Could not rename the folder: something still has a file open in it." -ForegroundColor Red
        Write-Host "    Close the CLIs, Unity, the game, Thumbnail Studio and IDEs working in $oldPath, then rerun."
        Write-Host "    ($($_.Exception.Message))"
        exit 1
    }
    Write-Host "[1] Renamed $oldPath -> $newPath"
}

# 2. The ticket database (its repo key; ticket IDs keep the GG- code).
$renamed = (& tickets repos renames --repo $new 2>&1 | Out-String) -match [regex]::Escape($old)
if ($renamed) { Write-Host "[2] Ticket database already re-keyed." }
else { & tickets repos rename $old $new; Write-Host "[2] Ticket database re-keyed $old -> $new" }

# 3. MindAttic.Launcher settings (name, repo, repoUrl, path, colour scheme). Backed up first.
$ls = Join-Path $env:APPDATA 'MindAttic\MindAttic.Launcher\settings.json'
$t = [IO.File]::ReadAllText($ls)
if ($t -match [regex]::Escape("`"$old`"") -or $t -match [regex]::Escape("\\$old")) {
    Copy-Item $ls "$ls.bak-GG-T555" -Force
    $t = $t.Replace("`"name`": `"$old`"", "`"name`": `"$new`"").Replace("`"repo`": `"$old`"", "`"repo`": `"$new`"")
    $t = $t.Replace("github.com/mindattic/$old.git", "github.com/mindattic/$new.git").Replace("MindAttic\\$old`"", "MindAttic\\$new`"")
    $t = $t.Replace("MindAttic-$old", "MindAttic-$new")
    [IO.File]::WriteAllText($ls, $t, (New-Object Text.UTF8Encoding($false)))
    Write-Host "[3] Launcher settings updated (backup: $ls.bak-GG-T555)"
} else { Write-Host "[3] Launcher settings already updated." }

# 4. Windows Terminal colour scheme name the Launcher entry points at.
$wt = Get-ChildItem "$env:LOCALAPPDATA\Packages\Microsoft.WindowsTerminal_*\LocalState\settings.json" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($wt) {
    $w = [IO.File]::ReadAllText($wt.FullName)
    if ($w.Contains("MindAttic-$old")) {
        Copy-Item $wt.FullName "$($wt.FullName).bak-GG-T555" -Force
        [IO.File]::WriteAllText($wt.FullName, $w.Replace("MindAttic-$old", "MindAttic-$new"), (New-Object Text.UTF8Encoding($false)))
        Write-Host "[4] Windows Terminal scheme renamed MindAttic-$new"
    } else { Write-Host "[4] Windows Terminal scheme already renamed." }
}

Write-Host ""
Write-Host "Done. Restart the Launcher and the Orchestrator so they pick it up (GridGame.Unity and GridGame.Studio" -ForegroundColor Green
Write-Host "then nest under GridGame). Open a CLI in $newPath and say: finish GG-T555" -ForegroundColor Green
Write-Host "(it updates the paths inside the repos: the nightly job, scripts and docs, the Emulator Suite app entry, memory)."
