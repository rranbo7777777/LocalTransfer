using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using LocalTransfer.Coordinator.Transfers;

namespace LocalTransfer.Windows;

/// <summary>
/// Confirmation dialog for an incoming batch.
/// <para>
/// It exists because a batch is one question, not N questions: before this, a hundred files meant a
/// hundred message boxes, each of which had to be answered inside the client's own approval window.
/// Checking a subset is still possible - unchecking a file simply rejects it.
/// </para>
/// </summary>
public partial class BatchApprovalWindow : Window
{
    private static readonly TimeSpan WarningThreshold = TimeSpan.FromSeconds(30);

    private readonly DateTimeOffset _deadline;
    private readonly DispatcherTimer _timer;

    public BatchApprovalWindow(InboundBatchInfo batch, string deviceName, DateTimeOffset deadline)
    {
        ArgumentNullException.ThrowIfNull(batch);
        _deadline = deadline;

        InitializeComponent();

        Files = new ObservableCollection<BatchFileItem>(
            batch.Files.Select(file => new BatchFileItem(file)));
        FilesList.ItemsSource = Files;

        HeadlineText.Text = $"接收 {batch.Files.Count} 个文件？";
        DeviceText.Text = $"来自：{deviceName}";
        SummaryText.Text =
            $"共 {FormatSize(batch.Files.Sum(file => file.Manifest.Length))} · 确认一次即可全部开始";

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTimerTick;
        _timer.Start();
        UpdateRemaining();
    }

    public ObservableCollection<BatchFileItem> Files { get; }

    /// <summary>The files the user left checked. Empty means everything was declined.</summary>
    public IReadOnlySet<Guid> AcceptedTransferIds { get; private set; } = new HashSet<Guid>();

    /// <summary>True when the dialog closed itself because the phone stopped waiting.</summary>
    public bool TimedOut { get; private set; }

    private void OnTimerTick(object? sender, EventArgs e) => UpdateRemaining();

    private void UpdateRemaining()
    {
        var remaining = _deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            TimedOut = true;
            _timer.Stop();
            DialogResult = false;
            return;
        }

        // Only start counting down once it is actually urgent; a permanent ticking clock is noise.
        RemainingText.Text = remaining <= WarningThreshold
            ? $"请在 {remaining:mm\\:ss} 内确认，否则手机会放弃这次发送"
            : string.Empty;
    }

    private void OnSelectAllClicked(object sender, RoutedEventArgs e)
    {
        var select = SelectAllCheckBox.IsChecked == true;
        foreach (var file in Files)
        {
            file.IsSelected = select;
        }

        ToggleAllButton.Content = select ? "全不选" : "全选";
    }

    private void OnToggleAllClicked(object sender, RoutedEventArgs e)
    {
        var select = Files.Any(file => !file.IsSelected);
        SelectAllCheckBox.IsChecked = select;
        foreach (var file in Files)
        {
            file.IsSelected = select;
        }

        ToggleAllButton.Content = select ? "全不选" : "全选";
    }

    private void OnAcceptClicked(object sender, RoutedEventArgs e)
    {
        AcceptedTransferIds = new HashSet<Guid>(
            Files.Where(file => file.IsSelected).Select(file => file.Transfer.TransferId));
        DialogResult = true;
    }

    private void OnRejectClicked(object sender, RoutedEventArgs e)
    {
        AcceptedTransferIds = new HashSet<Guid>();
        DialogResult = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }

    private static string FormatSize(long length)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)length;
        var unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:0.##} {units[unitIndex]}";
    }
}

/// <summary>One row of the batch list. Checked means "receive this one".</summary>
public sealed class BatchFileItem : INotifyPropertyChanged
{
    private bool _isSelected = true;

    public BatchFileItem(InboundTransferInfo transfer)
    {
        Transfer = transfer ?? throw new ArgumentNullException(nameof(transfer));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public InboundTransferInfo Transfer { get; }

    public string FileName => Transfer.Manifest.FileName;

    public string SizeText => FormatSize(Transfer.Manifest.Length);

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    private static string FormatSize(long length)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)length;
        var unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:0.##} {units[unitIndex]}";
    }
}
