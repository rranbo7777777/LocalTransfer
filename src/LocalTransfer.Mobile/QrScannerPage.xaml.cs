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

    protected override bool OnBackButtonPressed()
    {
        // The OS back gesture pops the modal without hitting Cancel; without this the
        // awaiting PairAsync would never resume and the page would stay disabled.
        CompleteAndClose(null);
        return true;
    }

    private void OnBarcodesDetected(object sender, BarcodeDetectionEventArgs e)
    {
        var value = e.Results.FirstOrDefault()?.Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        CompleteAndClose(value);
    }

    private void OnCancelClicked(object sender, EventArgs e)
    {
        CompleteAndClose(null);
    }

    private void CompleteAndClose(string? value)
    {
        if (Interlocked.Exchange(ref _handled, 1) != 0)
        {
            return;
        }

        CameraView.IsDetecting = false;
        _result.TrySetResult(value);
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                if (Navigation.ModalStack.Count > 0)
                {
                    await Navigation.PopModalAsync();
                }
            }
            catch
            {
                // Barcode detection and cancel can race; whichever pops first wins.
            }
        });
    }
}
