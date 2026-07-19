using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SmartPowerManager.Views;

public sealed partial class CompactDateTimeInput : UserControl
{
    private bool _suppressEvents;
    private bool _initialized;

    public event EventHandler? DateTimeChanged;

    public CompactDateTimeInput()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public System.DateTime DateTime
    {
        get
        {
            EnsureInitialized();
            int year = GetSelectedYear();
            int month = MonthBox.SelectedIndex >= 0 ? MonthBox.SelectedIndex + 1 : System.DateTime.Now.Month;
            int day = GetSelectedDay(year, month);
            var time = TimeInput.Time;
            return new System.DateTime(year, month, day, time.Hours, time.Minutes, 0);
        }
        set
        {
            EnsureInitialized();
            _suppressEvents = true;
            try
            {
                SelectYear(value.Year);
                MonthBox.SelectedIndex = Math.Clamp(value.Month - 1, 0, 11);
                ClampDaySelection(value.Year, value.Month, value.Day);
                TimeInput.Time = new TimeSpan(value.Hour, value.Minute, 0);
            }
            finally
            {
                _suppressEvents = false;
            }
        }
    }

    public void StopTracking()
    {
        TimeInput.StopTracking();
        DateTimeChanged = null;
    }

    public CompactTimeInput TimeInputControl => TimeInput;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsureInitialized();
    }

    private void EnsureInitialized()
    {
        if (_initialized)
            return;

        _initialized = true;
        InitializeDateLists();
    }

    private void InitializeDateLists()
    {
        _suppressEvents = true;
        try
        {
            int currentYear = System.DateTime.Now.Year;
            for (int y = currentYear; y <= currentYear + 5; y++)
                YearBox.Items.Add($"{y}年");

            for (int m = 1; m <= 12; m++)
                MonthBox.Items.Add($"{m}月");

            // 日は最大31日分を一度だけ追加し、以降は Items を変更しない
            for (int d = 1; d <= 31; d++)
                DayBox.Items.Add($"{d}日");

            SelectYear(currentYear);
            MonthBox.SelectedIndex = System.DateTime.Now.Month - 1;
            ClampDaySelection(currentYear, System.DateTime.Now.Month, System.DateTime.Now.Day);
            ComboBoxMouseWheelHelper.Attach(YearBox, MonthBox, DayBox);
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private void SelectYear(int year)
    {
        string label = $"{year}年";
        for (int i = 0; i < YearBox.Items.Count; i++)
        {
            if (YearBox.Items[i] is string text && text == label)
            {
                YearBox.SelectedIndex = i;
                return;
            }
        }

        if (YearBox.Items.Count > 0)
            YearBox.SelectedIndex = 0;
    }

    private int GetSelectedYear()
    {
        if (YearBox.SelectedItem is string text
            && text.EndsWith('年')
            && int.TryParse(text[..^1], out int year))
            return year;

        return System.DateTime.Now.Year;
    }

    private int GetSelectedDay(int year, int month)
    {
        int maxDay = System.DateTime.DaysInMonth(year, month);
        int day = DayBox.SelectedIndex >= 0 ? DayBox.SelectedIndex + 1 : 1;
        return Math.Clamp(day, 1, maxDay);
    }

    private void ClampDaySelection(int year, int month, int? preferredDay = null)
    {
        int maxDay = System.DateTime.DaysInMonth(year, month);
        int day = preferredDay ?? (DayBox.SelectedIndex >= 0 ? DayBox.SelectedIndex + 1 : 1);
        int targetIndex = Math.Clamp(day, 1, maxDay) - 1;

        if (DayBox.SelectedIndex != targetIndex)
            DayBox.SelectedIndex = targetIndex;
    }

    private void YearOrMonth_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || !_initialized || MonthBox.SelectedIndex < 0)
            return;

        int year = GetSelectedYear();
        int month = MonthBox.SelectedIndex + 1;

        _suppressEvents = true;
        try
        {
            ClampDaySelection(year, month);
        }
        finally
        {
            _suppressEvents = false;
        }

        DateTimeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DayBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || !_initialized)
            return;

        DateTimeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void TimeInput_TimeChanged(object? sender, EventArgs e)
    {
        if (_suppressEvents)
            return;

        DateTimeChanged?.Invoke(this, EventArgs.Empty);
    }
}
