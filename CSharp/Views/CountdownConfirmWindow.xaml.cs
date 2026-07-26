using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using SmartPowerManager.Services;
using Windows.Graphics;
using WinRT.Interop;

namespace SmartPowerManager.Views;

/// <summary>
/// シャットダウン／再起動前の 60 秒カウントダウンを最前面で表示する専用ウィンドウ。
/// </summary>
public sealed partial class CountdownConfirmWindow : Window
{
    private const int CountdownSeconds = 60;
    private const int WindowWidth = 440;
    private const int WindowHeight = 300;

    private readonly TaskCompletionSource<bool> _tcs = new();
    private readonly DispatcherTimer _timer;
    private readonly bool _isPreview;
    private readonly bool _playBeep;
    private int _remaining = CountdownSeconds;
    private bool _confirmed;
    private bool _cancelled;
    private bool _closing;
    private OverlappedPresenter? _presenter;

    public Task<bool> Result => _tcs.Task;

    public CountdownConfirmWindow(
        string action,
        string triggerLabel,
        bool isPreview = false,
        bool playBeep = true)
    {
        _isPreview = isPreview;
        _playBeep = playBeep && !isPreview;

        InitializeComponent();

        string label = action == AppConstants.ActionShutdown ? "シャットダウン" : "再起動";
        string titleSuffix = isPreview ? " [プレビュー]" : string.Empty;
        Title = $"{label}確認{titleSuffix}";
        TitleText.Text = Title;
        MessageText.Text = $"{triggerLabel} の{label}まであと";
        ExecuteNowButton.Content = $"今すぐ{label}";
        CancelButton.Content = "キャンセル";
        PreviewHintText.Visibility = isPreview ? Visibility.Visible : Visibility.Collapsed;

        ThemeService.ApplyThemeToElement(RootGrid);
        ConfigureAlwaysOnTopWindow();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += Timer_Tick;

        Closed += CountdownConfirmWindow_Closed;
        Activated += CountdownConfirmWindow_Activated;
    }

    public void StartCountdown()
    {
        if (!_isPreview)
            PowerStateHelper.WakeDisplay();

        CountdownText.Text = $"{_remaining}秒";
        _timer.Start();

        if (_playBeep)
            PowerStateHelper.PlayWarningBeep();

        BringToForeground();
    }

    private void ConfigureAlwaysOnTopWindow()
    {
        AppWindow.Resize(new SizeInt32(WindowWidth, WindowHeight));
        AppWindow.IsShownInSwitchers = true;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            _presenter = presenter;
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app_icon.ico");
        if (File.Exists(iconPath))
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            IntPtr hIcon = PInvokeHelper.LoadImageIcon(iconPath);
            if (hIcon != IntPtr.Zero)
                PInvokeHelper.SendMessageIcon(hwnd, hIcon);
        }

        CenterOnScreen();
        AppWindow.Show();
        Activate();
    }

    private void CenterOnScreen()
    {
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        if (display == null)
            return;

        int x = display.WorkArea.X + (display.WorkArea.Width - WindowWidth) / 2;
        int y = display.WorkArea.Y + (display.WorkArea.Height - WindowHeight) / 2;
        AppWindow.Move(new PointInt32(x, y));
    }

    private void BringToForeground()
    {
        if (_presenter != null)
            _presenter.IsAlwaysOnTop = true;

        AppWindow.Show();
        Activate();
        IntPtr hwnd = WindowNative.GetWindowHandle(this);
        PInvokeHelper.SetForegroundWindow(hwnd);
    }

    private void CountdownConfirmWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated && _presenter != null)
            _presenter.IsAlwaysOnTop = true;
    }

    private void Timer_Tick(object? sender, object e)
    {
        _remaining--;
        CountdownText.Text = $"{Math.Max(_remaining, 0)}秒";

        if (_remaining <= 10 && _remaining >= 0)
        {
            if (_playBeep)
                PowerStateHelper.PlayWarningBeep();

            if (_isPreview)
            {
                PreviewHintText.Visibility = Visibility.Visible;
                PreviewHintText.Text = _remaining <= 0
                    ? "警告音: ビープ（プレビュー・音は鳴りません）"
                    : $"警告音: ビープ × {11 - _remaining} 回（プレビュー・音は鳴りません）";
            }
        }

        if (_remaining <= 0)
        {
            _timer.Stop();
            _confirmed = true;
            CloseSafely();
        }
    }

    private void ExecuteNowButton_Click(object sender, RoutedEventArgs e)
    {
        _confirmed = true;
        _cancelled = false;
        CloseSafely();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _confirmed = false;
        _cancelled = true;
        CloseSafely();
    }

    private void CloseSafely()
    {
        if (_closing)
            return;

        _closing = true;
        _timer.Stop();
        Close();
    }

    private void CountdownConfirmWindow_Closed(object sender, WindowEventArgs args)
    {
        _timer.Stop();

        if (_presenter != null)
            _presenter.IsAlwaysOnTop = false;

        if (!_isPreview)
            PowerStateHelper.ResetPowerState();

        bool result = _confirmed && !_cancelled;
        _tcs.TrySetResult(result);
    }
}
