/*
 * SmartPowerManager_PicoW_GAS.ino
 * Raspberry Pi Pico W用ファームウェア (GAS/LINE連携版)
 * 
 * 機能:
 * - Standard版の全機能（Web管理、スケジュール起動、スキャン接続、固定IP）
 * - Google Apps Script (GAS) へのポーリングによるLINEからの起動指示受け取り
 * - https対応 (WiFiClientSecure使用)
 * 
 * GAS API:
 * - ?action=signal → "TRIGGER" があればトリガーあり
 * - ?action=command → "デスクトップPC起動" or "サーバーPC起動" を返す
 * 
 * 必要なライブラリ:
 * - WiFi (RP2040 built-in)
 * - WebServer (RP2040 built-in / mbed core)
 * - NTPClient
 * - WiFiClientSecure (RP2040 built-in)
 * 
 * v1.6.3
 * - Web UIからの毎日スケジュール設定に対応
 * - /update_schedule, /update_daily, /get_schedule APIのJSON対応
 */


#include <WiFi.h>
#include <WebServer.h>
#include <NTPClient.h>
#include <WiFiUdp.h>
#include <WiFiClientSecure.h>
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
NTPClient timeClient(ntpUDP, NTP_SERVER, 32400, 60000); // JST
bool shouldBlink = false;

WiFiUDP udp;
const int WOL_PORT = 9;
String targetMac = "";

// GASポーリング
unsigned long lastGasPoll = 0;
const unsigned long GAS_POLL_INTERVAL = 5000; // 5秒

// スケジュール定義
struct ScheduleDaily { bool enabled; int hour; int minute; };
struct ScheduleWeekly { String id; int weekday; int hour; int minute; };
struct ScheduleOneTime { String id; int year; int month; int day; int hour; int minute; bool executed; String source; };

ScheduleDaily dailyWakeup = {false, 7, 0};
ScheduleWeekly weeklySchedules[10];
int weeklyCount = 0;
ScheduleOneTime onetimeSchedules[10];
int onetimeCount = 0;

// ----------------------------------------------------------------
// LED制御
// ----------------------------------------------------------------
void blinkLED(int times, int onMs, int offMs) {
  for (int i = 0; i < times; i++) {
    digitalWrite(LED_BUILTIN, HIGH);
    delay(onMs);
    digitalWrite(LED_BUILTIN, LOW);
    if (i < times - 1) delay(offMs);
  }
}

// ----------------------------------------------------------------
// WiFi接続処理 (スキャン方式)
// ----------------------------------------------------------------
void connectToWiFi() {
  Serial.println("Connecting to WiFi...");

  if (USE_STATIC_IP) {
    Serial.println("Using Static IP configuration.");
    WiFi.config(staticIP, primaryDNS, gateway, subnet); 
  }

  int bestNetwork = -1;
  int maxRssi = -1000;

  Serial.println("Scanning available networks...");
  int n = WiFi.scanNetworks();
  if (n == 0) {
    Serial.println("No networks found.");
  } else {
    for (int i = 0; i < n; ++i) {
      Serial.printf("%d: %s (%d)\n", i+1, WiFi.SSID(i), WiFi.RSSI(i));
      for (int j = 0; j < numWifiCredentials; ++j) {
        if (strcmp(WiFi.SSID(i), wifiCredentials[j].ssid) == 0) {
          if (WiFi.RSSI(i) > maxRssi) {
            maxRssi = WiFi.RSSI(i);
            bestNetwork = j;
          }
        }
      }
    }
  }

  if (bestNetwork != -1) {
    Serial.print("Connecting to: ");
    Serial.println(wifiCredentials[bestNetwork].ssid);
    WiFi.begin(wifiCredentials[bestNetwork].ssid, wifiCredentials[bestNetwork].password);
    
    // WiFi接続中: 1秒間隔で点滅
    long startTime = millis();
    while (WiFi.status() != WL_CONNECTED && (millis() - startTime < 15000)) {
      digitalWrite(LED_BUILTIN, HIGH); delay(500);
      digitalWrite(LED_BUILTIN, LOW); delay(500);
    }
  }

  if (WiFi.status() == WL_CONNECTED) {
    // 接続完了: 2回素早く点滅後、消灯
    blinkLED(2, 100, 100);
    digitalWrite(LED_BUILTIN, LOW);
    Serial.println("WiFi Connected.");
    Serial.print("IP: ");
    Serial.println(WiFi.localIP());
  } else {
    Serial.println("WiFi Failed.");
    digitalWrite(LED_BUILTIN, LOW);
  }
}

// ----------------------------------------------------------------
// WoL (MAC指定可能)
// ----------------------------------------------------------------
void sendWOL(String macStr) {
  if (macStr.length() == 0) {
    Serial.println("MAC is empty, skip WoL.");
    return;
  }
  
  byte mac[6];
  // MACアドレスのパース (hxx -> hhx format note: sscanf with %hhx for byte)
  if (sscanf(macStr.c_str(), "%hhx:%hhx:%hhx:%hhx:%hhx:%hhx", 
             &mac[0], &mac[1], &mac[2], &mac[3], &mac[4], &mac[5]) != 6) {
    Serial.println("Invalid MAC format.");
    return;
  }
  
  byte magicPacket[102];
  for (int i = 0; i < 6; i++) magicPacket[i] = 0xFF;
  for (int i = 0; i < 16; i++) {
    for (int j = 0; j < 6; j++) magicPacket[6 + i * 6 + j] = mac[j];
  }
  
  udp.beginPacket(IPAddress(255, 255, 255, 255), WOL_PORT);
  udp.write(magicPacket, 102);
  udp.endPacket();
  
  // WoL実行時: 2回素早く点滅
  blinkLED(2, 100, 100);
  
  Serial.print("WoL Sent to: ");
  Serial.println(macStr);
}

// ----------------------------------------------------------------
// Web Handlers
// ----------------------------------------------------------------
void handleRoot() {
  timeClient.update();
  String formattedTime = timeClient.getFormattedTime();
  int day = timeClient.getDay();
  String weekStr = "日";
  if (day==1) weekStr="月"; if (day==2) weekStr="火"; if (day==3) weekStr="水"; 
  if (day==4) weekStr="木"; if (day==5) weekStr="金"; if (day==6) weekStr="土";
  
  // 曜日名配列（Python側と合わせる: 0=月, 1=火, ..., 6=日）
  const char* weekdayNames[] = {"月", "火", "水", "木", "金", "土", "日"};

  String message = "<!DOCTYPE html><html><head><meta charset='UTF-8'>";
  message += "<meta name='viewport' content='width=device-width, initial-scale=1.0'>";
  message += "<title>SmartPowerManager</title>";
  message += "<style>body{font-family:sans-serif;padding:20px;background:#f0f2f5;color:#333;}";
  message += ".card{background:white;padding:20px;margin-bottom:20px;border-radius:12px;box-shadow:0 4px 6px rgba(0,0,0,0.1);}";
  message += "h1{color:#1a73e8;text-align:center;font-size:24px;margin-bottom:20px;}";
  message += "h2{color:#444;border-bottom:2px solid #eee;padding-bottom:10px;margin-top:0;font-size:18px;}";
  message += "p{margin:10px 0;line-height:1.5;}";
  message += "button{padding:8px 16px;background:#1a73e8;color:white;border:none;border-radius:4px;cursor:pointer;font-size:14px;transition:background 0.2s;}";
  message += "button:hover{background:#1557b0;}";
  message += "button.delete{background:#dc3545;} button.delete:hover{background:#c82333;}";
  message += "input.clock-input, select {padding:8px;border:1px solid #ddd;border-radius:4px;font-size:14px;width:70px;text-align:center;}";
  message += "input[type=checkbox] {transform: scale(1.5); margin: 0 10px;}";
  message += ".btn-row{display:flex;flex-wrap:wrap;gap:10px;margin-top:15px;}";
  message += ".form-row{display:flex;align-items:center;gap:5px;flex-wrap:wrap;margin:10px 0;}";
  message += "ul{list-style:none;padding:0;} li{background:#f8f9fa;margin:5px 0;padding:10px;border-radius:4px;display:flex;justify-content:space-between;align-items:center;}";
  message += "</style></head><body>";
  message += "<h1>SmartPowerManager</h1>";
  
  // ステータス
  message += "<div class='card'><h2>ステータス</h2>";
  message += "<p>現在時刻: " + formattedTime + " (" + weekStr + ")</p>";
  message += "<p>IPアドレス: " + WiFi.localIP().toString() + "</p>";
  message += "<p>アプリ設定MAC: " + targetMac + "</p>";
  message += "<p>GASポーリング: 5秒間隔</p></div>";
  
  // 手動起動ボタン（横並び）
  message += "<div class='card'><h2>手動起動 (WoL)</h2>";
  message += "<div class='btn-row'>";
  message += "<button onclick=\"doWol(this, '/wake_desktop')\">デスクトップPC</button>";
  message += "<button onclick=\"doWol(this, '/wake_server')\">サーバーPC</button>";
  message += "<button onclick=\"doWol(this, '/force_wake')\">アプリ設定PC</button>";
  message += "</div></div>";
  
  // スケジュール設定（詳細表示）
  message += "<div class='card'><h2>スケジュール設定</h2>";
  
  // 毎日
  if (dailyWakeup.enabled) {
      char buf[30]; sprintf(buf, "%02d時%02d分", dailyWakeup.hour, dailyWakeup.minute);
      message += "<p id='daily-status'><b>毎日:</b> " + String(buf) + "</p>";
  } else { 
      message += "<p id='daily-status'><b>毎日:</b> 無効</p>"; 
  }
  
  // 毎週
  message += "<p id='weekly-status'><b>毎週:</b> ";
  if (weeklyCount == 0) {
      message += "なし";
  } else {
      for (int i = 0; i < weeklyCount; i++) {
          char buf[30];
          int wd = weeklySchedules[i].weekday;
          if (wd >= 0 && wd <= 6) {
              sprintf(buf, "%s曜日 %02d時%02d分", weekdayNames[wd], weeklySchedules[i].hour, weeklySchedules[i].minute);
          } else {
              sprintf(buf, "? %02d:%02d", weeklySchedules[i].hour, weeklySchedules[i].minute);
          }
          message += String(buf);
          if (i < weeklyCount - 1) message += ", ";
      }
  }
  message += "</p>";
  
  // 一回限り
  message += "<p id='onetime-status'><b>一回限り:</b> ";
  if (onetimeCount == 0) {
      message += "なし";
  } else {
      for (int i = 0; i < onetimeCount; i++) {
          char buf[30];
          sprintf(buf, "%04d年%02d月%02d日 %02d時%02d分", 
                  onetimeSchedules[i].year, onetimeSchedules[i].month, onetimeSchedules[i].day,
                  onetimeSchedules[i].hour, onetimeSchedules[i].minute);
          message += String(buf);
          if (onetimeSchedules[i].executed) message += "(済)";
          if (i < onetimeCount - 1) message += ", ";
      }
  }
  message += "</p></div>";

  // ブラウザ設定フォーム
  message += "<div class='card'><h2>設定変更 (毎日)</h2>";
  message += "<form action='/update_daily' method='POST' onsubmit='event.preventDefault(); submitForm(this);'>";
  message += "<div class='form-row'><label style='display:flex;align-items:center;'>有効: <input type='checkbox' name='enabled' " + String(dailyWakeup.enabled ? "checked" : "") + "></label></div>";
  message += "<div class='form-row'><label>時刻: </label>";
  char dh[3]; sprintf(dh, "%02d", dailyWakeup.hour);
  char dm[3]; sprintf(dm, "%02d", dailyWakeup.minute);
  message += "<input type='text' class='clock-input' name='h' data-min='0' data-max='23' value='" + String(dh) + "'> : ";
  message += "<input type='text' class='clock-input' name='m' data-min='0' data-max='59' value='" + String(dm) + "'>";
  message += "<button type='submit' style='margin-left:10px;'>更新</button></div></form></div>";

  // 毎週設定フォーム
  message += "<div class='card'><h2>設定変更 (毎週)</h2>";
  message += "<form action='/add_weekly' method='POST' class='form-row' onsubmit='event.preventDefault(); submitForm(this);'>";
  message += "<select name='wd' style='width:auto;'>";
  for(int i=0; i<7; i++) message += "<option value='" + String(i) + "'>" + String(weekdayNames[i]) + "</option>";
  message += "</select>";
  message += "<input type='text' class='clock-input' name='h' data-min='0' data-max='23' placeholder='時' required> : ";
  message += "<input type='text' class='clock-input' name='m' data-min='0' data-max='59' placeholder='分' required>";
  message += "<button type='submit' style='margin-left:10px;'>追加</button></form>";
  message += "<hr><ul id='weekly-list'>";
  for(int i=0; i<weeklyCount; i++) {
    int wd = weeklySchedules[i].weekday;
    String dayName = (wd >= 0 && wd <= 6) ? weekdayNames[wd] : "?";
    char timeBuf[20]; sprintf(timeBuf, "%02d時%02d分", weeklySchedules[i].hour, weeklySchedules[i].minute);
    message += "<li><span>" + dayName + "曜日 " + String(timeBuf) + "</span>";
    message += " <button class='delete' onclick='del(\"/delete_weekly\", " + String(i) + ")'>削除</button></li>";
  }
  message += "</ul></div>";

  // 一回限り・クイック設定フォーム
  message += "<div class='card'><h2>設定変更 (一回限り・クイック)</h2>";
  message += "<form action='/add_onetime' method='POST' class='form-row' onsubmit='event.preventDefault(); submitForm(this);'>";
  
  // Calculate Year from Epoch
  time_t epochTime = timeClient.getEpochTime();
  struct tm *ptm = gmtime((const time_t *)&epochTime); 
  int currentYear = ptm->tm_year + 1900;
  
  message += "<input type='text' class='clock-input' name='Y' placeholder='年' value='" + String(currentYear) + "' required data-min='2024' data-max='2100' data-pad='4' style='width:60px;'> / ";
  message += "<input type='text' class='clock-input' name='M' placeholder='月' required data-min='1' data-max='12'> / ";
  message += "<input type='text' class='clock-input' name='D' placeholder='日' required data-min='1' data-max='31'> &nbsp;&nbsp;";
  message += "<input type='text' class='clock-input' name='h' placeholder='時' required data-min='0' data-max='23'> : ";
  message += "<input type='text' class='clock-input' name='m' placeholder='分' required data-min='0' data-max='59'>";
  message += "<button type='submit' style='margin-left:10px;'>追加</button></form>";
  message += "<hr><ul id='onetime-list'>";
  for(int i=0; i<onetimeCount; i++) {
    char dtBuf[50];
    sprintf(dtBuf, "%04d年%02d月%02d日 %02d時%02d分", 
            onetimeSchedules[i].year, onetimeSchedules[i].month, onetimeSchedules[i].day,
            onetimeSchedules[i].hour, onetimeSchedules[i].minute);
    message += "<li><span>" + String(dtBuf);
    if(onetimeSchedules[i].executed) message += " (済)";
    message += "</span> <button class='delete' onclick='del(\"/delete_onetime\", " + String(i) + ")'>削除</button></li>";
  }
  message += "</ul></div>";
  
  message += "<script>";
  message += "document.querySelectorAll('.clock-input').forEach(i=>{";
  message += "  const padLen = i.dataset.pad ? parseInt(i.dataset.pad) : 2;";
  message += "  const update = (val) => {";
  message += "    if(i.dataset.min) val = Math.max(parseInt(i.dataset.min), val);";
  message += "    if(i.dataset.max) val = Math.min(parseInt(i.dataset.max), val);";
  message += "    i.value = val.toString().padStart(padLen, '0');";
  message += "  };";
  message += "  i.addEventListener('wheel',e=>{";
  message += "    e.preventDefault();";
  message += "    let v = parseInt(i.value || 0) + (e.deltaY < 0 ? 1 : -1);";
  message += "    update(v);";
  message += "  }, { passive: false });";
  message += "  i.addEventListener('blur',e=>{";
  message += "    let v = parseInt(i.value || 0);";
  message += "    update(v);";
  message += "  });";
  message += "});";
  message += "const pad = (n, len=2) => n.toString().padStart(len, '0');";
  message += "const refresh = (data) => {";
  message += "  const d = data.daily;";
  message += "  document.getElementById('daily-status').innerHTML = d.enabled ? `<b>毎日:</b> ${pad(d.hour)}時${pad(d.minute)}分` : `<b>毎日:</b> 無効`;";
  message += "  const wStats = data.weekly.length ? data.weekly.map(w => {";
  message += "      const days = ['月','火','水','木','金','土','日'];";
  message += "      return `${days[w.weekday]}曜日 ${pad(w.hour)}時${pad(w.minute)}分`;";
  message += "  }).join(', ') : 'なし';";
  message += "  document.getElementById('weekly-status').innerHTML = `<b>毎週:</b> ${wStats}`;";
  message += "  const wl = document.getElementById('weekly-list');";
  message += "  wl.innerHTML = '';";
  message += "  data.weekly.forEach((w, i) => {";
  message += "      const days = ['月','火','水','木','金','土','日'];";
  message += "      const li = document.createElement('li');";
  message += "      li.innerHTML = `<span>${days[w.weekday]}曜日 ${pad(w.hour)}時${pad(w.minute)}分</span> <button class='delete' onclick='del(\"/delete_weekly\", ${i})'>削除</button>`;";
  message += "      wl.appendChild(li);";
  message += "  });";
  message += "  const oStats = data.onetime.length ? data.onetime.map(o => {";
  message += "      return `${o.year}年${pad(o.month)}月${pad(o.day)}日 ${pad(o.hour)}時${pad(o.minute)}分${o.executed?' (済)':''}`;";
  message += "  }).join(', ') : 'なし';";
  message += "  document.getElementById('onetime-status').innerHTML = `<b>一回限り:</b> ${oStats}`;";
  message += "  const ol = document.getElementById('onetime-list');";
  message += "  ol.innerHTML = '';";
  message += "  data.onetime.forEach((o, i) => {";
  message += "      const li = document.createElement('li');";
  message += "      li.innerHTML = `<span>${o.year}年${pad(o.month)}月${pad(o.day)}日 ${pad(o.hour)}時${pad(o.minute)}分${o.executed?' (済)':''}</span> <button class='delete' onclick='del(\"/delete_onetime\", ${i})'>削除</button>`;";
  message += "      ol.appendChild(li);";
  message += "  });";
  message += "};";
  message += "const submitForm = (form) => {";
  message += "    const fd = new FormData(form);";
  message += "    fetch(form.action, { method: 'POST', body: fd }).then(r => r.json()).then(d => refresh(d));";
  message += "};";
  message += "const doWol = (btn, url) => {";
  message += "  fetch(url).then(r => {";
  message += "    btn.setCustomValidity('実行しました');";
  message += "    btn.reportValidity();";
  message += "    setTimeout(() => { btn.setCustomValidity(''); }, 2000);";
  message += "  });";
  message += "};";
  message += "const del = (url, idx) => {";
  message += "    const fd = new FormData(); fd.append('idx', idx);";
  message += "    fetch(url, { method: 'POST', body: fd }).then(r => r.json()).then(d => refresh(d));";
  message += "};";
  message += "</script>";
  message += "</body></html>";
  server.send(200, "text/html", message);
}

// ブラウザからの毎日設定更新ハンドラ
void handleUpdateDaily() {
  bool en = server.hasArg("enabled"); // checkbox sends "on" if checked, nothing if unchecked
  // However, form submission depends on browser. usually present=on.
  // We can check if "enabled" arg exists.
  // Wait, standard checkbox behavior: send name=value if checked.
  // Using hasArg is likely sufficient if name is unique.
  
  // Correction: server.hasArg("enabled") is true if checked.
  // But wait, if unchecked, it's not sent.
  // So we need to handle "enabled" key being missing = false.
  
  dailyWakeup.enabled = server.hasArg("enabled");
  if (server.hasArg("h")) dailyWakeup.hour = server.arg("h").toInt();
  if (server.hasArg("m")) dailyWakeup.minute = server.arg("m").toInt();
  
  shouldBlink = true;
  
  // Redirect back to root
  server.send(200, "application/json", getScheduleJSON());
}

// 毎週追加
void handleAddWeekly() {
  if (weeklyCount < 10) {
    if (server.hasArg("wd") && server.hasArg("h") && server.hasArg("m")) {
       weeklySchedules[weeklyCount++] = {"web", server.arg("wd").toInt(), server.arg("h").toInt(), server.arg("m").toInt()};
       shouldBlink = true;
    }
  }
  server.send(200, "application/json", getScheduleJSON());
}
// 毎週削除
void handleDeleteWeekly() {
  if (server.hasArg("idx")) {
    int idx = server.arg("idx").toInt();
    if (idx >= 0 && idx < weeklyCount) {
      for(int i=idx; i<weeklyCount-1; i++) weeklySchedules[i] = weeklySchedules[i+1];
      weeklyCount--;
      shouldBlink = true;
    }
  }
  server.send(200, "application/json", getScheduleJSON());
}

// 一回限り追加
void handleAddOneTime() {
  if (onetimeCount < 10) {
    if (server.hasArg("Y") && server.hasArg("M") && server.hasArg("D") && server.hasArg("h") && server.hasArg("m")) {
       onetimeSchedules[onetimeCount++] = {"web", server.arg("Y").toInt(), server.arg("M").toInt(), server.arg("D").toInt(), server.arg("h").toInt(), server.arg("m").toInt(), false, "web"};
       shouldBlink = true;
    }
  }
  server.send(200, "application/json", getScheduleJSON());
}
// 一回限り削除
void handleDeleteOneTime() {
  if (server.hasArg("idx")) {
    int idx = server.arg("idx").toInt();
    if (idx >= 0 && idx < onetimeCount) {
      for(int i=idx; i<onetimeCount-1; i++) onetimeSchedules[i] = onetimeSchedules[i+1];
      onetimeCount--;
      shouldBlink = true;
    }
  }
  server.send(200, "application/json", getScheduleJSON());
}

void handleUpdateSchedule() {
  if (server.hasArg("mac")) targetMac = server.arg("mac");
  if (server.hasArg("d_en")) {
     dailyWakeup.enabled = (server.arg("d_en").toInt() == 1);
     dailyWakeup.hour = server.arg("d_h").toInt();
     dailyWakeup.minute = server.arg("d_m").toInt();
  }
  
  // Weekly parsing
  if (server.hasArg("weekly")) {
    String wStr = server.arg("weekly");
    weeklyCount = 0;
    while (wStr.length() > 0 && weeklyCount < 10) {
      int semi = wStr.indexOf(';');
      String item = (semi == -1) ? wStr : wStr.substring(0, semi);
      wStr = (semi == -1) ? "" : wStr.substring(semi + 1);
      int c1 = item.indexOf(','); int c2 = item.lastIndexOf(',');
      if (c1 != -1 && c2 != -1 && c1 != c2) {
         int d = item.substring(0, c1).toInt();
         int h = item.substring(c1 + 1, c2).toInt();
         int m = item.substring(c2 + 1).toInt();
         weeklySchedules[weeklyCount++] = {String(weeklyCount), d, h, m};
      }
    }
  }
  
  // OneTime parsing
  if (server.hasArg("onetime")) {
    String oStr = server.arg("onetime");
    onetimeCount = 0;
    while (oStr.length() > 0 && onetimeCount < 10) {
      int semi = oStr.indexOf(';');
      String item = (semi == -1) ? oStr : oStr.substring(0, semi);
      oStr = (semi == -1) ? "" : oStr.substring(semi + 1);
      int c1=item.indexOf(','), c2=item.indexOf(',',c1+1), c3=item.indexOf(',',c2+1), c4=item.indexOf(',',c3+1);
      int c5=item.indexOf(',',c4+1); // source check
      if (c4!=-1) {
          int y = item.substring(0, c1).toInt(); int mo = item.substring(c1+1, c2).toInt();
          int d = item.substring(c2+1, c3).toInt(); int h = item.substring(c3+1, c4).toInt();
          // if c5 exists, parse minute and source
          int mi;
          String src = "manual";
          if (c5 != -1) {
              mi = item.substring(c4+1, c5).toInt();
              src = item.substring(c5+1);
          } else {
              mi = item.substring(c4+1).toInt();
          }
          onetimeSchedules[onetimeCount++] = {String(onetimeCount), y, mo, d, h, mi, false, src};
      }
    }
  }

  Serial.println("Schedule Updated via POST");
  
  // アプリから設定受信時: 2回素早く点滅
  // アプリから設定受信時: 2回素早く点滅させるフラグを立てる
  shouldBlink = true;
  
  // アプリがこれをパースして同期する
  server.send(200, "application/json", getScheduleJSON());
}

String getScheduleJSON() {
  String json = "{";
  json += "\"daily\":{\"enabled\":" + String(dailyWakeup.enabled ? "true" : "false") + ",";
  json += "\"hour\":" + String(dailyWakeup.hour) + ",\"minute\":" + String(dailyWakeup.minute) + "},";
  
  json += "\"weekly\":[";
  for(int i=0; i<weeklyCount; i++) {
    json += "{\"weekday\":" + String(weeklySchedules[i].weekday) + ",";
    json += "\"hour\":" + String(weeklySchedules[i].hour) + ",";
    json += "\"minute\":" + String(weeklySchedules[i].minute) + "}";
    if(i < weeklyCount-1) json += ",";
  }
  json += "],";
  
  json += "\"onetime\":[";
  for(int i=0; i<onetimeCount; i++) {
    json += "{\"year\":" + String(onetimeSchedules[i].year) + ",";
    json += "\"month\":" + String(onetimeSchedules[i].month) + ",";
    json += "\"day\":" + String(onetimeSchedules[i].day) + ",";
    json += "\"hour\":" + String(onetimeSchedules[i].hour) + ",";
    json += "\"minute\":" + String(onetimeSchedules[i].minute) + ",";
    json += "\"source\":\"" + onetimeSchedules[i].source + "\"}";
    if(i < onetimeCount-1) json += ",";
  }
  json += "]";
  json += "}";
  return json;
}

void handleGetSchedule() {
  server.send(200, "application/json", getScheduleJSON());
}

void handleForceWake() {
  sendWOL(targetMac);
  server.send(200, "text/plain", "Executed");
}

void handleWakeDesktop() {
  sendWOL(String(MAC_DESKTOP));
  server.send(200, "text/plain", "Executed");
}

void handleWakeServer() {
  sendWOL(String(MAC_SERVER));
  server.send(200, "text/plain", "Executed");
}

// ----------------------------------------------------------------
// Schedule Check
// ----------------------------------------------------------------
void checkSchedule() {
  time_t now = timeClient.getEpochTime();
  struct tm *ptm = gmtime(&now);
  int currentYear = ptm->tm_year + 1900;
  int currentMonth = ptm->tm_mon + 1;
  int currentDay = ptm->tm_mday;
  int currentHour = timeClient.getHours();
  int currentMinute = timeClient.getMinutes();
  int d = timeClient.getDay();
  int currentWeekday = (d==0)?6:(d-1); // 0=Sun -> 6, 1=Mon -> 0, etc.

  static int lastCheckedMinute = -1;
  if (currentMinute == lastCheckedMinute) return;
  lastCheckedMinute = currentMinute;

  bool wake = false;
  if (dailyWakeup.enabled && dailyWakeup.hour == currentHour && dailyWakeup.minute == currentMinute) wake = true;
  for (int i=0; i<weeklyCount; i++) {
    if (weeklySchedules[i].weekday==currentWeekday && weeklySchedules[i].hour==currentHour && weeklySchedules[i].minute==currentMinute) wake=true;
  }
  for (int i=0; i<onetimeCount; i++) {
    if (!onetimeSchedules[i].executed && onetimeSchedules[i].year==currentYear && onetimeSchedules[i].month==currentMonth && 
        onetimeSchedules[i].day==currentDay && onetimeSchedules[i].hour==currentHour && onetimeSchedules[i].minute==currentMinute) { 
      wake=true; onetimeSchedules[i].executed=true; 
    }
  }
  
  if (wake) sendWOL(targetMac);
}

// ----------------------------------------------------------------
// GAS Logic - LINEトリガー対応
// ----------------------------------------------------------------
String fetchGAS(String action) {
  WiFiClientSecure client;
  client.setInsecure();
  
  String url = String(GAS_URL_WOL);
  int domainStart = url.indexOf("//") + 2;
  int pathStart = url.indexOf("/", domainStart);
  String host = url.substring(domainStart, pathStart);
  String path = url.substring(pathStart) + "?action=" + action;
  
  if (!client.connect(host.c_str(), 443)) {
    Serial.println("GAS Connect failed");
    return "";
  }
  
  client.print(String("GET ") + path + " HTTP/1.1\r\n" +
               "Host: " + host + "\r\n" + 
               "Connection: close\r\n\r\n");
  
  // ヘッダー読み飛ばし
  while (client.connected()) {
    String line = client.readStringUntil('\n');
    if (line == "\r") break;
  }
  
  String body = client.readString();
  body.trim();
  client.stop();
  return body;
}

void checkGasTrigger() {
  Serial.println("Checking GAS...");
  
  // Step 1: ?action=signal でトリガー確認
  String signalResponse = fetchGAS("signal");
  Serial.print("Signal Response: ");
  Serial.println(signalResponse);
  
  if (signalResponse.indexOf("TRIGGER") != -1) {
    Serial.println("Trigger detected!");
    
    // Step 2: ?action=command でコマンド取得
    String command = fetchGAS("command");
    Serial.print("Command: ");
    Serial.println(command);
    
    // コマンドに応じてWoL送信
    if (command.indexOf("デスクトップPC起動") != -1) {
      Serial.println("Waking Desktop PC...");
      sendWOL(String(MAC_DESKTOP));
    } else if (command.indexOf("サーバーPC起動") != -1) {
      Serial.println("Waking Server PC...");
      sendWOL(String(MAC_SERVER));
    } else if (command.length() > 0) {
      // 不明なコマンドの場合はデフォルトMACを使用
      Serial.println("Unknown command, using default MAC...");
      sendWOL(targetMac);
    }
  } else {
    Serial.println("No trigger.");
  }
}

// ----------------------------------------------------------------
// SETUP & LOOP
// ----------------------------------------------------------------
void setup() {
  pinMode(LED_BUILTIN, OUTPUT);
  Serial.begin(115200);
  delay(1000);
  Serial.println("SmartPowerManager Pico W (GAS) v1.6.4");

  #ifdef MAC_DESKTOP
    targetMac = String(MAC_DESKTOP);
  #endif

  connectToWiFi();

  if (WiFi.status() == WL_CONNECTED) {
    timeClient.begin();
    timeClient.forceUpdate();
    
    // Webサーバーハンドラ登録
    server.on("/", handleRoot);
    server.on("/update_schedule", HTTP_POST, handleUpdateSchedule);
    server.on("/get_schedule", HTTP_GET, handleGetSchedule);
    server.on("/update_daily", HTTP_POST, handleUpdateDaily); // 追加
    server.on("/add_weekly", HTTP_POST, handleAddWeekly);
    server.on("/delete_weekly", HTTP_POST, handleDeleteWeekly);
    server.on("/add_onetime", HTTP_POST, handleAddOneTime);
    server.on("/delete_onetime", HTTP_POST, handleDeleteOneTime);
    
    server.on("/force_wake", handleForceWake);
    server.on("/wake_desktop", handleWakeDesktop);
    server.on("/wake_server", handleWakeServer);
    server.begin();
    Serial.println("HTTP server started");
  }
}

// ----------------------------------------------------------------
// Core 1 (Dual Core)
// ----------------------------------------------------------------
void setup1() {
  // Core 1 setup if needed
  delay(5000); // Wait for Core 0 to connect WiFi
}

void loop1() { // GAS Polling on Core 1
  if (WiFi.status() == WL_CONNECTED) {
    // GASポーリング
    if (millis() - lastGasPoll > GAS_POLL_INTERVAL) {
      checkGasTrigger();
      lastGasPoll = millis();
    }
  }
  delay(10);
}

void loop() {
  if (shouldBlink) {
    blinkLED(2, 100, 100);
    shouldBlink = false;
  }
  
  if (WiFi.status() == WL_CONNECTED) {
    server.handleClient();
    checkSchedule();
    
    // NTP更新
    static unsigned long lastNtpUpdate = 0;
    if (millis() - lastNtpUpdate > 60000) {
      timeClient.update();
      lastNtpUpdate = millis();
    }
    
    // GASポーリングはCore 1へ移動
    // if (millis() - lastGasPoll > GAS_POLL_INTERVAL) {
    //   checkGasTrigger();
    //   lastGasPoll = millis();
    // }
  } else {
    // 再接続試行
    static unsigned long lastReconnect = 0;
    if (millis() - lastReconnect > 30000) {
      Serial.println("Reconnecting WiFi...");
      connectToWiFi();
      lastReconnect = millis();
    }
  }
  
  delay(10); // CPU負荷軽減
}
