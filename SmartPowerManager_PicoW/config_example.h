#ifndef CONFIG_H
#define CONFIG_H

// --- IRremote警告抑制 ---
// RP2040でSEND_PWM_BY_TIMERがデフォルト有効の警告を抑制
#define SUPPRESS_SEND_PWM_BY_TIMER_INFO
// LED_BUILTINを無効化してフィードバックLED警告を抑制
#define NO_LED_FEEDBACK_CODE

//================================================================
// ★★★ 設定ファイル (このファイルに機密情報を入力してください) ★★★
//================================================================

// --- デバッグ設定 ---
// trueにすると、動作状況がシリアルモニタに詳細に出力され、デバッグ用メニューが表示されます。
// falseにすると、出力とメニューが抑制されます。
constexpr bool DEBUG = false;

// --- WiFi設定 (優先順位順に3つまで設定) ---
struct WiFiCredential
{
  const char *ssid;
  const char *password;
};

const WiFiCredential wifiCredentials[] = {
    {"YOUR_SSID_1", "YOUR_PASSWORD_1"}, // 優先度1
    {"YOUR_SSID_2", "YOUR_PASSWORD_2"}, // 優先度2
    {"YOUR_SSID_3", "YOUR_PASSWORD_3"}   // 優先度3
};
const int numWifiCredentials = sizeof(wifiCredentials) / sizeof(wifiCredentials[0]);

const char *NTP_SERVER = "ntp.nict.jp";

// --- 静的IPアドレス設定 (固定IPを使用する場合) ---
// v5.2.0: IPアドレスの値をここに設定
// trueにするとIPアドレスを固定します。falseにするとDHCPから自動取得します。
const bool USE_STATIC_IP = true;                    // ★★★ 静的IPを使う場合は true に変更 ★★★
const byte STATIC_IP_BYTES[] = {192, 168, 10, xxx}; // 固定IP
const byte GATEWAY_BYTES[] = {192, 168, 10, 1};     // ゲートウェイ (ルーター)
const byte SUBNET_BYTES[] = {255, 255, 255, 0};     // サブネットマスク
const byte PRIMARY_DNS_BYTES[] = {8, 8, 8, 8};      // DNS (例: Google)
const byte SECONDARY_DNS_BYTES[] = {8, 8, 4, 4};    // (Pico W mbedコアでは現在使用されません)

// --- Google Apps Script ---
const char *GAS_URL_WOL = "YOUR_GAS_SCRIPT_URL_HERE"; // LINE経由のWoL指示を受け取るGASのURL


#endif // CONFIG_H
