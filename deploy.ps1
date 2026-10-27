<#
.SYNOPSIS
  Builds Tickets Desktop as one self-contained exe, installs it to C:\Apps\MindAttic.Tickets.Desktop, and (re)creates
  its Desktop and Start-menu shortcuts with the app's icon. Also installs the tickets CLI (tools/install.ps1).
  It builds the latest checked-in version, not the working tree: it fetches origin, takes the newer of main and
  origin/main (origin/main when they've diverged), and builds a clean, temporary detached worktree of that commit. Work
  landed and pushed from a worktree is deployed even when this checkout hasn't pulled it; uncommitted edits never are.
  The shortcuts open the install folder's deploy.bat, which reruns this script with -Launch -SkipCli: every open
  republishes when the deployed build inputs differ from that commit's, and otherwise just opens it (M.TIX-T126).
.PARAMETER Force
  Publish even when the installed exe was already built from the same build inputs.
.PARAMETER Launch
  Open Tickets Desktop when done.
.PARAMETER SkipCli
  Don't reinstall the tickets CLI global tool.
#>
param([switch]$Launch, [switch]$SkipCli, [switch]$Force)
$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$name = 'MindAttic.Tickets.Desktop'
$apps = if (Test-Path 'C:\Apps') { 'C:\Apps' } else { Join-Path $env:LOCALAPPDATA 'Programs' }
$dest = Join-Path $apps $name
$exe = Join-Path $dest "$name.exe"

# Windows PowerShell turns a native command's stderr into a terminating error under 'Stop', so git runs under 'Continue'.
function Invoke-Git { $ErrorActionPreference = 'Continue'; $out = & git -C $repo @args 2>$null; if ($LASTEXITCODE -ne 0) { return $null }; $out }
function Test-Ancestor($a, $b) { $ErrorActionPreference = 'Continue'; & git -C $repo merge-base --is-ancestor $a $b 2>$null; $LASTEXITCODE -eq 0 }

# The latest checked-in version: the newer of main and origin/main. Agents land and push from worktrees, so origin/main
# is often ahead of this checkout; when the two have diverged, origin/main (what's pushed) wins.
$null = Invoke-Git fetch origin --quiet
if ($LASTEXITCODE -ne 0) { Write-Host 'git fetch failed; deploying from the last fetched origin/main.' }
$local = Invoke-Git rev-parse --verify --quiet 'refs/heads/main'
$remote = Invoke-Git rev-parse --verify --quiet 'refs/remotes/origin/main'
if (-not $remote) { $commit = $local }
elseif (-not $local) { $commit = $remote }
else {
  if (Test-Ancestor $local $remote) { $commit = $remote }
  else {
    if (Test-Ancestor $remote $local) { $commit = $local }
    else {
      $commit = $remote
      $unpushed = Invoke-Git log --oneline "$remote..$local" -- src assets Directory.Build.props global.json NuGet.config
      if ($unpushed) { Write-Warning "main has unpushed build changes that origin/main lacks; they aren't deployed:`n$($unpushed -join "`n")" }
    }
  }
}
if (-not $commit) { throw 'No main or origin/main commit to deploy.' }
$short = $commit.Substring(0, 9)

# What the exe is built from: the git tree ids of its build inputs at that commit. A ticket-database sync changes the
# commit but none of these, so it doesn't republish.
$buildInputs = "src/$name", 'src/MindAttic.Tickets', 'assets', 'Directory.Build.props', 'global.json', 'NuGet.config'
$buildKey = ($buildInputs | ForEach-Object { "$_=$(Invoke-Git rev-parse --verify --quiet "${commit}:$_")" }) -join ';'
$stamp = Join-Path $dest 'deployed-build.txt'
$deployedKey = if (Test-Path $stamp) { (Get-Content $stamp -TotalCount 1) } else { $null }
$stale = $Force -or -not (Test-Path $exe) -or $deployedKey -ne $buildKey

# A clean copy of that commit to build from, so uncommitted edits and stale bin/obj never reach the install.
$snapshot = $null
function Get-Snapshot {
  if ($script:snapshot) { return $script:snapshot }
  $dir = Join-Path ([IO.Path]::GetTempPath()) "MindAttic.Tickets.deploy\$short"
  # A detached worktree, not a `git archive` copy: the tests find the repo root by its .git.
  if (Test-Path $dir) { $null = Invoke-Git worktree remove --force $dir; if (Test-Path $dir) { Remove-Item $dir -Recurse -Force } }
  $null = Invoke-Git worktree prune
  $null = Invoke-Git worktree add --detach $dir $commit
  if ($LASTEXITCODE -ne 0) { throw "git worktree add $short failed ($LASTEXITCODE)" }
  $script:snapshot = $dir
  $dir
}

try {
if ($stale) {
  $src = Get-Snapshot
  Get-Process $name -ErrorAction SilentlyContinue | Stop-Process -Force
  Start-Sleep -Milliseconds 500

  Write-Host "Publishing $name at $short to $dest"
  dotnet publish (Join-Path $src "src\$name\$name.csproj") -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -o $dest --nologo -v quiet
  if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
  Set-Content -Path $stamp -Encoding ascii -Value $buildKey, "commit=$commit"
} else {
  Write-Host "$name is up to date ($short)."
}

# The shortcuts' launcher: reruns this script from the repo, which republishes only when the source is newer, then opens
# the app. -SkipCli: reinstalling the global tool on every open is slow, and every agent's tickets call would wait on it.
$launcher = Join-Path $dest 'deploy.bat'
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Set-Content -Path $launcher -Encoding ascii -Value @"
@echo off
title MindAttic Tickets
rem Generated by $repo\deploy.ps1 - edits here are overwritten.
set "DEPLOY_PS1=$repo\deploy.ps1"
if not exist "%DEPLOY_PS1%" (
    start "" "%~dp0$name.exe"
    exit /b 0
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%DEPLOY_PS1%" -Launch -SkipCli %*
if errorlevel 1 (
    echo Redeploy failed - opening the installed copy.
    start "" "%~dp0$name.exe"
    pause
)
"@

function New-Shortcut([string]$path) {
  New-Item -ItemType Directory -Force -Path (Split-Path $path) | Out-Null
  $shell = New-Object -ComObject WScript.Shell
  $lnk = $shell.CreateShortcut($path)
  $lnk.TargetPath = $launcher
  $lnk.WorkingDirectory = $dest
  $lnk.WindowStyle = 7
  $lnk.IconLocation = "$exe,0"
  $lnk.Description = 'Every MindAttic repo''s tickets: queue, approve, reject, reorder.'
  $lnk.Save()
  Write-Host "Shortcut $path"
}
# Before the rename (M.TIX-T120) the app was MindAttic.Workflow.Desktop: its shortcuts and install folder go.
Get-Process 'MindAttic.Workflow.Desktop' -ErrorAction SilentlyContinue | Stop-Process -Force
foreach ($old in (Join-Path ([Environment]::GetFolderPath('Desktop')) 'MindAttic Workflow.lnk'),
                 (Join-Path ([Environment]::GetFolderPath('Programs')) 'MindAttic\MindAttic Workflow.lnk'),
                 (Join-Path $apps 'MindAttic.Workflow.Desktop')) {
  if (Test-Path $old) { Remove-Item $old -Recurse -Force; Write-Host "Removed $old" }
}
New-Shortcut (Join-Path ([Environment]::GetFolderPath('Desktop')) 'MindAttic Tickets.lnk')
New-Shortcut (Join-Path ([Environment]::GetFolderPath('Programs')) 'MindAttic\MindAttic Tickets.lnk')

if (-not $SkipCli) {
  $install = Join-Path (Get-Snapshot) 'tools\install.ps1'
  if (Test-Path $install) {
    Write-Host "Installing the tickets CLI at $short"
    & $install
  }
}
} finally {
  if ($snapshot) {
    $null = Invoke-Git worktree remove --force $snapshot
    if (Test-Path $snapshot) { Remove-Item $snapshot -Recurse -Force -ErrorAction SilentlyContinue; $null = Invoke-Git worktree prune }
  }
}

Write-Host "Installed $exe"
if ($Launch) { Start-Process $exe }

# Deploying Tickets also deploys Orchestrator (its deploy.ps1 does the same back here).
# MINDATTIC_DEPLOY_CHAIN stops Tickets -> Orchestrator -> Tickets from looping.
if (-not $env:MINDATTIC_DEPLOY_CHAIN) {
  $orchDeploy = Join-Path $repo '..\MindAttic.Orchestrator\deploy.ps1'
  if (Test-Path $orchDeploy) {
    Write-Host "Deploying MindAttic.Orchestrator too ($orchDeploy)..."
    $chainArgs = @{}
    if ($Force) { $chainArgs['Force'] = $true }
    $env:MINDATTIC_DEPLOY_CHAIN = '1'
    try {
      & $orchDeploy @chainArgs
      if ($LASTEXITCODE -ne 0) { throw "MindAttic.Orchestrator deploy failed (exit code $LASTEXITCODE)" }
      Write-Host "MindAttic.Orchestrator deployed"
    } finally {
      Remove-Item Env:\MINDATTIC_DEPLOY_CHAIN -ErrorAction SilentlyContinue
    }
  } else {
    Write-Host "MindAttic.Orchestrator checkout not found at $orchDeploy; skipping its deploy."
  }
}
