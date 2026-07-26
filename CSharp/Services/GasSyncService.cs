using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using SmartPowerManager.Models;

namespace SmartPowerManager.Services;

public static class GasSyncService
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static async Task SyncAsync(ScheduleData data, CancellationToken cancellationToken = default)
    {
        string gasUrl = data.PicoSettings.GasUrl?.Trim() ?? string.Empty;
        if (!gasUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("GAS Webhook URL 未設定");

        var triggers = AutoWolCalculator.BuildWolTriggers(data, includeStartupSchedules: true);
        var payload = new
        {
            wol_triggers = triggers.Select(t => new
            {
                type = t.Type,
                hour = t.Hour,
                minute = t.Minute,
                weekday = t.Weekday,
                datetime = t.Datetime
            }),
            target_pc = string.IsNullOrWhiteSpace(data.PicoSettings.GasTarget)
                ? AppConstants.GasTargetDesktop
                : data.PicoSettings.GasTarget
        };

        string json = JsonConvert.SerializeObject(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await HttpClient.PostAsync(gasUrl, content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
