# Build SmartPowerManager installer (dotnet publish + Inno Setup)
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1
#
# 手順の本体は共通基盤の Shared\scripts\Build-WinUiInstaller.ps1（全アプリ共通）。
# ここにはこのアプリ固有の値だけを書く。版は csproj の <Version> だけを書き換える（ここと .iss には書かない）。

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

# 共通基盤を Template（正本）から同期してから作る（リリースが古い Shared で作られないように）
& (Join-Path $PSScriptRoot "sync-shared.ps1")
$shared = Join-Path $root "Shared"

& (Join-Path $shared "scripts\Build-WinUiInstaller.ps1") `
    -Root $root `
    -Csproj "CSharp\SmartPowerManager.csproj" `
    -Iss "installer\SmartPowerManager.iss" `
    -ExeName "SmartPowerManager.exe" `
    -CopyToRoot
