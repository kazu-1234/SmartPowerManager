# SmartPowerManager C# 版

WinUI 3 ベースの SmartPowerManager v2.0.0

## ビルド

Visual Studio 2022/2026 で `SmartPowerManager.sln` を開き、x64 Release でビルドしてください。

```powershell
dotnet build SmartPowerManager.csproj -c Release -p:Platform=x64
```

## 主な機能

- シャットダウン / 再起動スケジュール（毎日・毎週・一回限り・クイック）
- Pico W / GAS 連携（WoL スケジュール同期、3分前自動 WoL）
- タスクトレイ常駐、多重起動防止
- ログオンタスクによる自動起動（`--background`）
- 3 テーマ（ライト / ダーク / システム連動）
- GitHub Releases アップデート確認

## 設定ファイル

- `%AppData%\SmartPowerManager\schedules.json` — Python 版互換スケジュール
- `%AppData%\SmartPowerManager\settings.json` — テーマ・自動起動設定

初回起動時、exe 横の旧 `schedules.json` を自動移行します。

## 免責事項

本ソフトウェアの使用により生じたいかなる損害についても、開発者は一切の責任を負いません。自己責任でご使用ください。
