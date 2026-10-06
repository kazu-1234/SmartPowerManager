# SmartPowerManager Pico W 専用ファーム

SmartPowerManager（C#）から LAN 経由で MAC と起動スケジュールを受け取り、NTP 時刻に合わせて Wake-on-LAN します。ブラウザで IP を開くとスケジュール一覧を確認できます。

## セットアップ

1. `config.h.example` を `config.h` にコピー
2. WiFi・固定 IP（`STATIC_IP_BYTES`）・NTP を編集
3. Arduino IDE で本フォルダの `SmartPowerManager_PicoW.ino` を開く
4. ボード: Raspberry Pi Pico 2 W（Pico W 互換）
5. 書き込み

`config.h` はリポジトリに含めない（`.gitignore`）。

## API

| メソッド | パス | 内容 |
|----------|------|------|
| GET | `/` `/schedule` | スケジュール一覧 HTML |
| POST | `/update_schedule` | mac / 起動 / auto_wol_* / auto_wol_rules |
| GET | `/get_schedule` | JSON（起動一覧 + 3分前条件と次時刻） |

受信内容は EEPROM に保存され、再起動後も維持されます。

## アプリ側

設定の **Pico W IP** を `config.h` の固定 IP に合わせ、**起動対象 MAC** を選択して同期します。

旧 `Python/SmartPowerManager_PicoW` は非推奨（本フォルダが正本）。
