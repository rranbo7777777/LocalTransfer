using ZXing.Net.Maui;

namespace LocalTransfer.Mobile;

public partial class QrScannerPage : ContentPage
{
    private readonly TaskCompletionSource<string?> _result =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _handled;

    public QrScannerPage()
    {
        InitializeComponent();
        CameraView.Options = new BarcodeReaderOptions
        {
            Formats = BarcodeFormats.TwoDimensional,
            AutoRotate = true,
            Multiple = false
        };
    }

    public Task<string?> WaitForResultAsync() => _result.Task;

    private void OnBarcodesDetected(object sender, BarcodeDetectionEventArgs e)
    {
        var value = e.Results.FirstOrDefault()?.Value;
        if (string.IsNullOrWhiteSpace(value) || Interlocked.Exchange(ref _handled, 1) != 0)
        {
            return;
        }

        CameraView.IsDetecting = false;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            _result.TrySetResult(value);
            await Navigation.PopModalAsync();
        });
    }

    private async void OnCancelClicked(object sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _handled, 1) != 0)
        {
            return;
        }

        CameraView.IsDetecting = false;
        _result.TrySetResult(null);
        await Navigation.PopModalAsync();
    }
}
