# Build SmartPowerManager installer (dotnet publish + Inno Setup)
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$version = "2.0.23"
$publishDir = Join-Path $root "dist\folder"
$iss = Join-Path $root "installer\SmartPowerManager.iss"
$outDir = Join-Path $root "dist\installer"
$csproj = Join-Path $root "CSharp\SmartPowerManager.csproj"

$isccCandidates = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 7\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "ISCC.exe not found. Install Inno Setup 6+."
}

Write-Host "==> Publish (self-contained folder)"
if (Test-Path $publishDir) {
    Remove-Item -Recurse -Force $publishDir
}

dotnet publish $csproj `
    -c Release `
    -p:Platform=x64 `
    -r win-x64 `
    --self-contained true `
    -p:WindowsAppSDKSelfContained=true `
    -p:PublishSingleFile=false `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed (exit=$LASTEXITCODE)"
}

$exe = Join-Path $publishDir "SmartPowerManager.exe"
if (-not (Test-Path $exe)) {
    throw "SmartPowerManager.exe missing in publish output: $publishDir"
}

Get-ChildItem $publishDir -Recurse -File | Where-Object {
    $_.Extension -eq ".pdb"
} | ForEach-Object {
    Remove-Item $_.FullName -Force
    Write-Host ("removed {0}" -f $_.Name)
}

$folderBytes = (Get-ChildItem $publishDir -Recurse -File | Measure-Object Length -Sum).Sum
$folderMb = [math]::Round($folderBytes / 1MB, 1)
Write-Host ("publish folder: {0} MB" -f $folderMb)

Write-Host "==> Inno Setup compile"
if (-not (Test-Path $outDir)) {
    New-Item -ItemType Directory -Path $outDir | Out-Null
}

& $iscc $iss
if ($LASTEXITCODE -ne 0) {
    throw "ISCC failed (exit=$LASTEXITCODE)"
}

$setup = Join-Path $outDir ("SmartPowerManager-v{0}-win-x64-setup.exe" -f $version)
if (-not (Test-Path $setup)) {
    throw "Installer not created: $setup"
}

$setupBytes = (Get-Item $setup).Length
$setupMb = [math]::Round($setupBytes / 1MB, 1)
Write-Host ("OK: {0} ({1} MB)" -f $setup, $setupMb)

$rootCopy = Join-Path $root ("SmartPowerManager-v{0}-win-x64-setup.exe" -f $version)
Copy-Item $setup $rootCopy -Force
Write-Host ("copied: {0}" -f $rootCopy)
