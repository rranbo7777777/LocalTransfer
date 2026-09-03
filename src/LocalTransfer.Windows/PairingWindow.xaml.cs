using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using LocalTransfer.Contracts.Pairing;
using QRCoder;

namespace LocalTransfer.Windows;

public partial class PairingWindow : Window
{
    private readonly string _payload;

    public PairingWindow(PairingBootstrap bootstrap, string payload)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        _payload = payload;
        InitializeComponent();
        EndpointText.Text = bootstrap.Endpoint;
        FingerprintText.Text = bootstrap.CertificateSha256;
        QrImage.Source = CreateQrImage(payload);
    }

    private void OnCopyClicked(object sender, RoutedEventArgs e)
    {
        System.Windows.Clipboard.SetText(_payload);
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

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
