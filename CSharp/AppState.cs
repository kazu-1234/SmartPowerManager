using System.Collections.ObjectModel;
using SmartPowerManager.Services;

namespace SmartPowerManager
{
    public sealed class AppState
    {
        public AppState(Settings settings, ScheduleManager scheduleManager, SyncCoordinatorService syncCoordinator)
        {
            Settings = settings;
            ScheduleManager = scheduleManager;
            SyncCoordinator = syncCoordinator;
        }

        public Settings Settings { get; }
        public ScheduleManager ScheduleManager { get; }
        public SyncCoordinatorService SyncCoordinator { get; }
        public ScheduleExecutorService? Executor { get; set; }
        public Action? RequestSharedScheduleRefresh { get; set; }
        public ObservableCollection<string> ActivityLogs { get; } = new();
        public ObservableCollection<string> StartupLogs { get; } = new();

        public void AddActivityLog(string message)
        {
            ActivityLogs.Insert(0, message);
            while (ActivityLogs.Count > 100)
                ActivityLogs.RemoveAt(ActivityLogs.Count - 1);
        }

        public void AddStartupLog(string message)
        {
            StartupLogs.Insert(0, message);
            while (StartupLogs.Count > 100)
                StartupLogs.RemoveAt(StartupLogs.Count - 1);
        }

        public async Task SyncDevicesAsync(bool silent = false)
        {
            SyncCoordinator.LogAdded -= OnSyncLog;
            SyncCoordinator.LogAdded += OnSyncLog;
            await SyncCoordinator.SyncToDevicesAsync(ScheduleManager, silent);
            SyncCoordinator.LogAdded -= OnSyncLog;
        }

        private void OnSyncLog(string message) => AddStartupLog(message);
    }
}
