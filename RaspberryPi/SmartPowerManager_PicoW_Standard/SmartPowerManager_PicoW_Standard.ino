/*
 * SmartPowerManager_PicoW_Standard.ino
 * Raspberry Pi Pico W用ファームウェア (Standard版)
 * 
 * 機能:
 * - 複数のWiFi設定から最適なAPに自動接続 (スキャン機能)
 * - 固定IP設定対応
 * - Webブラウザによる管理画面 (スケジュール設定、手動WoL、IP確認)
 * - NTPによる時刻同期とスケジュール起動 (WoL送信)
 * 
 * 必要なライブラリ:
 * - WiFi (RP2040 built-in)
 * - WebServer (RP2040 built-in / mbed core)
 * - NTPClient
 */

#include <WiFi.h>
#include <WebServer.h>
#include <NTPClient.h>
#include <WiFiUdp.h>
#include "config.h"

// ----------------------------------------------------------------
// 静的IP設定
// ----------------------------------------------------------------
IPAddress staticIP(STATIC_IP_BYTES);
IPAddress gateway(GATEWAY_BYTES);
IPAddress subnet(SUBNET_BYTES);
IPAddress primaryDNS(PRIMARY_DNS_BYTES);

// ----------------------------------------------------------------
// グローバル変数
// ----------------------------------------------------------------
WebServer server(80);
WiFiUDP ntpUDP;
// JST (+9時間 = 32400秒)
NTPClient timeClient(ntpUDP, NTP_SERVER, 32400, 60000); // 60秒更新

// WoL設定
WiFiUDP udp;
const int WOL_PORT = 9;
String targetMac = "";

// スケジュール定義
struct ScheduleDaily {
  bool enabled;
  int hour;
  int minute;
};
struct ScheduleWeekly {
  String id; // Added ID for management if needed, or simple index
  int weekday; // 0=Mon, 6=Sun (to match Python)
  int hour;
  int minute;
};
struct ScheduleOneTime {
  String id;
  int year;
  int month;
  int day;
  int hour;
  int minute;
  bool executed;
};

// スケジュールデータ
ScheduleDaily dailyWakeup = {false, 7, 0};
ScheduleWeekly weeklySchedules[10];
int weeklyCount = 0;
ScheduleOneTime onetimeSchedules[10];
int onetimeCount = 0;

// ----------------------------------------------------------------
// WiFi接続処理 (スキャン方式)
// ----------------------------------------------------------------
void connectToWiFi() {
  Serial.println("Connecting to WiFi...");

  if (USE_STATIC_IP) {
    Serial.println("Using Static IP configuration.");
    WiFi.config(staticIP, primaryDNS, gateway, subnet); 
    Serial.print("Static IP: ");
    Serial.println(staticIP);
  } else {
    Serial.println("Using DHCP (Dynamic IP).");
  }

  int bestNetwork = -1;
  int maxRssi = -1000;

  Serial.println("Scanning available networks...");
  int n = WiFi.scanNetworks();
  if (n == 0) {
    Serial.println("No networks found.");
  } else {
    Serial.print(n);
    Serial.println(" networks found.");
    for (int i = 0; i < n; ++i) {
      Serial.print(i + 1);
      Serial.print(": ");
      Serial.print(WiFi.SSID(i));
      Serial.print(" (");
      Serial.print(WiFi.RSSI(i));
      Serial.println(")");

      for (int j = 0; j < numWifiCredentials; ++j) {
        if (strcmp(WiFi.SSID(i), wifiCredentials[j].ssid) == 0) {
          Serial.print("  MATCHED with config index ");
          Serial.println(j);
          if (WiFi.RSSI(i) > maxRssi) {
            maxRssi = WiFi.RSSI(i);
            bestNetwork = j;
          }
        }
      }
    }
  }

  if (bestNetwork == -1) {
    Serial.println("No configured WiFi networks found in scan result.");
    // Retry or sleep? Just return to loop to retry later if logic allows, 
    // but here we wait/halt or retry.
  } else {
    Serial.print("Connecting to best network: ");
    Serial.println(wifiCredentials[bestNetwork].ssid);
    WiFi.begin(wifiCredentials[bestNetwork].ssid, wifiCredentials[bestNetwork].password);

    long startTime = millis();
    while (WiFi.status() != WL_CONNECTED && (millis() - startTime < 15000)) {
      digitalWrite(LED_BUILTIN, HIGH);
      delay(100);
      digitalWrite(LED_BUILTIN, LOW);
      delay(400);
    }
  }

  if (WiFi.status() == WL_CONNECTED) {
    digitalWrite(LED_BUILTIN, HIGH);
    Serial.println("\nWiFi Connected.");
    Serial.print("IP Address: ");
    Serial.println(WiFi.localIP());
    // WiFi.setSleep(false); // Removed: Not supported in this core
  } else {
    Serial.println("\nWiFi Connection Failed.");
    digitalWrite(LED_BUILTIN, LOW);
  }
}

// ----------------------------------------------------------------
// WoL Magic Packet 送信
// ----------------------------------------------------------------
void sendWOL(String macStr) {
  if (macStr.length() == 0) {
    Serial.println("Target MAC is empty. WoL canceled.");
    return;
  }
  
  byte mac[6];
  if (sscanf(macStr.c_str(), "%hxx:%hxx:%hxx:%hxx:%hxx:%hxx", 
             &mac[0], &mac[1], &mac[2], &mac[3], &mac[4], &mac[5]) != 6) {
    Serial.println("Invalid MAC address format.");
    return;
  }
  
  byte magicPacket[102];
  for (int i = 0; i < 6; i++) magicPacket[i] = 0xFF;
  for (int i = 0; i < 16; i++) {
    for (int j = 0; j < 6; j++) {
      magicPacket[6 + i * 6 + j] = mac[j];
    }
  }
  
  udp.beginPacket(IPAddress(255, 255, 255, 255), WOL_PORT);
  udp.write(magicPacket, 102);
  udp.endPacket();
  
  Serial.print("WoL Magic Packet sent to ");
  Serial.println(macStr);
}

// ----------------------------------------------------------------
// Webサーバー ハンドラ
// ----------------------------------------------------------------

// ルート: ステータス表示
void handleRoot() {
  timeClient.update();
  String formattedTime = timeClient.getFormattedTime();
  int day = timeClient.getDay(); // 0=Sun, 1=Mon... wait, library depends.
  // NTPClient (arduino-libraries/NTPClient) uses 0=Sunday, 1=Monday...
  // Python uses 0=Monday.
  // We need to be careful.
  // Let's display text.
  String weekStr = "";
  switch (day) {
    case 0: weekStr = "日"; break;
    case 1: weekStr = "月"; break;
    case 2: weekStr = "火"; break;
    case 3: weekStr = "水"; break;
    case 4: weekStr = "木"; break;
    case 5: weekStr = "金"; break;
    case 6: weekStr = "土"; break;
  }

  String message = "<html>\n<head><meta charset='UTF-8'><meta name='viewport' content='width=device-width, initial-scale=1.0'>\n";
  message += "<title>SmartPowerManager Pico W</title>\n";
  message += "<style>body{font-family:sans-serif;text-align:center;padding:20px;} h1{color:#007bff;} .card{background:#f8f9fa;padding:15px;margin:10px auto;max-width:400px;border-radius:10px;box-shadow:0 2px 4px rgba(0,0,0,0.1);}</style>\n";
  message += "</head><body>\n";
  message += "<h1>SmartPowerManager</h1>\n";
  
  message += "<div class='card'>\n";
  message += "<h2>Status</h2>\n";
  message += "<p>Time: " + formattedTime + " (" + weekStr + ")</p>\n";
  message += "<p>IP: " + WiFi.localIP().toString() + "</p>\n";
  message += "<p>Target MAC: " + targetMac + "</p>\n";
  message += "</div>\n";

  message += "<div class='card'>\n";
  message += "<h2>Schedule</h2>\n";
  if (dailyWakeup.enabled) {
      char buf[20];
      sprintf(buf, "%02d:%02d", dailyWakeup.hour, dailyWakeup.minute);
      message += "<p>Daily: " + String(buf) + "</p>\n";
  } else {
      message += "<p>Daily: Disabled</p>\n";
  }
  message += "<p>Weekly: " + String(weeklyCount) + " items</p>\n";
  message += "<p>OneTime: " + String(onetimeCount) + " items</p>\n";
  message += "</div>\n";

  message += "<div class='card'>\n";
  message += "<h2>Actions</h2>\n";
  message += "<form action='/force_wake' method='GET'><button style='padding:10px 20px;background:#28a745;color:white;border:none;border-radius:5px;'>Force Wake (WoL)</button></form>\n";
  message += "</div>\n";

  message += "</body></html>";
  server.send(200, "text/html", message);
}

// 設定受信 (POST)
// Format: daily=en,h,m&mac=...&weekly=d,h,m;...&onetime=y,m,d,h,m;...
// Note: en is 0 or 1.
void handleUpdateSchedule() {
  if (!server.hasArg("d_en")) {
    server.send(400, "text/plain", "Missing arguments");
    return;
  }

  // MAC
  if (server.hasArg("mac")) {
    targetMac = server.arg("mac");
  }

  // Daily
  int d_en = server.arg("d_en").toInt();
  int d_h = server.arg("d_h").toInt();
  int d_m = server.arg("d_m").toInt();
  dailyWakeup.enabled = (d_en == 1);
  dailyWakeup.hour = d_h;
  dailyWakeup.minute = d_m;

  // Weekly (CSV: d,h,m;d,h,m;...)
  // Parse simple CSV manually
  if (server.hasArg("weekly")) {
    String wStr = server.arg("weekly");
    weeklyCount = 0;
    int idx = 0;
    while (wStr.length() > 0 && weeklyCount < 10) {
      int semi = wStr.indexOf(';');
      String item = (semi == -1) ? wStr : wStr.substring(0, semi);
      wStr = (semi == -1) ? "" : wStr.substring(semi + 1);

      // item: d,h,m
      int comma1 = item.indexOf(',');
      int comma2 = item.lastIndexOf(',');
      if (comma1 != -1 && comma2 != -1 && comma1 != comma2) {
         int d = item.substring(0, comma1).toInt();
         int h = item.substring(comma1 + 1, comma2).toInt();
         int m = item.substring(comma2 + 1).toInt();
         weeklySchedules[weeklyCount++] = {String(weeklyCount), d, h, m};
      }
    }
  }

  // OneTime (CSV: y,m,d,h,m;...)
  if (server.hasArg("onetime")) {
    String oStr = server.arg("onetime");
    onetimeCount = 0;
    while (oStr.length() > 0 && onetimeCount < 10) {
      int semi = oStr.indexOf(';');
      String item = (semi == -1) ? oStr : oStr.substring(0, semi);
      oStr = (semi == -1) ? "" : oStr.substring(semi + 1);
      
      // y,m,d,h,m (count commas = 4)
      // Quick parse approach
      int c1 = item.indexOf(',');
      int c2 = item.indexOf(',', c1+1);
      int c3 = item.indexOf(',', c2+1);
      int c4 = item.indexOf(',', c3+1);
      if (c1!=-1 && c2!=-1 && c3!=-1 && c4!=-1) {
          int y = item.substring(0, c1).toInt();
          int mo = item.substring(c1+1, c2).toInt();
          int d = item.substring(c2+1, c3).toInt();
          int h = item.substring(c3+1, c4).toInt();
          int mi = item.substring(c4+1).toInt();
          onetimeSchedules[onetimeCount++] = {String(onetimeCount), y, mo, d, h, mi, false};
      }
    }
  }

  Serial.println("Schedule Updated via POST");
  server.send(200, "text/plain", "OK");
}

void handleForceWake() {
  sendWOL(targetMac);
  server.send(200, "text/html", "<html><body><h1>WoL Packet Sent</h1><p>Returning in 3s...</p><meta http-equiv='refresh' content='3;url=/' ></body></html>");
}

// ----------------------------------------------------------------
// スケジュールチェック
// ----------------------------------------------------------------
void checkSchedule() {
  // NTPClient uses Unix Epoch.
  time_t now = timeClient.getEpochTime();
  struct tm *ptm = gmtime(&now);
  // Correct timezone? NTPClient(...., 32400) offsets it already for getFormattedTime
  // BUT getEpochTime returns UTC epoch unless offset is applied?
  // NTPClient implementation: getEpochTime returns raw time + offset.
  // So 'now' is JST.
  // Warning: standard gmtime expects UTC. If 'now' is JST, gmtime will give fields as if it were UTC.
  // Example: if now is 9:00 JST, epoch is X. if getEpochTime adds 9h, then returns X+9h.
  // gmtime(X+9h) -> "9:00". So it corresponds to local time fields.
  // So:
  int currentYear = ptm->tm_year + 1900;
  int currentMonth = ptm->tm_mon + 1;
  int currentDay = ptm->tm_mday;
  int currentHour = timeClient.getHours();
  int currentMinute = timeClient.getMinutes();
  int currentSecond = timeClient.getSeconds();
  int currentWeekday = timeClient.getDay(); // 0=Sun...6=Sat

  // Python app uses 0=Mon...6=Sun.
  // NTPClient: 0=Sun, 1=Mon, 2=Tue, 3=Wed, 4=Thu, 5=Fri, 6=Sat.
  // Conversion needed:
  int pyWeekday = (currentWeekday == 0) ? 6 : (currentWeekday - 1);

  // Check 1x per minute (at 00 seconds)
  static int lastCheckedMinute = -1;
  if (currentMinute == lastCheckedMinute) return;
  lastCheckedMinute = currentMinute;

  Serial.printf("Check Schedule: %04d/%02d/%02d %02d:%02d (%d)\n", currentYear, currentMonth, currentDay, currentHour, currentMinute, pyWeekday);

  bool wake = false;

  // Daily
  if (dailyWakeup.enabled) {
    if (dailyWakeup.hour == currentHour && dailyWakeup.minute == currentMinute) {
      Serial.println("Trigger: Daily");
      wake = true;
    }
  }

  // Weekly
  for (int i = 0; i < weeklyCount; i++) {
    if (weeklySchedules[i].weekday == pyWeekday &&
        weeklySchedules[i].hour == currentHour &&
        weeklySchedules[i].minute == currentMinute) {
      Serial.println("Trigger: Weekly");
      wake = true;
    }
  }

  // OneTime
  for (int i = 0; i < onetimeCount; i++) {
    if (!onetimeSchedules[i].executed) {
      if (onetimeSchedules[i].year == currentYear &&
          onetimeSchedules[i].month == currentMonth &&
          onetimeSchedules[i].day == currentDay &&
          onetimeSchedules[i].hour == currentHour &&
          onetimeSchedules[i].minute == currentMinute) {
        Serial.println("Trigger: OneTime");
        wake = true;
        onetimeSchedules[i].executed = true; // Mark done
      }
    }
  }

  if (wake) {
    sendWOL(targetMac);
  }
}

// ----------------------------------------------------------------
// SETUP & LOOP
// ----------------------------------------------------------------
void setup() {
  pinMode(LED_BUILTIN, OUTPUT);
  digitalWrite(LED_BUILTIN, LOW);
  Serial.begin(115200);
  delay(1000);

  // Default MAC
  #ifdef MAC_DESKTOP
    targetMac = String(MAC_DESKTOP);
  #endif

  connectToWiFi();

  if (WiFi.status() == WL_CONNECTED) {
    timeClient.begin();
    // Quick sync
    if (timeClient.forceUpdate()) {
      Serial.println("NTP Updated.");
    }

    server.on("/", handleRoot);
    server.on("/update_schedule", HTTP_POST, handleUpdateSchedule);
    server.on("/force_wake", handleForceWake);
    server.begin();
    Serial.println("HTTP server started");
  }
}

void loop() {
  if (WiFi.status() == WL_CONNECTED) {
    server.handleClient();
    checkSchedule();
    // timeClient update is called in handleRoot, but should be called regularly
    static unsigned long lastUpdate = 0;
    if (millis() - lastUpdate > 60000) {
      timeClient.update();
      lastUpdate = millis();
    }
  } else {
     // Reconnect?
     // connectToWiFi(); // Blocking... might be bad.
  }
}
