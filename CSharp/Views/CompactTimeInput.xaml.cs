using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SmartPowerManager.Views;

public sealed partial class CompactTimeInput : UserControl
{
    private bool _suppressEvents;
    private bool _initialized;

    public event EventHandler? TimeChanged;

    public CompactTimeInput()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public TimeSpan Time
    {
        get
        {
            EnsureInitialized();
            int hour = HourBox.SelectedIndex >= 0 ? HourBox.SelectedIndex : 0;
            int minute = MinuteBox.SelectedIndex >= 0 ? MinuteBox.SelectedIndex : 0;
            return new TimeSpan(hour, minute, 0);
        }
        set
        {
            EnsureInitialized();
            _suppressEvents = true;
            try
            {
                HourBox.SelectedIndex = Math.Clamp(value.Hours, 0, 23);
                MinuteBox.SelectedIndex = Math.Clamp(value.Minutes, 0, 59);
            }
            finally
            {
                _suppressEvents = false;
            }
        }
    }

    public void StopTracking() => TimeChanged = null;

    private void OnLoaded(object sender, RoutedEventArgs e) => EnsureInitialized();

    private void EnsureInitialized()
    {
        if (_initialized)
            return;

        _initialized = true;
        _suppressEvents = true;
        try
        {
            for (int h = 0; h <= 23; h++)
                HourBox.Items.Add($"{h:D2}時");

            for (int m = 0; m <= 59; m++)
                MinuteBox.Items.Add($"{m:D2}分");

            HourBox.SelectedIndex = 0;
            MinuteBox.SelectedIndex = 0;
            ComboBoxMouseWheelHelper.Attach(HourBox, MinuteBox);
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private void TimePart_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || !_initialized)
            return;

        TimeChanged?.Invoke(this, EventArgs.Empty);
    }
}
