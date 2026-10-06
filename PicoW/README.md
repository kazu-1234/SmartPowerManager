# SmartPowerManager Pico W 専用ファーム

Web UI / LINE / 赤外線なし。SmartPowerManager（C#）から LAN 経由で MAC と起動スケジュールを受け取り、NTP 時刻に合わせて Wake-on-LAN します。

## セットアップ

1. `config.h.example` を `config.h` にコピー
2. WiFi・固定 IP（`STATIC_IP_BYTES`）・NTP を編集
3. Arduino IDE で本フォルダの `SmartPowerManager_PicoW.ino` を開く
4. ボード: Raspberry Pi Pico 2 W（Pico W 互換）
5. 書き込み

`config.h` はリポジトリに含めない（`.gitignore`）。

## API（アプリ互換）

| メソッド | パス | 内容 |
|----------|------|------|
| POST | `/update_schedule` | `mac`, `d_en`/`d_h`/`d_m`, `weekly`, `onetime`, `auto_wol_weekly`, `auto_wol_onetime` |
| GET | `/get_schedule` | 現在の起動スケジュール JSON |

受信内容は EEPROM に保存され、再起動後も維持されます。

## アプリ側

設定の **Pico W IP** を `config.h` の固定 IP に合わせ、**起動対象 MAC** を選択して同期します。GAS は使いません。

旧 `Python/SmartPowerManager_PicoW` は非推奨（本フォルダが正本）。
