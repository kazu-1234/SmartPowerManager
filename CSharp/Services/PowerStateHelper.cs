using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SmartPowerManager.Services;

public static class PowerStateHelper
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsDisplayRequired = 0x00000002;
    private const uint EsSystemRequired = 0x00000001;
    private const uint MbIconExclamation = 0x00000030;

    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;
    private const string SeShutdownName = "SeShutdownPrivilege";
    private const uint ShutdownReasonPlannedOther = 0x80000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MessageBeep(uint uType);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle,
        bool disableAllPrivileges,
        ref TokenPrivileges newState,
        uint bufferLength,
        IntPtr previousState,
        IntPtr returnLength);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool InitiateSystemShutdownEx(
        string? machineName,
        string? message,
        uint timeout,
        bool forceAppsClosed,
        bool rebootAfterShutdown,
        uint reason);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool AbortSystemShutdown(string? machineName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

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
            if (AbortSystemShutdown(null))
                return;
        }
        catch
        {
        }

        TryRunShutdownExe("/a");
    }

    /// <summary>
    /// シャットダウン／再起動を実行する。子プロセス優先ではなく Win32 API を使う。
    /// </summary>
    /// <returns>成功時 null。失敗時は理由文字列。</returns>
    public static string? ExecuteShutdownOrRestart(string action)
    {
        bool reboot = action != AppConstants.ActionShutdown;

        if (TryInitiateShutdown(reboot, out int apiError))
            return null;

        string flag = reboot ? "/r" : "/s";
        if (TryRunShutdownExe($"{flag} /t 0 /f", out string? processError))
            return null;

        string apiText = apiError != 0
            ? new Win32Exception(apiError).Message
            : "InitiateSystemShutdownEx failed";
        return processError == null
            ? $"api: {apiText} (0x{apiError:X8})"
            : $"api: {apiText} (0x{apiError:X8}); fallback: {processError}";
    }

    private static bool TryInitiateShutdown(bool reboot, out int error)
    {
        error = 0;
        try
        {
            EnableShutdownPrivilege();

            if (InitiateSystemShutdownEx(
                    null,
                    null,
                    0,
                    forceAppsClosed: true,
                    rebootAfterShutdown: reboot,
                    reason: ShutdownReasonPlannedOther))
            {
                return true;
            }

            error = Marshal.GetLastWin32Error();
            return false;
        }
        catch (Exception ex)
        {
            error = Marshal.GetLastWin32Error();
            if (error == 0)
                Debug.WriteLine($"InitiateSystemShutdownEx exception: {ex.Message}");
            return false;
        }
    }

    private static bool EnableShutdownPrivilege()
    {
        if (!OpenProcessToken(
                Process.GetCurrentProcess().Handle,
                TokenAdjustPrivileges | TokenQuery,
                out IntPtr token))
        {
            return false;
        }

        try
        {
            if (!LookupPrivilegeValue(null, SeShutdownName, out Luid luid))
                return false;

            var privileges = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SePrivilegeEnabled
            };

            return AdjustTokenPrivileges(
                token,
                false,
                ref privileges,
                0,
                IntPtr.Zero,
                IntPtr.Zero);
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static bool TryRunShutdownExe(string arguments) =>
        TryRunShutdownExe(arguments, out _);

    private static bool TryRunShutdownExe(string arguments, out string? error)
    {
        error = null;
        try
        {
            string exe = Path.Combine(Environment.SystemDirectory, "shutdown.exe");
            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false
            });
            if (process == null)
            {
                error = "Process.Start returned null";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
