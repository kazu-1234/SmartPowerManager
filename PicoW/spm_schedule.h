#pragma once
// SPM 互換: MAC + 起動/auto_wol + 表示用 rules + EEPROM（magic SPM1, v2）

#include <Arduino.h>
#include <EEPROM.h>
#include <string.h>
#include <stdio.h>
#include <stddef.h>
#include <time.h>
#include <stdlib.h>

const int SPM_MAX_WEEKLY = 10;
const int SPM_MAX_ONETIME = 10;
const int SPM_MAX_AUTO_WOL_WEEKLY = 21;
const int SPM_MAX_AUTO_WOL_ONETIME = 21;
const int SPM_RULES_MAX = 384;
const uint32_t SPM_EEPROM_MAGIC = 0x31504D53UL; // 'SPM1'
const uint8_t SPM_EEPROM_VERSION = 2;
const size_t SPM_EEPROM_SIZE = 2048;

struct SpmScheduleDaily
{
    bool enabled;
    int hour;
    int minute;
};

struct SpmScheduleWeekly
{
    int weekday;
    int hour;
    int minute;
};

struct SpmScheduleOneTime
{
    int year;
    int month;
    int day;
    int hour;
    int minute;
    bool executed;
    char source[16];
};

struct SpmEepromBlob
{
    uint32_t magic;
    uint8_t version;
    uint8_t reserved[3];
    char targetMac[18];
    SpmScheduleDaily daily;
    uint8_t weeklyCount;
    SpmScheduleWeekly weekly[SPM_MAX_WEEKLY];
    uint8_t onetimeCount;
    SpmScheduleOneTime onetime[SPM_MAX_ONETIME];
    uint8_t autoWolWeeklyCount;
    SpmScheduleWeekly autoWolWeekly[SPM_MAX_AUTO_WOL_WEEKLY];
    uint8_t autoWolOnetimeCount;
    SpmScheduleOneTime autoWolOnetime[SPM_MAX_AUTO_WOL_ONETIME];
    char autoWolRules[SPM_RULES_MAX];
    uint32_t checksum;
};

static_assert(sizeof(SpmEepromBlob) <= SPM_EEPROM_SIZE, "SpmEepromBlob too large");

inline char g_spmTargetMac[18] = "";
inline SpmScheduleDaily g_spmDaily = {false, 7, 0};
inline SpmScheduleWeekly g_spmWeekly[SPM_MAX_WEEKLY];
inline int g_spmWeeklyCount = 0;
inline SpmScheduleOneTime g_spmOnetime[SPM_MAX_ONETIME];
inline int g_spmOnetimeCount = 0;
inline SpmScheduleWeekly g_spmAutoWolWeekly[SPM_MAX_AUTO_WOL_WEEKLY];
inline int g_spmAutoWolWeeklyCount = 0;
inline SpmScheduleOneTime g_spmAutoWolOnetime[SPM_MAX_AUTO_WOL_ONETIME];
inline int g_spmAutoWolOnetimeCount = 0;
inline char g_spmAutoWolRules[SPM_RULES_MAX] = "";
inline bool g_spmEepromReady = false;

inline uint32_t spmCalcChecksum(const SpmEepromBlob &blob)
{
    const uint8_t *p = reinterpret_cast<const uint8_t *>(&blob);
    size_t n = offsetof(SpmEepromBlob, checksum);
    uint32_t sum = 0xA5A5A5A5UL;
    for (size_t i = 0; i < n; i++)
    {
        sum = (sum * 33U) + p[i];
    }
    return sum;
}

inline void spmSetSource(char *dst, size_t dstSize, const char *src)
{
    if (src == nullptr)
    {
        src = "";
    }
    strncpy(dst, src, dstSize - 1);
    dst[dstSize - 1] = '\0';
}

inline void spmSaveEeprom()
{
    if (!g_spmEepromReady)
    {
        return;
    }
    SpmEepromBlob blob;
    memset(&blob, 0, sizeof(blob));
    blob.magic = SPM_EEPROM_MAGIC;
    blob.version = SPM_EEPROM_VERSION;
    strncpy(blob.targetMac, g_spmTargetMac, sizeof(blob.targetMac) - 1);
    blob.daily = g_spmDaily;
    blob.weeklyCount = (uint8_t)constrain(g_spmWeeklyCount, 0, SPM_MAX_WEEKLY);
    for (int i = 0; i < blob.weeklyCount; i++)
    {
        blob.weekly[i] = g_spmWeekly[i];
    }
    blob.onetimeCount = (uint8_t)constrain(g_spmOnetimeCount, 0, SPM_MAX_ONETIME);
    for (int i = 0; i < blob.onetimeCount; i++)
    {
        blob.onetime[i] = g_spmOnetime[i];
    }
    blob.autoWolWeeklyCount = (uint8_t)constrain(g_spmAutoWolWeeklyCount, 0, SPM_MAX_AUTO_WOL_WEEKLY);
    for (int i = 0; i < blob.autoWolWeeklyCount; i++)
    {
        blob.autoWolWeekly[i] = g_spmAutoWolWeekly[i];
    }
    blob.autoWolOnetimeCount = (uint8_t)constrain(g_spmAutoWolOnetimeCount, 0, SPM_MAX_AUTO_WOL_ONETIME);
    for (int i = 0; i < blob.autoWolOnetimeCount; i++)
    {
        blob.autoWolOnetime[i] = g_spmAutoWolOnetime[i];
    }
    strncpy(blob.autoWolRules, g_spmAutoWolRules, sizeof(blob.autoWolRules) - 1);
    blob.checksum = spmCalcChecksum(blob);
    EEPROM.put(0, blob);
    EEPROM.commit();
}

inline bool spmLoadEeprom()
{
    SpmEepromBlob blob;
    EEPROM.get(0, blob);
    if (blob.magic != SPM_EEPROM_MAGIC)
    {
        return false;
    }
    if (blob.version != 1 && blob.version != SPM_EEPROM_VERSION)
    {
        return false;
    }
    // v1 は rules 無し・checksum 位置が異なるため、v1 はフィールド先頭だけ採用
    if (blob.version == SPM_EEPROM_VERSION && blob.checksum != spmCalcChecksum(blob))
    {
        return false;
    }

    strncpy(g_spmTargetMac, blob.targetMac, sizeof(g_spmTargetMac) - 1);
    g_spmTargetMac[sizeof(g_spmTargetMac) - 1] = '\0';
    g_spmDaily = blob.daily;
    g_spmWeeklyCount = constrain((int)blob.weeklyCount, 0, SPM_MAX_WEEKLY);
    for (int i = 0; i < g_spmWeeklyCount; i++)
    {
        g_spmWeekly[i] = blob.weekly[i];
    }
    g_spmOnetimeCount = constrain((int)blob.onetimeCount, 0, SPM_MAX_ONETIME);
    for (int i = 0; i < g_spmOnetimeCount; i++)
    {
        g_spmOnetime[i] = blob.onetime[i];
    }
    g_spmAutoWolWeeklyCount = constrain((int)blob.autoWolWeeklyCount, 0, SPM_MAX_AUTO_WOL_WEEKLY);
    for (int i = 0; i < g_spmAutoWolWeeklyCount; i++)
    {
        g_spmAutoWolWeekly[i] = blob.autoWolWeekly[i];
    }
    g_spmAutoWolOnetimeCount = constrain((int)blob.autoWolOnetimeCount, 0, SPM_MAX_AUTO_WOL_ONETIME);
    for (int i = 0; i < g_spmAutoWolOnetimeCount; i++)
    {
        g_spmAutoWolOnetime[i] = blob.autoWolOnetime[i];
    }
    if (blob.version >= 2)
    {
        strncpy(g_spmAutoWolRules, blob.autoWolRules, sizeof(g_spmAutoWolRules) - 1);
        g_spmAutoWolRules[sizeof(g_spmAutoWolRules) - 1] = '\0';
    }
    else
    {
        g_spmAutoWolRules[0] = '\0';
    }
    return true;
}

inline void spmBeginEeprom()
{
    EEPROM.begin(SPM_EEPROM_SIZE);
    g_spmEepromReady = true;
    if (spmLoadEeprom())
    {
        Serial.printf("[SPM] EEPROM loaded mac=%s\n", g_spmTargetMac);
    }
    else
    {
        Serial.println(F("[SPM] EEPROM empty"));
    }
}

inline bool spmFormGet(const char *body, const char *key, char *out, size_t outSize)
{
    if (body == nullptr || key == nullptr || out == nullptr || outSize == 0)
    {
        return false;
    }
    out[0] = '\0';
    size_t keyLen = strlen(key);
    const char *p = body;
    while (*p)
    {
        if ((p == body || *(p - 1) == '&') && strncmp(p, key, keyLen) == 0 && p[keyLen] == '=')
        {
            p += keyLen + 1;
            size_t oi = 0;
            while (*p && *p != '&' && oi + 1 < outSize)
            {
                char c = *p++;
                if (c == '+')
                {
                    c = ' ';
                }
                else if (c == '%' && p[0] && p[1])
                {
                    char hex[3] = {p[0], p[1], 0};
                    c = (char)strtol(hex, nullptr, 16);
                    p += 2;
                }
                out[oi++] = c;
            }
            out[oi] = '\0';
            return true;
        }
        p++;
    }
    return false;
}

inline void spmParseWeeklyList(const char *wStr, SpmScheduleWeekly *arr, int *count, int maxCount)
{
    *count = 0;
    if (wStr == nullptr || wStr[0] == '\0')
    {
        return;
    }
    char buf[512];
    strncpy(buf, wStr, sizeof(buf) - 1);
    buf[sizeof(buf) - 1] = '\0';
    char *save = nullptr;
    char *item = strtok_r(buf, ";", &save);
    while (item && *count < maxCount)
    {
        int d = 0, h = 0, m = 0;
        if (sscanf(item, "%d,%d,%d", &d, &h, &m) == 3)
        {
            arr[*count].weekday = d;
            arr[*count].hour = h;
            arr[*count].minute = m;
            (*count)++;
        }
        item = strtok_r(nullptr, ";", &save);
    }
}

inline void spmParseOnetimeList(const char *oStr, SpmScheduleOneTime *arr, int *count, int maxCount, const char *defaultSource)
{
    *count = 0;
    if (oStr == nullptr || oStr[0] == '\0')
    {
        return;
    }
    char buf[768];
    strncpy(buf, oStr, sizeof(buf) - 1);
    buf[sizeof(buf) - 1] = '\0';
    char *save = nullptr;
    char *item = strtok_r(buf, ";", &save);
    while (item && *count < maxCount)
    {
        int y = 0, mo = 0, d = 0, h = 0, mi = 0;
        char src[16] = "";
        int n = sscanf(item, "%d,%d,%d,%d,%d,%15s", &y, &mo, &d, &h, &mi, src);
        if (n >= 5)
        {
            SpmScheduleOneTime &ot = arr[*count];
            ot.year = y;
            ot.month = mo;
            ot.day = d;
            ot.hour = h;
            ot.minute = mi;
            ot.executed = false;
            spmSetSource(ot.source, sizeof(ot.source), (n >= 6 && src[0]) ? src : defaultSource);
            (*count)++;
        }
        item = strtok_r(nullptr, ";", &save);
    }
}

inline void spmApplyUpdateBody(const char *body)
{
    char val[768];
    if (spmFormGet(body, "mac", val, sizeof(val)) && strlen(val) == 17)
    {
        strncpy(g_spmTargetMac, val, sizeof(g_spmTargetMac) - 1);
        g_spmTargetMac[sizeof(g_spmTargetMac) - 1] = '\0';
    }
    if (spmFormGet(body, "d_en", val, sizeof(val)))
    {
        g_spmDaily.enabled = (atoi(val) == 1);
        if (spmFormGet(body, "d_h", val, sizeof(val)))
        {
            g_spmDaily.hour = atoi(val);
        }
        if (spmFormGet(body, "d_m", val, sizeof(val)))
        {
            g_spmDaily.minute = atoi(val);
        }
    }
    if (spmFormGet(body, "weekly", val, sizeof(val)))
    {
        spmParseWeeklyList(val, g_spmWeekly, &g_spmWeeklyCount, SPM_MAX_WEEKLY);
    }
    if (spmFormGet(body, "onetime", val, sizeof(val)))
    {
        spmParseOnetimeList(val, g_spmOnetime, &g_spmOnetimeCount, SPM_MAX_ONETIME, "manual");
    }
    if (spmFormGet(body, "auto_wol_weekly", val, sizeof(val)))
    {
        spmParseWeeklyList(val, g_spmAutoWolWeekly, &g_spmAutoWolWeeklyCount, SPM_MAX_AUTO_WOL_WEEKLY);
    }
    if (spmFormGet(body, "auto_wol_onetime", val, sizeof(val)))
    {
        spmParseOnetimeList(val, g_spmAutoWolOnetime, &g_spmAutoWolOnetimeCount, SPM_MAX_AUTO_WOL_ONETIME, "auto_wol");
    }
    if (spmFormGet(body, "auto_wol_rules", val, sizeof(val)))
    {
        strncpy(g_spmAutoWolRules, val, sizeof(g_spmAutoWolRules) - 1);
        g_spmAutoWolRules[sizeof(g_spmAutoWolRules) - 1] = '\0';
    }
    spmSaveEeprom();
}

// 本処理時刻 → WoL 時刻（−3分）。曜日は 0=月…6=日。戻り値 true で out に次の WoL local time
inline bool spmNextWolFromDaily(int actH, int actM, time_t nowEpoch, struct tm *outWol)
{
    struct tm base;
    localtime_r(&nowEpoch, &base);
    int wolMin = actH * 60 + actM - 3;
    if (wolMin < 0)
    {
        wolMin += 24 * 60;
    }
    int wolH = wolMin / 60;
    int wolM = wolMin % 60;

    struct tm cand = base;
    cand.tm_hour = wolH;
    cand.tm_min = wolM;
    cand.tm_sec = 0;
    time_t t = mktime(&cand);
    if (t <= nowEpoch)
    {
        cand.tm_mday += 1;
        t = mktime(&cand);
    }
    if (t < 0)
    {
        return false;
    }
    localtime_r(&t, outWol);
    return true;
}

inline bool spmNextWolFromWeekly(int weekday, int actH, int actM, time_t nowEpoch, struct tm *outWol)
{
    int wolWd = weekday;
    int wolMin = actH * 60 + actM - 3;
    if (wolMin < 0)
    {
        wolMin += 24 * 60;
        wolWd = (wolWd - 1 + 7) % 7;
    }
    int wolH = wolMin / 60;
    int wolM = wolMin % 60;

    struct tm nowTm;
    localtime_r(&nowEpoch, &nowTm);
    int nowWd = (nowTm.tm_wday == 0) ? 6 : (nowTm.tm_wday - 1);

    for (int add = 0; add < 8; add++)
    {
        int d = (nowWd + add) % 7;
        if (d != wolWd)
        {
            continue;
        }
        struct tm cand = nowTm;
        cand.tm_mday += add;
        cand.tm_hour = wolH;
        cand.tm_min = wolM;
        cand.tm_sec = 0;
        time_t t = mktime(&cand);
        if (t > nowEpoch)
        {
            localtime_r(&t, outWol);
            return true;
        }
    }
    return false;
}

inline bool spmNextWolFromOnetime(int y, int mo, int d, int actH, int actM, time_t nowEpoch, struct tm *outWol)
{
    struct tm act = {};
    act.tm_year = y - 1900;
    act.tm_mon = mo - 1;
    act.tm_mday = d;
    act.tm_hour = actH;
    act.tm_min = actM;
    act.tm_sec = 0;
    time_t actT = mktime(&act);
    if (actT < 0)
    {
        return false;
    }
    time_t wolT = actT - 180;
    if (wolT <= nowEpoch)
    {
        return false;
    }
    localtime_r(&wolT, outWol);
    return true;
}

inline void spmFormatTm(const struct tm *t, char *buf, size_t bufSize)
{
    snprintf(buf, bufSize, "%04d-%02d-%02d %02d:%02d",
             t->tm_year + 1900, t->tm_mon + 1, t->tm_mday, t->tm_hour, t->tm_min);
}

inline void spmBuildScheduleJson(char *out, size_t outSize, bool timeOk)
{
    size_t n = 0;
#define SPM_APPEND(...)                                                             \
    do                                                                              \
    {                                                                               \
        int _w = snprintf(out + n, (n < outSize) ? (outSize - n) : 0, __VA_ARGS__); \
        if (_w > 0)                                                                 \
            n += (size_t)_w;                                                        \
    } while (0)

    time_t nowEpoch = time(nullptr);
    bool canNext = timeOk && nowEpoch > 1700000000;

    SPM_APPEND("{\"mac\":\"%s\",\"time_ok\":%s,", g_spmTargetMac, timeOk ? "true" : "false");
    SPM_APPEND("\"daily\":{\"enabled\":%s,\"hour\":%d,\"minute\":%d},\"weekly\":[",
               g_spmDaily.enabled ? "true" : "false", g_spmDaily.hour, g_spmDaily.minute);
    for (int i = 0; i < g_spmWeeklyCount; i++)
    {
        if (i > 0)
        {
            SPM_APPEND(",");
        }
        SPM_APPEND("{\"weekday\":%d,\"hour\":%d,\"minute\":%d}",
                   g_spmWeekly[i].weekday, g_spmWeekly[i].hour, g_spmWeekly[i].minute);
    }
    SPM_APPEND("],\"onetime\":[");
    for (int i = 0; i < g_spmOnetimeCount; i++)
    {
        if (i > 0)
        {
            SPM_APPEND(",");
        }
        SPM_APPEND("{\"year\":%d,\"month\":%d,\"day\":%d,\"hour\":%d,\"minute\":%d,\"source\":\"%s\"}",
                   g_spmOnetime[i].year, g_spmOnetime[i].month, g_spmOnetime[i].day,
                   g_spmOnetime[i].hour, g_spmOnetime[i].minute, g_spmOnetime[i].source);
    }
    SPM_APPEND("],\"auto_wol_rules\":[");

    // rules: action,type,...;
    char rulesCopy[SPM_RULES_MAX];
    strncpy(rulesCopy, g_spmAutoWolRules, sizeof(rulesCopy) - 1);
    rulesCopy[sizeof(rulesCopy) - 1] = '\0';
    char *save = nullptr;
    char *item = strtok_r(rulesCopy, ";", &save);
    bool firstRule = true;
    while (item)
    {
        char action[16] = "";
        char type[16] = "";
        int a = 0, b = 0, c = 0, d = 0, e = 0;
        // daily: action,daily,h,m
        // weekly: action,weekly,wd,h,m
        // onetime: action,onetime,y,mo,d,h,m
        int nScan = sscanf(item, "%15[^,],%15[^,],%d,%d,%d,%d,%d", action, type, &a, &b, &c, &d, &e);
        if (nScan >= 4)
        {
            char nextBuf[32] = "";
            struct tm wolTm;
            bool haveNext = false;
            if (canNext && strcmp(type, "daily") == 0 && nScan >= 4)
            {
                haveNext = spmNextWolFromDaily(a, b, nowEpoch, &wolTm);
            }
            else if (canNext && strcmp(type, "weekly") == 0 && nScan >= 5)
            {
                haveNext = spmNextWolFromWeekly(a, b, c, nowEpoch, &wolTm);
            }
            else if (canNext && strcmp(type, "onetime") == 0 && nScan >= 7)
            {
                haveNext = spmNextWolFromOnetime(a, b, c, d, e, nowEpoch, &wolTm);
            }
            if (haveNext)
            {
                spmFormatTm(&wolTm, nextBuf, sizeof(nextBuf));
            }

            if (!firstRule)
            {
                SPM_APPEND(",");
            }
            firstRule = false;
            if (strcmp(type, "daily") == 0)
            {
                SPM_APPEND("{\"action\":\"%s\",\"type\":\"daily\",\"hour\":%d,\"minute\":%d,\"next\":\"%s\"}",
                           action, a, b, nextBuf);
            }
            else if (strcmp(type, "weekly") == 0)
            {
                SPM_APPEND("{\"action\":\"%s\",\"type\":\"weekly\",\"weekday\":%d,\"hour\":%d,\"minute\":%d,\"next\":\"%s\"}",
                           action, a, b, c, nextBuf);
            }
            else if (strcmp(type, "onetime") == 0)
            {
                SPM_APPEND("{\"action\":\"%s\",\"type\":\"onetime\",\"year\":%d,\"month\":%d,\"day\":%d,\"hour\":%d,\"minute\":%d,\"next\":\"%s\"}",
                           action, a, b, c, d, e, nextBuf);
            }
        }
        item = strtok_r(nullptr, ";", &save);
    }
    SPM_APPEND("]}");
#undef SPM_APPEND
    if (outSize > 0)
    {
        out[(n < outSize) ? n : (outSize - 1)] = '\0';
    }
}

inline void spmCheckSchedule(bool timeOk, void (*sendWolFn)(const char *mac))
{
    if (!timeOk || sendWolFn == nullptr)
    {
        return;
    }
    time_t nowEpoch = time(nullptr);
    if (nowEpoch < 1700000000)
    {
        return;
    }
    struct tm tmNow;
    localtime_r(&nowEpoch, &tmNow);
    int currentYear = tmNow.tm_year + 1900;
    int currentMonth = tmNow.tm_mon + 1;
    int currentDay = tmNow.tm_mday;
    int currentHour = tmNow.tm_hour;
    int currentMinute = tmNow.tm_min;
    int currentWeekday = (tmNow.tm_wday == 0) ? 6 : (tmNow.tm_wday - 1);

    static int lastCheckedMinute = -1;
    if (currentMinute == lastCheckedMinute)
    {
        return;
    }
    lastCheckedMinute = currentMinute;

    bool wake = false;
    bool needSave = false;

    if (g_spmDaily.enabled && g_spmDaily.hour == currentHour && g_spmDaily.minute == currentMinute)
    {
        wake = true;
    }
    for (int i = 0; i < g_spmWeeklyCount; i++)
    {
        if (g_spmWeekly[i].weekday == currentWeekday &&
            g_spmWeekly[i].hour == currentHour &&
            g_spmWeekly[i].minute == currentMinute)
        {
            wake = true;
        }
    }
    for (int i = 0; i < g_spmOnetimeCount; i++)
    {
        if (!g_spmOnetime[i].executed &&
            g_spmOnetime[i].year == currentYear &&
            g_spmOnetime[i].month == currentMonth &&
            g_spmOnetime[i].day == currentDay &&
            g_spmOnetime[i].hour == currentHour &&
            g_spmOnetime[i].minute == currentMinute)
        {
            wake = true;
            g_spmOnetime[i].executed = true;
            needSave = true;
        }
    }
    for (int i = 0; i < g_spmAutoWolWeeklyCount; i++)
    {
        if (g_spmAutoWolWeekly[i].weekday == currentWeekday &&
            g_spmAutoWolWeekly[i].hour == currentHour &&
            g_spmAutoWolWeekly[i].minute == currentMinute)
        {
            wake = true;
        }
    }
    for (int i = 0; i < g_spmAutoWolOnetimeCount; i++)
    {
        if (!g_spmAutoWolOnetime[i].executed &&
            g_spmAutoWolOnetime[i].year == currentYear &&
            g_spmAutoWolOnetime[i].month == currentMonth &&
            g_spmAutoWolOnetime[i].day == currentDay &&
            g_spmAutoWolOnetime[i].hour == currentHour &&
            g_spmAutoWolOnetime[i].minute == currentMinute)
        {
            wake = true;
            g_spmAutoWolOnetime[i].executed = true;
            needSave = true;
        }
    }

    if (needSave)
    {
        spmSaveEeprom();
    }
    if (wake)
    {
        sendWolFn(g_spmTargetMac);
    }
}
