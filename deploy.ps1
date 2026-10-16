<#
.SYNOPSIS
  Builds Workflow Desktop as one self-contained exe, installs it to C:\Apps\MindAttic.Workflow.Desktop, and (re)creates
  its Desktop and Start-menu shortcuts with the app's icon. Also installs the workflow CLI (tools/install.ps1).
.PARAMETER Launch
  Open Workflow Desktop when done.
.PARAMETER SkipCli
  Don't reinstall the workflow CLI global tool.
#>
param([switch]$Launch, [switch]$SkipCli)
$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$name = 'MindAttic.Workflow.Desktop'
$apps = if (Test-Path 'C:\Apps') { 'C:\Apps' } else { Join-Path $env:LOCALAPPDATA 'Programs' }
$dest = Join-Path $apps $name
$exe = Join-Path $dest "$name.exe"

Get-Process $name -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

Write-Host "Publishing $name to $dest"
dotnet publish (Join-Path $repo "src\$name\$name.csproj") -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
  -p:DebugType=None -o $dest --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# A one-click redeploy from the install folder: it runs this script from the repo.
Set-Content -Path (Join-Path $dest 'deploy.bat') -Encoding ascii -Value @"
@echo off
rem Rebuilds and reinstalls Workflow Desktop from $repo
powershell -NoProfile -ExecutionPolicy Bypass -File "$repo\deploy.ps1" %*
"@

function New-Shortcut([string]$path) {
  New-Item -ItemType Directory -Force -Path (Split-Path $path) | Out-Null
  $shell = New-Object -ComObject WScript.Shell
  $lnk = $shell.CreateShortcut($path)
  $lnk.TargetPath = $exe
  $lnk.WorkingDirectory = $dest
  $lnk.IconLocation = "$exe,0"
  $lnk.Description = 'Every MindAttic repo''s tickets: queue, approve, reject, reorder.'
  $lnk.Save()
  Write-Host "Shortcut $path"
}
New-Shortcut (Join-Path ([Environment]::GetFolderPath('Desktop')) 'MindAttic Workflow.lnk')
New-Shortcut (Join-Path ([Environment]::GetFolderPath('Programs')) 'MindAttic\MindAttic Workflow.lnk')

if (-not $SkipCli -and (Test-Path (Join-Path $repo 'tools\install.ps1'))) {
  Write-Host 'Installing the workflow CLI'
  & (Join-Path $repo 'tools\install.ps1')
}

Write-Host "Installed $exe"
if ($Launch) { Start-Process $exe }
