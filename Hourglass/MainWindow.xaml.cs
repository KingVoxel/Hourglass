using System.Globalization;
using System.Media;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;

namespace Hourglass;

public partial class MainWindow : Window
{
    // Geometry of the hourglass on its 200 x 300 canvas (must match MainWindow.xaml).
    private const double BulbLeft = 30;
    private const double BulbWidth = 140;
    private const double BulbHeight = 130;
    private const double NeckY = 150;
    private const double FloorY = 280;

    private const string DefaultHint = "Try 5m, 90s, 1h 30m or 2:30";

    private enum TimerState { Idle, Running, Paused, Finished }

    private readonly DispatcherTimer _timer;
    private TimerState _state = TimerState.Idle;
    private TimeSpan _total = TimeSpan.FromMinutes(5);
    private TimeSpan _remaining = TimeSpan.FromMinutes(5);
    private DateTime _endUtc;
    private bool _flipping;

    public MainWindow()
    {
        InitializeComponent();

        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(30)
        };
        _timer.Tick += (_, _) => Tick();

        DurationBox.Text = "5m";
        DurationBox.TextChanged += DurationBox_TextChanged;
        Loaded += (_, _) =>
        {
            DurationBox.Focus();
            DurationBox.SelectAll();
        };

        Render();
    }

    // ---------- Windows 11 dark title bar ----------

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        int enabled = 1;
        var hwnd = new WindowInteropHelper(this).Handle;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int));
    }

    // ---------- UI events ----------

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        switch (_state)
        {
            case TimerState.Running:
                _remaining = _endUtc - DateTime.UtcNow;
                _timer.Stop();
                _state = TimerState.Paused;
                break;

            case TimerState.Paused:
                _endUtc = DateTime.UtcNow + _remaining;
                _state = TimerState.Running;
                _timer.Start();
                break;

            default: // Idle or Finished: start a new countdown
                if (!TryParseDuration(DurationBox.Text, out var duration))
                {
                    HintText.Text = "Couldn't read that. " + DefaultHint;
                    HintText.Foreground = Brushes.Salmon;
                    HintText.Opacity = 1;
                    SetCompact(false); // make sure the message is visible
                    return;
                }

                _total = duration;
                _remaining = duration;
                _endUtc = DateTime.UtcNow + duration;
                _state = TimerState.Running;
                _timer.Start();
                PlayFlip();
                break;
        }

        Render();
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        _flipping = false;
        Flip.BeginAnimation(RotateTransform.AngleProperty, null);
        _remaining = _total;
        _state = TimerState.Idle;
        Render();
    }

    private void TopmostCheck_Click(object sender, RoutedEventArgs e)
    {
        Topmost = TopmostCheck.IsChecked == true;
    }

    private void DurationBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        HintText.Text = DefaultHint;
        HintText.ClearValue(ForegroundProperty);
        HintText.Opacity = 0.65;

        if (_state is TimerState.Running or TimerState.Paused)
            return;

        if (TryParseDuration(DurationBox.Text, out var duration))
        {
            _total = duration;
            _remaining = duration;
            _state = TimerState.Idle;
            Render();
        }
    }

    // ---------- Full / compact mode ----------

    private const double FullMinHeight = 240;
    private const double CompactMinHeight = 150;

    private bool _compact;
    private double _detailsHeight; // height of the controls below the countdown, in layout units

    // Clicking the hourglass switches between full and compact mode.
    private void HourglassArea_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        SetCompact(!_compact);
    }

    // Compact mode hides the controls below the countdown and shortens the window by
    // exactly their height, so the hourglass and countdown stay the same size and the
    // top edge of the window does not move. Full mode grows the window back down.
    private void SetCompact(bool compact)
    {
        if (compact == _compact)
            return;
        _compact = compact;

        bool canResize = WindowState == WindowState.Normal;
        double scale = ContentScale();

        if (compact)
        {
            _detailsHeight = DetailsPanel.ActualHeight;
            DetailsPanel.Visibility = Visibility.Collapsed;
            MinHeight = CompactMinHeight;
            if (canResize)
                Height = Math.Max(CompactMinHeight, ActualHeight - _detailsHeight * scale);
        }
        else
        {
            DetailsPanel.Visibility = Visibility.Visible;
            if (canResize)
                Height = ActualHeight + _detailsHeight * scale;
            MinHeight = FullMinHeight;
            DurationBox.Focus();

            // Once the window has grown, make sure its bottom is still on the screen.
            Dispatcher.BeginInvoke(new Action(KeepOnScreen), DispatcherPriority.Loaded);
        }
    }

    // How much the Viewbox is currently enlarging or shrinking the layout.
    private double ContentScale()
    {
        var toWindow = ContentPanel.TransformToAncestor(this);
        return toWindow.Transform(new Point(0, 1)).Y - toWindow.Transform(new Point(0, 0)).Y;
    }

    // If the window hangs off the bottom of its monitor's work area, slide it up.
    private void KeepOnScreen()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || WindowState != WindowState.Normal)
            return;

        var monitor = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetWindowRect(hwnd, out RECT window) ||
            !GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref monitor))
            return;

        int overflow = window.Bottom - monitor.rcWork.Bottom;
        if (overflow <= 0)
            return;

        int newTop = Math.Max(monitor.rcWork.Top, window.Top - overflow);
        SetWindowPos(hwnd, IntPtr.Zero, window.Left, newTop, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter,
        int x, int y, int cx, int cy, uint flags);

    // The buttons are hidden in compact mode, so Space / Enter start and pause the timer there.
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (_compact && (e.Key == Key.Space || e.Key == Key.Enter))
        {
            StartButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    // ---------- Timer ----------

    private void Tick()
    {
        _remaining = _endUtc - DateTime.UtcNow;

        if (_remaining <= TimeSpan.Zero)
        {
            _remaining = TimeSpan.Zero;
            _timer.Stop();
            _state = TimerState.Finished;
            SystemSounds.Exclamation.Play();
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
            SetCompact(false); // show the controls again when time is up
        }

        Render();
    }

    private void PlayFlip()
    {
        _flipping = true;
        var animation = new DoubleAnimation(180, 0, TimeSpan.FromMilliseconds(600))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        animation.Completed += (_, _) =>
        {
            _flipping = false;
            Render();
        };
        Flip.BeginAnimation(RotateTransform.AngleProperty, animation);
    }

    // ---------- Drawing ----------

    private void Render()
    {
        // Fraction of sand still in the top bulb.
        double fraction = _total > TimeSpan.Zero
            ? Math.Clamp(_remaining.TotalSeconds / _total.TotalSeconds, 0, 1)
            : 0;

        // The bulbs narrow towards the neck, so the sand level follows a square root:
        // it drops slowly at first and quickly near the end, like a real hourglass.
        double root = Math.Sqrt(fraction);
        double topHeight = BulbHeight * root;
        double bottomHeight = BulbHeight * (1 - root);

        TopSandRect.Rect = new Rect(BulbLeft, NeckY - topHeight, BulbWidth, topHeight);
        BottomSandRect.Rect = new Rect(BulbLeft, FloorY - bottomHeight, BulbWidth, bottomHeight);

        bool streaming = _state == TimerState.Running && !_flipping && fraction > 0;
        SandStream.Visibility = streaming ? Visibility.Visible : Visibility.Collapsed;
        SandStream.Height = Math.Max(0, FloorY - bottomHeight - NeckY);

        string time = FormatTime(_remaining);
        TimeText.Text = _state == TimerState.Finished ? "Time's up" : time;

        Title = _state is TimerState.Running or TimerState.Paused
            ? $"{time} - Hourglass"
            : "Hourglass";

        StartButton.Content = _state switch
        {
            TimerState.Running => "Pause",
            TimerState.Paused => "Resume",
            _ => "Start"
        };

        bool busy = _state is TimerState.Running or TimerState.Paused;
        DurationBox.IsReadOnly = busy;
        DurationBox.Opacity = busy ? 0.5 : 1;

        // Progress on the taskbar button.
        Taskbar.ProgressValue = _state == TimerState.Finished ? 1 : 1 - fraction;
        Taskbar.ProgressState = _state switch
        {
            TimerState.Running => TaskbarItemProgressState.Normal,
            TimerState.Paused => TaskbarItemProgressState.Paused,
            TimerState.Finished => TaskbarItemProgressState.Error,
            _ => TaskbarItemProgressState.None
        };
    }

    private static string FormatTime(TimeSpan time)
    {
        long seconds = Math.Max(0, (long)Math.Ceiling(time.TotalSeconds));
        long h = seconds / 3600;
        long m = seconds % 3600 / 60;
        long s = seconds % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
    }

    // ---------- Duration parsing ----------

    // Accepts "5" (minutes), "5m", "90s", "1h 30m", "1.5h", "2:30" (m:ss) and "1:15:00" (h:mm:ss).
    private static bool TryParseDuration(string text, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        text = text.Trim().ToLowerInvariant();
        if (text.Length == 0)
            return false;

        double seconds = 0;

        if (text.Contains(':'))
        {
            var parts = text.Split(':');
            if (parts.Length is < 2 or > 3)
                return false;

            foreach (var part in parts)
            {
                if (!double.TryParse(part.Trim(), NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture, out double value))
                    return false;
                seconds = seconds * 60 + value;
            }
        }
        else
        {
            const string pattern = @"(\d+(?:\.\d+)?)\s*([a-z]*)";

            // Anything left over after removing "number + unit" pieces is invalid input.
            if (Regex.Replace(text, pattern, "").Trim().Length > 0)
                return false;

            foreach (Match match in Regex.Matches(text, pattern))
            {
                double value = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                string unit = match.Groups[2].Value;

                if (unit.Length == 0 || unit[0] == 'm') seconds += value * 60;
                else if (unit[0] == 'h') seconds += value * 3600;
                else if (unit[0] == 's') seconds += value;
                else return false;
            }
        }

        if (seconds < 1 || seconds > 359_999) // up to 99:59:59
            return false;

        result = TimeSpan.FromSeconds(seconds);
        return true;
    }
}
