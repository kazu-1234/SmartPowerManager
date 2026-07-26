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
        bool hasGas = (data.PicoSettings.GasUrl ?? string.Empty).StartsWith("http", StringComparison.OrdinalIgnoreCase);

        if (!hasPico && !hasGas)
        {
            if (!silent)
                LogAdded?.Invoke("Pico W IP または GAS Webhook URL を設定してください");
            return;
        }

        if (!silent)
            LogAdded?.Invoke("設定を送信中...");

        var tasks = new List<Task>();

        if (hasPico)
        {
            tasks.Add(Task.Run(async () =>
            {
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
            }));
        }

        if (hasGas)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await GasSyncService.SyncAsync(data);
                    if (!silent)
                        LogAdded?.Invoke("GAS 同期成功");
                }
                catch (Exception ex)
                {
                    if (!silent)
                        LogAdded?.Invoke($"GAS 送信エラー: {ex.Message}");
                }
            }));
        }

        await Task.WhenAll(tasks);
    }
}
