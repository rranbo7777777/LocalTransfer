using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LocalTransfer.Contracts.Pairing;
using QRCoder;
// WinForms is referenced by this project as well, so the WPF types below need explicit aliases.
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using MessageBox = System.Windows.MessageBox;

namespace LocalTransfer.Windows;

public partial class PairingWindow : Window
{
    private static readonly TimeSpan WarningThreshold = TimeSpan.FromSeconds(30);

    private readonly string _payload;
    private readonly DateTimeOffset _expiresAtUtc;
    private readonly TimeSpan _window;
    private readonly DispatcherTimer _timer;

    public PairingWindow(PairingBootstrap bootstrap, string payload)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        _payload = payload;
        _expiresAtUtc = bootstrap.ExpiresAtUtc;

        var remaining = bootstrap.ExpiresAtUtc - DateTimeOffset.UtcNow;
        _window = remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMinutes(2);

        InitializeComponent();
        EndpointText.Text = bootstrap.Endpoint;
        FingerprintText.Text = bootstrap.CertificateSha256;
        QrImage.Source = CreateQrImage(payload);

        // The ticket is short lived by design, so show the countdown: a user who cannot see how
        // long the phone will wait is exactly the user who answers too late.
        _timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _timer.Tick += (_, _) => UpdateCountdown();
        Loaded += (_, _) =>
        {
            UpdateCountdown();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    private void OnCopyClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(_payload);
        }
        catch (Exception exception)
        {
            // Clipboard can transiently fail (COMException) when another process holds it open.
            MessageBox.Show(
                $"复制失败：{exception.Message}",
                "局域传输",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    private void UpdateCountdown()
    {
        var remaining = _expiresAtUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            _timer.Stop();
            CountdownText.Text = "已过期";
            CountdownText.Foreground = TryFindResource("DangerBrush") as Brush ?? Brushes.Red;
            CountdownBar.Value = 0;
            CopyButton.IsEnabled = false;
            ExpiryHintText.Foreground = TryFindResource("DangerBrush") as Brush ?? Brushes.Red;
            ExpiryHintText.Text = "这个二维码已经失效，手机此时扫码也无法配对。请关闭本窗口，重新点「创建配对信息」生成新的二维码。";
            return;
        }

        CountdownText.Text = $"{(int)remaining.TotalMinutes:00}:{remaining.Seconds:00}";
        CountdownBar.Value = Math.Clamp(remaining.TotalSeconds / _window.TotalSeconds, 0, 1);

        if (remaining <= WarningThreshold)
        {
            CountdownText.Foreground = TryFindResource("DangerBrush") as Brush ?? Brushes.Red;
            ExpiryHintText.Text = "马上就要过期了：请在手机上完成扫码，并立即在电脑上点「信任此设备」。";
        }
    }

    private static BitmapImage CreateQrImage(string payload)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        using var qrCode = new PngByteQRCode(data);
        var bytes = qrCode.GetGraphic(8);
        using var stream = new MemoryStream(bytes, writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
