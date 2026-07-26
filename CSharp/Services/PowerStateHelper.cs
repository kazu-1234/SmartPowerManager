using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SmartPowerManager.Services;

public static class PowerStateHelper
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsDisplayRequired = 0x00000002;
    private const uint EsSystemRequired = 0x00000001;

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MessageBeep(uint uType);

    private const uint MbIconExclamation = 0x00000030;

    public static void WakeDisplay()
    {
        try
        {
            SetThreadExecutionState(EsDisplayRequired | EsSystemRequired);
        }
        catch
        {
        }
    }

    public static void ResetPowerState()
    {
        try
        {
            SetThreadExecutionState(EsContinuous);
        }
        catch
        {
        }
    }

    public static void PlayWarningBeep()
    {
        try
        {
            MessageBeep(MbIconExclamation);
        }
        catch
        {
        }
    }

    public static void AbortPendingShutdown()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "shutdown",
                Arguments = "/a",
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }
        catch
        {
        }
    }

    public static void ExecuteShutdownOrRestart(string action)
    {
        string flag = action == AppConstants.ActionShutdown ? "/s" : "/r";
        Process.Start(new ProcessStartInfo
        {
            FileName = "shutdown",
            Arguments = $"{flag} /t 0",
            CreateNoWindow = true,
            UseShellExecute = false
        });
    }
}
