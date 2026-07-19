# SmartPowerManager

Windows PC のシャットダウン／再起動スケジュール管理と、Raspberry Pi Pico W / GAS 経由の Wake on LAN 遠隔起動を統合する常駐アプリです。

## 構成

| フォルダ | 内容 |
| --- | --- |
| `CSharp/` | **推奨** WinUI 3 版（v2.0.0） |
| `Python/` | 従来の Python 版（v1.9.2）および Pico W / GAS 連携スクリプト |

## C# 版（v2.0.0）の主な機能

- シャットダウン／再起動／起動スケジュール（毎日・毎週・一回限り・クイック）
- 監視オン／オフ（シャットダウンと再起動で独立）
- Pico W / GAS 連携（WoL 同期、3分前自動 WoL）
- タスクトレイ常駐、多重起動防止、ログオン時自動起動
- ライト／ダーク／システム連動テーマ
- GitHub Releases からのアップデート確認

## ビルド（C#）

```powershell
cd CSharp
dotnet publish SmartPowerManager.csproj -c Release -r win-x64 -p:Platform=x64 --self-contained true
```

## 免責事項

本ソフトウェアの使用により生じたいかなる損害についても、開発者は一切の責任を負いません。自己責任でご使用ください。

## ライセンス

MIT LICENSE
