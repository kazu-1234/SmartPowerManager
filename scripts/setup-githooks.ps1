# Git フック（.githooks）を有効化する
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$hooksDir = Join-Path $repoRoot '.githooks'
$gitHooksDir = Join-Path $repoRoot '.git\hooks'

git config core.hooksPath .githooks

New-Item -ItemType Directory -Force -Path $gitHooksDir | Out-Null
Copy-Item (Join-Path $hooksDir 'commit-msg') (Join-Path $gitHooksDir 'commit-msg') -Force
Copy-Item (Join-Path $hooksDir 'prepare-commit-msg') (Join-Path $gitHooksDir 'prepare-commit-msg') -Force

Write-Host 'Installed commit-msg hooks to .githooks and .git/hooks'
Write-Host 'core.hooksPath = .githooks'
