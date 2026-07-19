using Microsoft.UI.Dispatching;

namespace SmartPowerManager.Services;

/// <summary>
/// 無効な毎日スケジュール等で時刻入力を現在時刻に追従させる（Python _setup_realtime_clock 相当）
/// UI スレッドの DispatcherQueueTimer のみで動作する。
/// </summary>
public static class RealtimeClockService
{
    private static readonly List<Tracker> Trackers = [];
    private static readonly object Gate = new();
    private static DispatcherQueueTimer? _timer;

    public sealed class Tracker
    {
        private readonly Action<TimeSpan> _setTime;
        private readonly Func<bool> _isActive;
        private bool _stopped;

        internal Tracker(Action<TimeSpan> setTime, Func<bool> isActive)
        {
            _setTime = setTime;
            _isActive = isActive;
        }

        public void Stop()
        {
            if (_stopped)
                return;

            _stopped = true;
            RemoveTracker(this);
        }

        internal bool IsStopped => _stopped;

        internal void UpdateIfActive()
        {
            if (_stopped || !_isActive())
                return;

            var now = System.DateTime.Now;
            _setTime(new TimeSpan(now.Hour, now.Minute, 0));
        }
    }

    public static Tracker Track(Views.CompactTimeInput input, Func<bool>? isActive = null)
    {
        EnsureTimerStarted();
        var tracker = new Tracker(t => input.Time = t, isActive ?? (() => true));
        lock (Gate)
            Trackers.Add(tracker);
        input.TimeChanged += (_, _) => tracker.Stop();
        tracker.UpdateIfActive();
        return tracker;
    }

    public static Tracker Track(Views.CompactDateTimeInput input, Func<bool>? isActive = null)
    {
        EnsureTimerStarted();
        var tracker = new Tracker(
            t =>
            {
                var current = input.DateTime;
                input.DateTime = new System.DateTime(current.Year, current.Month, current.Day, t.Hours, t.Minutes, 0);
            },
            isActive ?? (() => true));
        lock (Gate)
            Trackers.Add(tracker);
        input.DateTimeChanged += (_, _) => tracker.Stop();
        tracker.UpdateIfActive();
        return tracker;
    }

    public static void Untrack(Tracker? tracker) => tracker?.Stop();

    private static void EnsureTimerStarted()
    {
        if (_timer != null)
            return;

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        if (dispatcher == null)
            return;

        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(30);
        _timer.Tick += (_, _) => OnTimerTick();
        _timer.Start();
    }

    private static void OnTimerTick()
    {
        Tracker[] snapshot;
        lock (Gate)
            snapshot = Trackers.Where(t => !t.IsStopped).ToArray();

        foreach (var tracker in snapshot)
            tracker.UpdateIfActive();

        StopTimerIfEmpty();
    }

    private static void RemoveTracker(Tracker tracker)
    {
        lock (Gate)
            Trackers.Remove(tracker);

        StopTimerIfEmpty();
    }

    private static void StopTimerIfEmpty()
    {
        lock (Gate)
        {
            Trackers.RemoveAll(t => t.IsStopped);
            if (Trackers.Count != 0 || _timer == null)
                return;

            _timer.Stop();
            _timer = null;
        }
    }
}
