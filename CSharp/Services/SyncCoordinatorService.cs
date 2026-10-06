using SmartPowerManager.Models;

namespace SmartPowerManager.Services;

public sealed class SyncCoordinatorService
{
    public event Action<string>? LogAdded;

    public async Task SyncToDevicesAsync(ScheduleManager scheduleManager, bool silent = false)
    {
        var data = scheduleManager.Data;
        string ip = data.PicoSettings.Ip?.Trim() ?? string.Empty;
        bool hasPico = !string.IsNullOrWhiteSpace(ip) && ip != AppConstants.DefaultPicoIp;

        if (!hasPico)
        {
            if (!silent)
                LogAdded?.Invoke("Pico W IP を設定してください");
            return;
        }

        if (!silent)
            LogAdded?.Invoke("設定を送信中...");

        try
        {
            string result = await PicoSyncService.SyncAsync(data, scheduleManager);
            LogAdded?.Invoke(result);
        }
        catch (Exception ex)
        {
            if (!silent)
                LogAdded?.Invoke($"Pico W 通信失敗: {ex.Message}");
        }
    }
}
