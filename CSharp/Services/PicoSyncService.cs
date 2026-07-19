using System.Net.Http;
using Newtonsoft.Json.Linq;
using SmartPowerManager.Models;

namespace SmartPowerManager.Services;

public static class PicoSyncService
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static async Task<string> SyncAsync(ScheduleData data, ScheduleManager? scheduleManager = null, CancellationToken cancellationToken = default)
    {
        string ip = data.PicoSettings.Ip?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(ip) || ip == AppConstants.DefaultPicoIp)
            return "Pico W IP 未設定";

        var daily = data.PicoSettings.StartupDaily;
        string weeklyStr = string.Join(";",
            data.PicoSettings.StartupWeekly.Select(s => $"{s.Weekday},{s.Hour},{s.Minute}"));
        if (!string.IsNullOrEmpty(weeklyStr))
            weeklyStr += ";";

        string onetimeStr = string.Empty;
        foreach (var s in data.PicoSettings.StartupOnetime)
        {
            if (!DateTime.TryParseExact(s.Datetime, "yyyy-MM-dd HH:mm", null,
                    System.Globalization.DateTimeStyles.None, out var dt))
                continue;

            string src = string.IsNullOrWhiteSpace(s.Source) ? "manual" : s.Source;
            onetimeStr += $"{dt.Year},{dt.Month},{dt.Day},{dt.Hour},{dt.Minute},{src};";
        }

        var autoWol = AutoWolCalculator.BuildPicoAutoWolStrings(data);
        var postData = new Dictionary<string, string>
        {
            ["d_en"] = daily.Enabled ? "1" : "0",
            ["d_h"] = daily.Hour.ToString(),
            ["d_m"] = daily.Minute.ToString(),
            ["mac"] = data.PicoSettings.TargetMac ?? string.Empty,
            ["weekly"] = weeklyStr,
            ["onetime"] = onetimeStr,
            ["auto_wol_weekly"] = autoWol.Weekly,
            ["auto_wol_onetime"] = autoWol.Onetime
        };

        using var content = new FormUrlEncodedContent(postData);
        string url = $"http://{ip}/update_schedule";
        using var response = await HttpClient.PostAsync(url, content, cancellationToken);
        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (TryParsePicoResponse(body, out var json) && scheduleManager != null)
        {
            scheduleManager.ApplyPicoResponse(json);
            return "Pico W 同期成功（設定を反映しました）";
        }

        return body.Contains("OK", StringComparison.OrdinalIgnoreCase)
            ? "Pico W 同期成功"
            : $"Pico W 応答: {body[..Math.Min(body.Length, 40)]}";
    }

    private static bool TryParsePicoResponse(string body, out JObject json)
    {
        json = new JObject();
        try
        {
            var parsed = JObject.Parse(body);
            json = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
