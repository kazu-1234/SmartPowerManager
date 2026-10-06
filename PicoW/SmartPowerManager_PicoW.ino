/*
 * SmartPowerManager Pico W 専用ファーム（push 用）
 * - Web UI / LINE / IR なし
 * - WiFi（固定 IP 可）+ NTP
 * - POST /update_schedule / GET /get_schedule（C# PicoSyncService 互換）
 * - EEPROM に MAC + 起動スケジュール + auto_wol（3分前）を永続化
 * - NTP 時刻に合わせて targetMac へ WoL
 *
 * v1.0.0
 */

#include <Arduino.h>
#include <WiFi.h>
#include <WebServer.h>
#include <WiFiUdp.h>
#include <time.h>
#include <string.h>
#include <stdio.h>
#include <stddef.h>
#include <EEPROM.h>
#include "config.h"

const char *const FIRMWARE_VERSION = "1.0.0";
const uint16_t HTTP_PORT = 80;
const unsigned long WIFI_CONNECT_TIMEOUT_MS = 30000;
const unsigned long TIME_SYNC_INTERVAL_MS = 4UL * 60UL * 1000UL;
const unsigned long TIME_NTP_WAIT_MS = 5000;
const char *const TIME_NTP_SERVER_FALLBACK = "pool.ntp.org";
const int WOL_PORT = 9;
const unsigned long WOL_LED_BLINK_MS = 166;
const int WOL_LED_BLINK_COUNT = 3;

#ifndef NTP_SERVER
#define NTP_SERVER "ntp.nict.jp"
#endif

// ----------------------------------------------------------------
// スケジュール（件数は EEPROM と共通）
// ----------------------------------------------------------------
const int MAX_WEEKLY = 10;
const int MAX_ONETIME = 10;
const int MAX_AUTO_WOL_WEEKLY = 21;
const int MAX_AUTO_WOL_ONETIME = 21;

struct ScheduleDaily
{
  bool enabled;
  int hour;
  int minute;
};

struct ScheduleWeekly
{
  int weekday; // 0=月 … 6=日
  int hour;
  int minute;
};

struct ScheduleOneTime
{
  int year;
  int month;
  int day;
  int hour;
  int minute;
  bool executed;
  char source[16];
};

// ----------------------------------------------------------------
// EEPROM（URC と同一フォーマット: magic SPM1）
// ----------------------------------------------------------------
const uint32_t EEPROM_MAGIC = 0x31504D53UL; // 'S''P''M''1' little-endian
const uint8_t EEPROM_VERSION = 1;
const size_t EEPROM_SIZE = 1024;

struct EepromBlob
{
  uint32_t magic;
  uint8_t version;
  uint8_t reserved[3];
  char targetMac[18];
  ScheduleDaily daily;
  uint8_t weeklyCount;
  ScheduleWeekly weekly[MAX_WEEKLY];
  uint8_t onetimeCount;
  ScheduleOneTime onetime[MAX_ONETIME];
  uint8_t autoWolWeeklyCount;
  ScheduleWeekly autoWolWeekly[MAX_AUTO_WOL_WEEKLY];
  uint8_t autoWolOnetimeCount;
  ScheduleOneTime autoWolOnetime[MAX_AUTO_WOL_ONETIME];
  uint32_t checksum;
};

static_assert(sizeof(EepromBlob) <= EEPROM_SIZE, "EepromBlob too large");

// ----------------------------------------------------------------
// グローバル
// ----------------------------------------------------------------
WebServer server(HTTP_PORT);
IPAddress staticIP(STATIC_IP_BYTES);
IPAddress gateway(GATEWAY_BYTES);
IPAddress subnet(SUBNET_BYTES);
IPAddress primaryDNS(PRIMARY_DNS_BYTES);

char targetMac[18] = "";
ScheduleDaily dailyWakeup = {false, 7, 0};
ScheduleWeekly weeklySchedules[MAX_WEEKLY];
int weeklyCount = 0;
ScheduleOneTime onetimeSchedules[MAX_ONETIME];
int onetimeCount = 0;
ScheduleWeekly autoWolWeekly[MAX_AUTO_WOL_WEEKLY];
int autoWolWeeklyCount = 0;
ScheduleOneTime autoWolOnetime[MAX_AUTO_WOL_ONETIME];
int autoWolOnetimeCount = 0;

bool g_timeOk = false;
unsigned long g_lastTimeSyncMs = 0;
bool shouldBlink = false;

// ----------------------------------------------------------------
// ユーティリティ
// ----------------------------------------------------------------
uint32_t calcChecksum(const EepromBlob &blob)
{
  const uint8_t *p = reinterpret_cast<const uint8_t *>(&blob);
  size_t n = offsetof(EepromBlob, checksum);
  uint32_t sum = 0xA5A5A5A5UL;
  for (size_t i = 0; i < n; i++)
  {
    sum = (sum * 33U) + p[i];
  }
  return sum;
}

void blinkLed(int times, unsigned long onMs)
{
  for (int i = 0; i < times; i++)
  {
    digitalWrite(LED_BUILTIN, HIGH);
    delay(onMs);
    digitalWrite(LED_BUILTIN, LOW);
    if (i + 1 < times)
    {
      delay(onMs);
    }
  }
}

bool parseMacAddress(const char *macStr, byte *macArray)
{
  if (macStr == nullptr || strlen(macStr) != 17)
  {
    return false;
  }
  int values[6];
  if (sscanf(macStr, "%x:%x:%x:%x:%x:%x",
             &values[0], &values[1], &values[2],
             &values[3], &values[4], &values[5]) != 6)
  {
    return false;
  }
  for (int i = 0; i < 6; i++)
  {
    macArray[i] = (byte)values[i];
  }
  return true;
}

void sendWakeOnLan(const char *macStr)
{
  byte mac[6];
  if (!parseMacAddress(macStr, mac))
  {
    Serial.println(F("[WoL] invalid or empty MAC"));
    return;
  }

  byte magicPacket[102];
  memset(magicPacket, 0xFF, 6);
  for (int i = 0; i < 16; i++)
  {
    memcpy(&magicPacket[6 + i * 6], mac, 6);
  }

  for (int attempt = 0; attempt < 3; attempt++)
  {
    WiFiUDP udp;
    if (udp.beginPacket(IPAddress(255, 255, 255, 255), WOL_PORT))
    {
      udp.write(magicPacket, sizeof(magicPacket));
      udp.endPacket();
    }
    udp.stop();
    if (attempt < 2)
    {
      delay(50);
    }
  }
  Serial.printf("[WoL] sent %s\n", macStr);
  blinkLed(WOL_LED_BLINK_COUNT, WOL_LED_BLINK_MS);
}

void setSource(char *dst, size_t dstSize, const String &src)
{
  strncpy(dst, src.c_str(), dstSize - 1);
  dst[dstSize - 1] = '\0';
}

// ----------------------------------------------------------------
// EEPROM
// ----------------------------------------------------------------
void saveToEeprom()
{
  EepromBlob blob;
  memset(&blob, 0, sizeof(blob));
  blob.magic = EEPROM_MAGIC;
  blob.version = EEPROM_VERSION;
  strncpy(blob.targetMac, targetMac, sizeof(blob.targetMac) - 1);
  blob.daily = dailyWakeup;

  blob.weeklyCount = (uint8_t)constrain(weeklyCount, 0, MAX_WEEKLY);
  for (int i = 0; i < blob.weeklyCount; i++)
  {
    blob.weekly[i] = weeklySchedules[i];
  }

  blob.onetimeCount = (uint8_t)constrain(onetimeCount, 0, MAX_ONETIME);
  for (int i = 0; i < blob.onetimeCount; i++)
  {
    blob.onetime[i] = onetimeSchedules[i];
  }

  blob.autoWolWeeklyCount = (uint8_t)constrain(autoWolWeeklyCount, 0, MAX_AUTO_WOL_WEEKLY);
  for (int i = 0; i < blob.autoWolWeeklyCount; i++)
  {
    blob.autoWolWeekly[i] = autoWolWeekly[i];
  }

  blob.autoWolOnetimeCount = (uint8_t)constrain(autoWolOnetimeCount, 0, MAX_AUTO_WOL_ONETIME);
  for (int i = 0; i < blob.autoWolOnetimeCount; i++)
  {
    blob.autoWolOnetime[i] = autoWolOnetime[i];
  }

  blob.checksum = calcChecksum(blob);
  EEPROM.put(0, blob);
  if (!EEPROM.commit())
  {
    Serial.println(F("[EEPROM] commit failed"));
  }
  else if (DEBUG)
  {
    Serial.println(F("[EEPROM] saved"));
  }
}

bool loadFromEeprom()
{
  EepromBlob blob;
  EEPROM.get(0, blob);
  if (blob.magic != EEPROM_MAGIC || blob.version != EEPROM_VERSION)
  {
    Serial.println(F("[EEPROM] empty or incompatible"));
    return false;
  }
  if (blob.checksum != calcChecksum(blob))
  {
    Serial.println(F("[EEPROM] checksum mismatch"));
    return false;
  }

  strncpy(targetMac, blob.targetMac, sizeof(targetMac) - 1);
  targetMac[sizeof(targetMac) - 1] = '\0';
  dailyWakeup = blob.daily;

  weeklyCount = constrain((int)blob.weeklyCount, 0, MAX_WEEKLY);
  for (int i = 0; i < weeklyCount; i++)
  {
    weeklySchedules[i] = blob.weekly[i];
  }

  onetimeCount = constrain((int)blob.onetimeCount, 0, MAX_ONETIME);
  for (int i = 0; i < onetimeCount; i++)
  {
    onetimeSchedules[i] = blob.onetime[i];
  }

  autoWolWeeklyCount = constrain((int)blob.autoWolWeeklyCount, 0, MAX_AUTO_WOL_WEEKLY);
  for (int i = 0; i < autoWolWeeklyCount; i++)
  {
    autoWolWeekly[i] = blob.autoWolWeekly[i];
  }

  autoWolOnetimeCount = constrain((int)blob.autoWolOnetimeCount, 0, MAX_AUTO_WOL_ONETIME);
  for (int i = 0; i < autoWolOnetimeCount; i++)
  {
    autoWolOnetime[i] = blob.autoWolOnetime[i];
  }

  Serial.printf("[EEPROM] loaded mac=%s weekly=%d onetime=%d autoW=%d autoO=%d\n",
                targetMac, weeklyCount, onetimeCount, autoWolWeeklyCount, autoWolOnetimeCount);
  return true;
}

// ----------------------------------------------------------------
// WiFi / NTP
// ----------------------------------------------------------------
void connectToWiFi()
{
  Serial.println(F("Connecting to WiFi..."));
  if (USE_STATIC_IP)
  {
    WiFi.config(staticIP, primaryDNS, gateway, subnet);
    Serial.printf("Static IP %s\n", staticIP.toString().c_str());
  }

  int bestNetwork = -1;
  int maxRssi = -1000;
  int n = WiFi.scanNetworks();
  for (int i = 0; i < n; i++)
  {
    String ssid = WiFi.SSID(i);
    for (int j = 0; j < numWifiCredentials; j++)
    {
      if (ssid == wifiCredentials[j].ssid)
      {
        int rssi = WiFi.RSSI(i);
        if (rssi > maxRssi)
        {
          maxRssi = rssi;
          bestNetwork = j;
        }
      }
    }
  }
  WiFi.scanDelete();

  if (bestNetwork < 0)
  {
    Serial.println(F("No known SSID found"));
    return;
  }

  WiFi.begin(wifiCredentials[bestNetwork].ssid, wifiCredentials[bestNetwork].password);
  unsigned long start = millis();
  while (WiFi.status() != WL_CONNECTED && (millis() - start) < WIFI_CONNECT_TIMEOUT_MS)
  {
    delay(50);
    digitalWrite(LED_BUILTIN, (millis() / 500) % 2);
  }
  digitalWrite(LED_BUILTIN, LOW);

  if (WiFi.status() == WL_CONNECTED)
  {
    Serial.printf("WiFi OK %s\n", WiFi.localIP().toString().c_str());
  }
  else
  {
    Serial.println(F("WiFi failed"));
  }
}

bool syncTimeOnce()
{
  configTime(NTP_GMT_OFFSET_SEC, 0, NTP_SERVER, TIME_NTP_SERVER_FALLBACK);
  unsigned long start = millis();
  while ((millis() - start) < TIME_NTP_WAIT_MS)
  {
    time_t now = time(nullptr);
    if (now > 1700000000)
    {
      g_timeOk = true;
      g_lastTimeSyncMs = millis();
      struct tm tmNow;
      localtime_r(&now, &tmNow);
      Serial.printf("[Time] OK %04d-%02d-%02d %02d:%02d:%02d\n",
                    tmNow.tm_year + 1900, tmNow.tm_mon + 1, tmNow.tm_mday,
                    tmNow.tm_hour, tmNow.tm_min, tmNow.tm_sec);
      return true;
    }
    delay(100);
  }
  Serial.println(F("[Time] NTP failed"));
  return false;
}

void serviceTime()
{
  if (WiFi.status() != WL_CONNECTED)
  {
    return;
  }
  if (g_lastTimeSyncMs == 0 || (millis() - g_lastTimeSyncMs) >= TIME_SYNC_INTERVAL_MS)
  {
    syncTimeOnce();
  }
}

// ----------------------------------------------------------------
// JSON / HTTP
// ----------------------------------------------------------------
String getScheduleJSON()
{
  String json = "{";
  json += "\"daily\":{\"enabled\":";
  json += dailyWakeup.enabled ? "true" : "false";
  json += ",\"hour\":";
  json += String(dailyWakeup.hour);
  json += ",\"minute\":";
  json += String(dailyWakeup.minute);
  json += "},\"weekly\":[";
  for (int i = 0; i < weeklyCount; i++)
  {
    if (i > 0)
    {
      json += ",";
    }
    json += "{\"weekday\":";
    json += String(weeklySchedules[i].weekday);
    json += ",\"hour\":";
    json += String(weeklySchedules[i].hour);
    json += ",\"minute\":";
    json += String(weeklySchedules[i].minute);
    json += "}";
  }
  json += "],\"onetime\":[";
  for (int i = 0; i < onetimeCount; i++)
  {
    if (i > 0)
    {
      json += ",";
    }
    json += "{\"year\":";
    json += String(onetimeSchedules[i].year);
    json += ",\"month\":";
    json += String(onetimeSchedules[i].month);
    json += ",\"day\":";
    json += String(onetimeSchedules[i].day);
    json += ",\"hour\":";
    json += String(onetimeSchedules[i].hour);
    json += ",\"minute\":";
    json += String(onetimeSchedules[i].minute);
    json += ",\"source\":\"";
    json += onetimeSchedules[i].source;
    json += "\"}";
  }
  json += "]}";
  return json;
}

void handleGetSchedule()
{
  server.send(200, "application/json", getScheduleJSON());
}

void handleUpdateSchedule()
{
  if (server.hasArg("mac"))
  {
    String mac = server.arg("mac");
    mac.trim();
    if (mac.length() == 17)
    {
      strncpy(targetMac, mac.c_str(), sizeof(targetMac) - 1);
      targetMac[sizeof(targetMac) - 1] = '\0';
    }
  }

  if (server.hasArg("d_en"))
  {
    dailyWakeup.enabled = (server.arg("d_en").toInt() == 1);
    if (server.hasArg("d_h"))
    {
      dailyWakeup.hour = server.arg("d_h").toInt();
    }
    if (server.hasArg("d_m"))
    {
      dailyWakeup.minute = server.arg("d_m").toInt();
    }
  }

  if (server.hasArg("weekly"))
  {
    String wStr = server.arg("weekly");
    weeklyCount = 0;
    while (wStr.length() > 0 && weeklyCount < MAX_WEEKLY)
    {
      int semi = wStr.indexOf(';');
      String item = (semi == -1) ? wStr : wStr.substring(0, semi);
      wStr = (semi == -1) ? "" : wStr.substring(semi + 1);
      int c1 = item.indexOf(',');
      int c2 = item.lastIndexOf(',');
      if (c1 != -1 && c2 != -1 && c1 != c2)
      {
        weeklySchedules[weeklyCount].weekday = item.substring(0, c1).toInt();
        weeklySchedules[weeklyCount].hour = item.substring(c1 + 1, c2).toInt();
        weeklySchedules[weeklyCount].minute = item.substring(c2 + 1).toInt();
        weeklyCount++;
      }
    }
  }

  if (server.hasArg("onetime"))
  {
    String oStr = server.arg("onetime");
    onetimeCount = 0;
    while (oStr.length() > 0 && onetimeCount < MAX_ONETIME)
    {
      int semi = oStr.indexOf(';');
      String item = (semi == -1) ? oStr : oStr.substring(0, semi);
      oStr = (semi == -1) ? "" : oStr.substring(semi + 1);
      int c1 = item.indexOf(',');
      int c2 = item.indexOf(',', c1 + 1);
      int c3 = item.indexOf(',', c2 + 1);
      int c4 = item.indexOf(',', c3 + 1);
      int c5 = item.indexOf(',', c4 + 1);
      if (c4 != -1)
      {
        ScheduleOneTime &ot = onetimeSchedules[onetimeCount];
        ot.year = item.substring(0, c1).toInt();
        ot.month = item.substring(c1 + 1, c2).toInt();
        ot.day = item.substring(c2 + 1, c3).toInt();
        ot.hour = item.substring(c3 + 1, c4).toInt();
        ot.executed = false;
        if (c5 != -1)
        {
          ot.minute = item.substring(c4 + 1, c5).toInt();
          setSource(ot.source, sizeof(ot.source), item.substring(c5 + 1));
        }
        else
        {
          ot.minute = item.substring(c4 + 1).toInt();
          setSource(ot.source, sizeof(ot.source), String("manual"));
        }
        onetimeCount++;
      }
    }
  }

  if (server.hasArg("auto_wol_weekly"))
  {
    String wStr = server.arg("auto_wol_weekly");
    autoWolWeeklyCount = 0;
    while (wStr.length() > 0 && autoWolWeeklyCount < MAX_AUTO_WOL_WEEKLY)
    {
      int semi = wStr.indexOf(';');
      String item = (semi == -1) ? wStr : wStr.substring(0, semi);
      wStr = (semi == -1) ? "" : wStr.substring(semi + 1);
      int c1 = item.indexOf(',');
      int c2 = item.lastIndexOf(',');
      if (c1 != -1 && c2 != -1 && c1 != c2)
      {
        autoWolWeekly[autoWolWeeklyCount].weekday = item.substring(0, c1).toInt();
        autoWolWeekly[autoWolWeeklyCount].hour = item.substring(c1 + 1, c2).toInt();
        autoWolWeekly[autoWolWeeklyCount].minute = item.substring(c2 + 1).toInt();
        autoWolWeeklyCount++;
      }
    }
  }

  if (server.hasArg("auto_wol_onetime"))
  {
    String oStr = server.arg("auto_wol_onetime");
    autoWolOnetimeCount = 0;
    while (oStr.length() > 0 && autoWolOnetimeCount < MAX_AUTO_WOL_ONETIME)
    {
      int semi = oStr.indexOf(';');
      String item = (semi == -1) ? oStr : oStr.substring(0, semi);
      oStr = (semi == -1) ? "" : oStr.substring(semi + 1);
      int c1 = item.indexOf(',');
      int c2 = item.indexOf(',', c1 + 1);
      int c3 = item.indexOf(',', c2 + 1);
      int c4 = item.indexOf(',', c3 + 1);
      int c5 = item.indexOf(',', c4 + 1);
      if (c4 != -1)
      {
        ScheduleOneTime &ot = autoWolOnetime[autoWolOnetimeCount];
        ot.year = item.substring(0, c1).toInt();
        ot.month = item.substring(c1 + 1, c2).toInt();
        ot.day = item.substring(c2 + 1, c3).toInt();
        ot.hour = item.substring(c3 + 1, c4).toInt();
        ot.executed = false;
        if (c5 != -1)
        {
          ot.minute = item.substring(c4 + 1, c5).toInt();
          setSource(ot.source, sizeof(ot.source), item.substring(c5 + 1));
        }
        else
        {
          ot.minute = item.substring(c4 + 1).toInt();
          setSource(ot.source, sizeof(ot.source), String("auto_wol"));
        }
        autoWolOnetimeCount++;
      }
    }
  }

  saveToEeprom();
  shouldBlink = true;
  Serial.println(F("Schedule Updated via POST"));
  server.send(200, "application/json", getScheduleJSON());
}

void handleNotFound()
{
  server.send(404, "text/plain", "Not Found");
}

// ----------------------------------------------------------------
// スケジュール実行
// ----------------------------------------------------------------
void checkSchedule()
{
  if (!g_timeOk)
  {
    return;
  }

  time_t nowEpoch = time(nullptr);
  struct tm tmNow;
  localtime_r(&nowEpoch, &tmNow);

  int currentYear = tmNow.tm_year + 1900;
  int currentMonth = tmNow.tm_mon + 1;
  int currentDay = tmNow.tm_mday;
  int currentHour = tmNow.tm_hour;
  int currentMinute = tmNow.tm_min;
  // tm_wday: 0=日 … 6=土 → SPM: 0=月 … 6=日
  int currentWeekday = (tmNow.tm_wday == 0) ? 6 : (tmNow.tm_wday - 1);

  static int lastCheckedMinute = -1;
  if (currentMinute == lastCheckedMinute)
  {
    return;
  }
  lastCheckedMinute = currentMinute;

  bool wake = false;
  bool needSave = false;

  if (dailyWakeup.enabled && dailyWakeup.hour == currentHour && dailyWakeup.minute == currentMinute)
  {
    wake = true;
  }
  for (int i = 0; i < weeklyCount; i++)
  {
    if (weeklySchedules[i].weekday == currentWeekday &&
        weeklySchedules[i].hour == currentHour &&
        weeklySchedules[i].minute == currentMinute)
    {
      wake = true;
    }
  }
  for (int i = 0; i < onetimeCount; i++)
  {
    if (!onetimeSchedules[i].executed &&
        onetimeSchedules[i].year == currentYear &&
        onetimeSchedules[i].month == currentMonth &&
        onetimeSchedules[i].day == currentDay &&
        onetimeSchedules[i].hour == currentHour &&
        onetimeSchedules[i].minute == currentMinute)
    {
      wake = true;
      onetimeSchedules[i].executed = true;
      needSave = true;
    }
  }
  for (int i = 0; i < autoWolWeeklyCount; i++)
  {
    if (autoWolWeekly[i].weekday == currentWeekday &&
        autoWolWeekly[i].hour == currentHour &&
        autoWolWeekly[i].minute == currentMinute)
    {
      wake = true;
    }
  }
  for (int i = 0; i < autoWolOnetimeCount; i++)
  {
    if (!autoWolOnetime[i].executed &&
        autoWolOnetime[i].year == currentYear &&
        autoWolOnetime[i].month == currentMonth &&
        autoWolOnetime[i].day == currentDay &&
        autoWolOnetime[i].hour == currentHour &&
        autoWolOnetime[i].minute == currentMinute)
    {
      wake = true;
      autoWolOnetime[i].executed = true;
      needSave = true;
    }
  }

  if (needSave)
  {
    saveToEeprom();
  }
  if (wake)
  {
    sendWakeOnLan(targetMac);
  }
}

// ----------------------------------------------------------------
void setup()
{
  pinMode(LED_BUILTIN, OUTPUT);
  Serial.begin(115200);
  delay(500);
  Serial.printf("SmartPowerManager PicoW v%s (no WebUI)\n", FIRMWARE_VERSION);

  EEPROM.begin(EEPROM_SIZE);
  loadFromEeprom();

  connectToWiFi();
  if (WiFi.status() == WL_CONNECTED)
  {
    syncTimeOnce();
  }

  server.on("/update_schedule", HTTP_POST, handleUpdateSchedule);
  server.on("/get_schedule", HTTP_GET, handleGetSchedule);
  server.onNotFound(handleNotFound);
  server.begin();
  Serial.println(F("HTTP API: POST /update_schedule, GET /get_schedule"));
}

void loop()
{
  if (WiFi.status() != WL_CONNECTED)
  {
    connectToWiFi();
    delay(1000);
    return;
  }

  server.handleClient();
  serviceTime();
  checkSchedule();

  if (shouldBlink)
  {
    shouldBlink = false;
    blinkLed(2, 80);
  }
}
