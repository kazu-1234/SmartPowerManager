using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace SmartPowerManager;

/// <summary>
/// Win32 WINDOWPLACEMENT で通常サイズ・位置・最大化を正確に保存／復元する。
/// </summary>
internal static class WindowPlacementHelper
{
    private const int SwHide = 0;
    private const int SwShowNormal = 1;
    private const int SwShowMinimized = 2;
    private const int SwShowMaximized = 3;
    private const int SwMinimized = 6;

    public static void Save(Window window, Settings settings)
    {
        IntPtr hwnd = WindowNative.GetWindowHandle(window);
        if (hwnd == IntPtr.Zero)
            return;

        var placement = new WindowPlacement
        {
            Length = Marshal.SizeOf<WindowPlacement>()
        };

        if (!GetWindowPlacement(hwnd, ref placement))
            return;

        if (placement.ShowCmd == SwShowMinimized || placement.ShowCmd == SwMinimized || placement.ShowCmd == SwHide)
            return;

        var normal = placement.NormalPosition;
        int width = normal.Right - normal.Left;
        int height = normal.Bottom - normal.Top;
        if (width < 400 || height < 300)
            return;

        settings.WindowX = normal.Left;
        settings.WindowY = normal.Top;
        settings.WindowWidth = width;
        settings.WindowHeight = height;
        settings.WindowMaximized = placement.ShowCmd == SwShowMaximized;
        settings.Save();
    }

    public static void Restore(Window window, Settings settings, int defaultWidth, int defaultHeight)
    {
        IntPtr hwnd = WindowNative.GetWindowHandle(window);
        if (hwnd == IntPtr.Zero)
            return;

        int width = settings.WindowWidth > 0 ? settings.WindowWidth : defaultWidth;
        int height = settings.WindowHeight > 0 ? settings.WindowHeight : defaultHeight;
        int x = settings.WindowX;
        int y = settings.WindowY;

        var display = (x >= 0 && y >= 0)
            ? DisplayArea.GetFromPoint(new PointInt32(x, y), DisplayAreaFallback.Nearest)
            : DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Primary);
        display ??= DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Primary);

        if (display != null)
        {
            var work = display.WorkArea;
            width = Math.Clamp(width, 400, Math.Max(400, work.Width));
            height = Math.Clamp(height, 300, Math.Max(300, work.Height));

            if (x < 0 || y < 0)
            {
                x = work.X + Math.Max(0, (work.Width - width) / 2);
                y = work.Y + Math.Max(0, (work.Height - height) / 2);
            }
            else
            {
                x = Math.Clamp(x, work.X, work.X + Math.Max(0, work.Width - width));
                y = Math.Clamp(y, work.Y, work.Y + Math.Max(0, work.Height - height));
            }
        }
        else if (x < 0 || y < 0)
        {
            x = 100;
            y = 100;
        }

        var placement = new WindowPlacement
        {
            Length = Marshal.SizeOf<WindowPlacement>(),
            Flags = 0,
            ShowCmd = settings.WindowMaximized ? SwShowMaximized : SwShowNormal,
            MinPosition = new Point { X = -1, Y = -1 },
            MaxPosition = new Point { X = -1, Y = -1 },
            NormalPosition = new Rect
            {
                Left = x,
                Top = y,
                Right = x + width,
                Bottom = y + height
            }
        };

        window.AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        SetWindowPlacement(hwnd, ref placement);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCmd;
        public Point MinPosition;
        public Point MaxPosition;
        public Rect NormalPosition;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WindowPlacement lpwndpl);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPlacement(IntPtr hWnd, ref WindowPlacement lpwndpl);
}
