# 共通基盤 WinUiShared をテンプレートからこのリポの Shared\ へ同期する
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts\sync-shared.ps1
#
# 正本は ..\WinUiAppTemplate\Shared（全アプリ共通の基盤はテンプレート側だけで直す）。
# このリポの Shared\ はそのコピーで、GitHub 上でこのリポ単体でもビルドできるように commit しておく。
# Shared\ を直接書き換えても次の同期で上書きされるので、修正は必ずテンプレート側で行うこと。

$ErrorActionPreference = "Stop"
chcp 65001 | Out-Null

$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path (Split-Path -Parent $root) "WinUiAppTemplate\Shared"
$dest = Join-Path $root "Shared"

# テンプレートが隣に無い（このリポだけ clone した）ときは、同梱の Shared\ をそのまま使う
if (-not (Test-Path (Join-Path $source "WinUiShared.csproj"))) {
    Write-Host "sync-shared: template not found ($source). Using bundled Shared."
    exit 0
}

# /MIR: 元と同じ内容にする（元で消したファイルはこちらでも消す）。bin / obj はビルド成果物なので対象外
robocopy $source $dest /MIR /XD bin obj /NFL /NDL /NJH /NJS /NP | Out-Null

# robocopy は 0～7 が成功（1 = コピーあり など）、8 以上が失敗
if ($LASTEXITCODE -ge 8) {
    throw "sync-shared: robocopy failed (exit=$LASTEXITCODE)"
}
Write-Host "sync-shared: Shared updated from $source"
exit 0
