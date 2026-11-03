# 全アプリ共通のインストーラー作成（dotnet publish + Inno Setup）
# 各アプリの scripts\build-installer.ps1 から、アプリごとの値を引数で渡して呼ぶ。
#
# 版は csproj の <Version> だけに書く（ここで読み取り、ISCC へ /DMyAppVersion で渡す）。
# 以前は csproj・ps1・iss の 3 か所に書いていて、AutoPasswordChanger で食い違っていた。
#
# 手順: 版を読む → AppId の重複を確かめる → obj\Release を消す → publish（デバッグ情報なし）
#       → .pri を確かめる → 不要ファイルを消す → ISCC → （指定時）リポ直下へコピー

param(
    # リポのルート（scripts の 1 つ上）
    [Parameter(Mandatory = $true)][string]$Root,
    # アプリの csproj（Root からの相対パス）
    [Parameter(Mandatory = $true)][string]$Csproj,
    # Inno Setup の .iss（Root からの相対パス）
    [Parameter(Mandatory = $true)][string]$Iss,
    # publish 後に必ずあるはずの exe 名
    [Parameter(Mandatory = $true)][string]$ExeName,
    # できた setup.exe をリポ直下にもコピーする（GitHub Releases に添付しやすくするため）
    [switch]$CopyToRoot,
    # publish フォルダから追加で消すファイル名（ワイルドカード可。BlueShift の使わない AI 用 DLL など）
    [string[]]$RemoveFiles = @()
)

$ErrorActionPreference = "Stop"
chcp 65001 | Out-Null
Set-Location $Root

$csprojPath = Join-Path $Root $Csproj
$issPath = Join-Path $Root $Iss
$publishDir = Join-Path $Root "dist\folder"
$outDir = Join-Path $Root "dist\installer"

# ── 版（csproj の <Version> だけが正本）──
$versionMatch = Select-String -Path $csprojPath -Pattern "<Version>([^<]+)</Version>" | Select-Object -First 1
if (-not $versionMatch) {
    throw "Version not found in $csprojPath"
}
$version = $versionMatch.Matches[0].Groups[1].Value
Write-Host "==> Version $version"

# ── AppId の重複チェック ──
# 同じ AppId のアプリがあると、そのアプリのフォルダへ上書きインストールされて両方とも壊れる
# （2026-10 に HomeServerManager → SunriseWatcher で発生）。Visual_Studio 直下のほかのアプリの .iss と比べ、
# 「AppId が同じでアプリ名が違う」ときだけ止める（同じアプリのバックアップの複製は同じ AppId で正しい）。
function Read-IssDefine([string]$path, [string]$name) {
    $line = Select-String -Path $path -Pattern ('^\s*#define\s+' + $name + '\s+"([^"]*)"') -Encoding UTF8 | Select-Object -First 1
    if ($line) { return $line.Matches[0].Groups[1].Value }
    return $null
}
$myAppId = Read-IssDefine $issPath "MyAppId"
$myAppName = Read-IssDefine $issPath "MyAppName"
if (-not $myAppId) {
    throw "MyAppId is not defined in $issPath"
}
$workspace = Split-Path -Parent $Root
Get-ChildItem $workspace -Directory | ForEach-Object {
    Get-ChildItem (Join-Path $_.FullName "installer") -Filter *.iss -ErrorAction SilentlyContinue
} | Where-Object { $_.FullName -ne (Resolve-Path $issPath).Path } | ForEach-Object {
    $otherId = Read-IssDefine $_.FullName "MyAppId"
    $otherName = Read-IssDefine $_.FullName "MyAppName"
    if ($otherId -and $otherId -eq $myAppId -and $otherName -ne $myAppName) {
        throw "AppId $myAppId is also used by $otherName ($($_.FullName)). Use a new GUID: [guid]::NewGuid()"
    }
}

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

# Release の中間ファイルを消して必ずコンパイルし直す（DebugType を変えても差分ビルドでは古い DLL が使い回され、
# .pdb のパスが埋め込まれたまま配布されてしまうため）。共通基盤はこのスクリプトの 1 つ上のフォルダ
$sharedDir = Split-Path -Parent $PSScriptRoot
foreach ($objRelease in @((Join-Path (Split-Path -Parent $csprojPath) "obj\x64\Release"), (Join-Path $sharedDir "obj\Release"))) {
    if (Test-Path $objRelease) {
        Remove-Item -Recurse -Force $objRelease
    }
}

dotnet publish $csprojPath `
    -c Release `
    -p:Platform=x64 `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:DebugType=none `
    -o $publishDir
# DebugType=none: 既定だと DLL に .pdb の絶対パス（C:\Users\<ユーザー名>\...）が埋め込まれ、配布物からユーザー名が分かってしまう。
# 配布物にはデバッグ情報を入れないので、パスごと出力しない

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed (exit=$LASTEXITCODE)"
}

$exe = Join-Path $publishDir $ExeName
if (-not (Test-Path $exe)) {
    throw "$ExeName missing in publish output: $publishDir"
}

# アプリ本体の .pri（XAML と文言）が無いと、インストール版が起動直後に何も出さず終了する。
# csproj の CopyAppPriToPublishDir が効いているかをここで確かめ、無ければインストーラを作らずに止める
# （bin から補うと Debug ビルドの古い .pri が混ざり得るので、補わない）
$pri = Join-Path $publishDir ([IO.Path]::ChangeExtension($ExeName, ".pri"))
if (-not (Test-Path $pri)) {
    throw "$(Split-Path $pri -Leaf) missing in publish output (app would exit on launch): $publishDir"
}

Get-ChildItem $publishDir -Recurse -File | Where-Object {
    $file = $_
    $file.Extension -eq ".pdb" -or ($RemoveFiles | Where-Object { $file.Name -like $_ })
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

& $iscc "/DMyAppVersion=$version" $issPath
if ($LASTEXITCODE -ne 0) {
    throw "ISCC failed (exit=$LASTEXITCODE)"
}

# setup.exe の名前は .iss の OutputBaseFilename（アプリ名-v版-win-x64-setup）
$setup = Get-ChildItem $outDir -Filter ("*-v{0}-win-x64-setup.exe" -f $version) |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $setup) {
    throw "Installer not created in $outDir (version $version)"
}

$setupMb = [math]::Round($setup.Length / 1MB, 1)
Write-Host ("OK: {0} ({1} MB)" -f $setup.FullName, $setupMb)

if ($CopyToRoot) {
    # *.exe は .gitignore で除外しているので、リポ直下に置いても commit されない
    $rootCopy = Join-Path $Root $setup.Name
    Copy-Item $setup.FullName $rootCopy -Force
    Write-Host ("copied: {0}" -f $rootCopy)
}
