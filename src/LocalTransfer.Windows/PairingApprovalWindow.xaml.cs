using System.Windows;
using System.Windows.Threading;
using LocalTransfer.Contracts.Pairing;

namespace LocalTransfer.Windows;

/// <summary>
/// Confirmation dialog for an incoming pairing request.
/// <para>
/// It exists instead of a plain message box because the phone only waits for a bounded time: a
/// user who answers late used to approve a request the client had already abandoned, which wrote
/// a credential the phone never received and left every later transfer failing with 401. Showing
/// the remaining time (and refusing to report success after it runs out) removes that failure
/// mode from the UI.
/// </para>
/// </summary>
public partial class PairingApprovalWindow : Window
{
    private static readonly TimeSpan WarningThreshold = TimeSpan.FromSeconds(30);

    private readonly DateTimeOffset _deadline;
    private readonly TimeSpan _window;
    private readonly DispatcherTimer _timer;

    public PairingApprovalWindow(PairingRequestInfo request, DateTimeOffset deadline)
    {
        ArgumentNullException.ThrowIfNull(request);
        _deadline = deadline;

        // The window between the phone submitting and its bootstrap expiring is what the bar
        // represents; guard against a zero/negative span so the ratio stays meaningful.
        var span = deadline - request.RequestedAtUtc;
        _window = span > TimeSpan.Zero ? span : TimeSpan.FromMinutes(2);

        InitializeComponent();
        DeviceNameText.Text = request.Device.DisplayName;
        DevicePlatformText.Text = $"{request.Device.Platform} · 协议版本 {request.Device.ProtocolVersion}";
        DeviceIdText.Text = request.Device.DeviceId.ToString();

        _timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _timer.Tick += OnTick;
        Loaded += OnLoaded;
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>True when the dialog closed because the phone stopped waiting.</summary>
    public bool TimedOut { get; private set; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateCountdown();
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e) => UpdateCountdown();

    private void UpdateCountdown()
    {
        var remaining = _deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            TimedOut = true;
            _timer.Stop();
            DialogResult = false;
            return;
        }

        CountdownText.Text = $"{(int)remaining.TotalMinutes:00}:{remaining.Seconds:00}";
        CountdownBar.Value = Math.Clamp(remaining.TotalSeconds / _window.TotalSeconds, 0, 1);

        if (remaining <= WarningThreshold)
        {
            CountdownText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            CountdownHintText.Text = "时间快到了：请立即点「信任此设备」，否则这次配对将作废。";
        }
    }

    private void OnApproveClicked(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        DialogResult = true;
    }

    private void OnRejectClicked(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        DialogResult = false;
    }
}
