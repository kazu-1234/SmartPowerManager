using Microsoft.UI.Dispatching;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinUiShared;

/// <summary>
/// 起動・スリープ復帰時の遅延再適用とウォッチドッグ。ドメイン適用はコールバックに委譲する。
/// </summary>
public sealed class ResumeReapplyCoordinator : IDisposable
{
    public static readonly int[] DefaultDelayMs =
        { 800, 2000, 5000, 15000, 30000, 60000, 90000 };

    public const int IntensiveSeconds = 120;
    public const int UntilAppliedMaxSeconds = 180;
    public const int WatchdogNormalIntervalMs = 30000;
    public const int WatchdogIntensiveIntervalMs = 2500;

    private const int SleepGapPollMs = 2000;
    private const long SleepGapThresholdMs = 2000;
    private const int ResumeCoalesceMs = 300;

    private readonly DispatcherQueue _dispatcher;
    private readonly Func<bool> _isExiting;
    private readonly Action _onApply;
    private readonly Action? _onApplyDirect;
    private readonly Action? _onWatchdogTick;
    private readonly Func<bool>? _needsContinueForcing;

    private readonly object _gate = new();
    private CancellationTokenSource? _delayCts;
    private Timer? _watchdog;
    private Timer? _sleepGapTimer;
    private DateTime _periodStartUtc = DateTime.MinValue;
    private DateTime _intensiveUntilUtc = DateTime.MinValue;
    private long _lastSleepBiasMs = -1;
    private bool _coalesceOpen;
    private bool _coalesceTrailing;
    private bool _disposed;

    public ResumeReapplyCoordinator(
        DispatcherQueue dispatcher,
        Func<bool> isExiting,
        Action onApply,
        Action? onApplyDirect = null,
        Action? onWatchdogTick = null,
        Func<bool>? needsContinueForcing = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _isExiting = isExiting ?? throw new ArgumentNullException(nameof(isExiting));
        _onApply = onApply ?? throw new ArgumentNullException(nameof(onApply));
        _onApplyDirect = onApplyDirect;
        _onWatchdogTick = onWatchdogTick;
        _needsContinueForcing = needsContinueForcing;
    }

    public bool ShouldForceApply
    {
        get
        {
            DateTime now = DateTime.UtcNow;
            if (now < _intensiveUntilUtc)
                return true;

            if (_needsContinueForcing == null || _periodStartUtc == DateTime.MinValue)
                return false;

            if (now >= _periodStartUtc.AddSeconds(UntilAppliedMaxSeconds))
                return false;

            try
            {
                return _needsContinueForcing();
            }
            catch
            {
                return false;
            }
        }
    }

    public void Start()
    {
        StartWatchdog();
        StartSleepGapMonitor();
    }

    /// <summary>ログオン起動など、復帰イベントが来ない経路用。</summary>
    public void BeginStartupPeriod() => NotifyResume(immediate: true);

    public void NotifySuspend()
    {
        // スリープ入りを記録するだけ。適用は復帰・ギャップ検知側。
        lock (_gate)
        {
            _periodStartUtc = DateTime.UtcNow;
        }
    }

    public void NotifyResume() => NotifyResume(immediate: false);

    private void NotifyResume(bool immediate)
    {
        if (_disposed || _isExiting())
            return;

        if (immediate)
        {
            BeginIntensivePeriod();
            InvokeApply();
            ScheduleDelayedApplies();
            StartWatchdog();
            return;
        }

        bool fireNow;
        bool scheduleTrailing;
        lock (_gate)
        {
            if (!_coalesceOpen)
            {
                _coalesceOpen = true;
                fireNow = true;
                scheduleTrailing = true;
            }
            else
            {
                _coalesceTrailing = true;
                fireNow = false;
                scheduleTrailing = false;
            }
        }

        if (fireNow)
        {
            BeginIntensivePeriod();
            InvokeApply();
            ScheduleDelayedApplies();
            StartWatchdog();
        }

        if (scheduleTrailing)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(ResumeCoalesceMs).ConfigureAwait(false);
                }
                catch
                {
                    return;
                }

                bool fireTrailing;
                lock (_gate)
                {
                    fireTrailing = _coalesceTrailing;
                    _coalesceTrailing = false;
                    _coalesceOpen = false;
                }

                if (fireTrailing && !_disposed && !_isExiting())
                {
                    BeginIntensivePeriod();
                    InvokeApply();
                    ScheduleDelayedApplies();
                    StartWatchdog();
                }
            });
        }
    }

    private void BeginIntensivePeriod()
    {
        DateTime now = DateTime.UtcNow;
        _periodStartUtc = now;
        _intensiveUntilUtc = now.AddSeconds(IntensiveSeconds);
    }

    private void ScheduleDelayedApplies()
    {
        _delayCts?.Cancel();
        _delayCts?.Dispose();
        _delayCts = new CancellationTokenSource();
        var token = _delayCts.Token;
        DateTime start = DateTime.UtcNow;

        _ = Task.Run(async () =>
        {
            foreach (int delayMs in DefaultDelayMs)
            {
                try
                {
                    TimeSpan wait = start.AddMilliseconds(delayMs) - DateTime.UtcNow;
                    if (wait > TimeSpan.Zero)
                        await Task.Delay(wait, token).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    break;
                }

                if (token.IsCancellationRequested || _disposed || _isExiting())
                    break;

                InvokeApply();
            }
        }, token);
    }

    private void InvokeApply()
    {
        if (_disposed || _isExiting())
            return;

        if (!_dispatcher.TryEnqueue(() =>
            {
                if (_disposed || _isExiting())
                    return;
                SafeInvoke(_onApply);
            }))
        {
            SafeInvoke(_onApplyDirect);
        }
    }

    private void StartWatchdog()
    {
        int intervalMs = CurrentWatchdogIntervalMs();
        if (_watchdog == null)
        {
            _watchdog = new Timer(_ => OnWatchdogTick(), null, intervalMs, intervalMs);
        }
        else
        {
            _watchdog.Change(intervalMs, intervalMs);
        }
    }

    private int CurrentWatchdogIntervalMs() =>
        ShouldForceApply ? WatchdogIntensiveIntervalMs : WatchdogNormalIntervalMs;

    private void OnWatchdogTick()
    {
        if (_disposed || _isExiting())
            return;

        Action tick = _onWatchdogTick ?? _onApply;
        if (!_dispatcher.TryEnqueue(() =>
            {
                if (_disposed || _isExiting())
                    return;
                SafeInvoke(tick);
                StartWatchdog();
            }))
        {
            if (ShouldForceApply)
                SafeInvoke(_onApplyDirect);
        }
    }

    private void StartSleepGapMonitor()
    {
        _lastSleepBiasMs = ReadSleepBiasMs();
        _sleepGapTimer ??= new Timer(_ => OnSleepGapTick(), null, SleepGapPollMs, SleepGapPollMs);
    }

    private void OnSleepGapTick()
    {
        if (_disposed || _isExiting())
            return;

        long bias = ReadSleepBiasMs();
        long previous = Interlocked.Exchange(ref _lastSleepBiasMs, bias);
        if (previous < 0)
            return;

        if (bias - previous >= SleepGapThresholdMs)
            NotifyResume();
    }

    private static long ReadSleepBiasMs()
    {
        if (!QueryUnbiasedInterruptTime(out ulong unbiased100Ns))
            return 0;

        ulong unbiasedMs = unbiased100Ns / 10_000UL;
        ulong tickMs = GetTickCount64();
        return (long)(tickMs - unbiasedMs);
    }

    private static void SafeInvoke(Action? action)
    {
        if (action == null)
            return;

        try
        {
            action();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ResumeReapplyCoordinator callback failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _delayCts?.Cancel();
        _delayCts?.Dispose();
        _delayCts = null;
        _watchdog?.Dispose();
        _watchdog = null;
        _sleepGapTimer?.Dispose();
        _sleepGapTimer = null;
    }

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();

    [DllImport("kernel32.dll")]
    private static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);
}
