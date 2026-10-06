/*
 * SmartPowerManager Pico W 専用ファーム（push 用）
 * - スケジュール一覧 Web（/ /schedule）
 * - POST /update_schedule / GET /get_schedule
 * - EEPROM 永続 + NTP 時刻 WoL
 * - LINE / IR なし
 *
 * v1.1.0
 */

#include <Arduino.h>
#include <WiFi.h>
#include <WebServer.h>
#include <WiFiUdp.h>
#include <time.h>
#include "config.h"
#include "spm_schedule.h"
#include "schedule_ui.h"

const char *const FIRMWARE_VERSION = "1.1.0";
const uint16_t HTTP_PORT = 80;
const unsigned long WIFI_CONNECT_TIMEOUT_MS = 30000;
const unsigned long TIME_SYNC_INTERVAL_MS = 4UL * 60UL * 1000UL;
const unsigned long TIME_NTP_WAIT_MS = 5000;
const char *const TIME_NTP_SERVER_FALLBACK = "pool.ntp.org";
const int WOL_PORT = 9;
const unsigned long WOL_LED_BLINK_MS = 166;
const int WOL_LED_BLINK_COUNT = 3;
const size_t SCHEDULE_JSON_MAX = 2048;

#ifndef NTP_GMT_OFFSET_SEC
#define NTP_GMT_OFFSET_SEC (9 * 3600)
#endif

WebServer server(HTTP_PORT);
IPAddress staticIP(STATIC_IP_BYTES);
IPAddress gateway(GATEWAY_BYTES);
IPAddress subnet(SUBNET_BYTES);
IPAddress primaryDNS(PRIMARY_DNS_BYTES);

bool g_timeOk = false;
unsigned long g_lastTimeSyncMs = 0;
bool shouldBlink = false;

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

void handleSchedulePage()
{
  server.send(200, "text/html; charset=utf-8", scheduleUiHtmlBody(FIRMWARE_VERSION));
}

void handleGetSchedule()
{
  char json[SCHEDULE_JSON_MAX];
  spmBuildScheduleJson(json, sizeof(json), g_timeOk);
  server.send(200, "application/json", json);
}

void handleUpdateSchedule()
{
  // WebServer がデコード済みの引数を form 文字列に再構成
  String body;
  for (int i = 0; i < server.args(); i++)
  {
    if (i > 0)
    {
      body += '&';
    }
    body += server.argName(i);
    body += '=';
    body += server.arg(i);
  }
  spmApplyUpdateBody(body.c_str());
  shouldBlink = true;
  Serial.println(F("Schedule Updated via POST"));
  char json[SCHEDULE_JSON_MAX];
  spmBuildScheduleJson(json, sizeof(json), g_timeOk);
  server.send(200, "application/json", json);
}

void handleNotFound()
{
  server.send(404, "text/plain", "Not Found");
}

void setup()
{
  pinMode(LED_BUILTIN, OUTPUT);
  Serial.begin(115200);
  delay(500);
  Serial.printf("SmartPowerManager PicoW v%s\n", FIRMWARE_VERSION);

  spmBeginEeprom();
  connectToWiFi();
  if (WiFi.status() == WL_CONNECTED)
  {
    syncTimeOnce();
  }

  server.on("/", HTTP_GET, handleSchedulePage);
  server.on("/schedule", HTTP_GET, handleSchedulePage);
  server.on("/update_schedule", HTTP_POST, handleUpdateSchedule);
  server.on("/get_schedule", HTTP_GET, handleGetSchedule);
  server.onNotFound(handleNotFound);
  server.begin();
  Serial.println(F("HTTP: / /schedule /get_schedule POST /update_schedule"));
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
  spmCheckSchedule(g_timeOk, sendWakeOnLan);

  if (shouldBlink)
  {
    shouldBlink = false;
    blinkLed(2, 80);
  }
}
