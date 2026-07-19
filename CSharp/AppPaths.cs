using System.Reflection;

namespace SmartPowerManager;

public static class AppPaths
{
    public static string AppDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SmartPowerManager");

    public static string SchedulesFilePath =>
        Path.Combine(AppDataDirectory, "schedules.json");

    public static string SignalFilePath =>
        Path.Combine(AppDataDirectory, ".show_signal");

    public static string ExecutableDirectory =>
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
        ?? AppContext.BaseDirectory;

    public static string LegacySchedulesFilePath =>
        Path.Combine(ExecutableDirectory, "schedules.json");
}
