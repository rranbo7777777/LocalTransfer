using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LocalTransfer.Contracts.Pairing;
using LocalTransfer.Contracts.Protocol;
using LocalTransfer.Contracts.Transfers;
using LocalTransfer.Coordinator;
using LocalTransfer.Coordinator.Devices;
using LocalTransfer.Coordinator.History;
using LocalTransfer.Coordinator.Pairing;
using LocalTransfer.Coordinator.Transfers;
using Microsoft.Win32;
// WinForms is referenced by this project as well, so the WPF types below need explicit aliases.
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace LocalTransfer.Windows;

public partial class MainWindow : Window
{
    private readonly CoordinatorHost _coordinator;
    private readonly DispatcherTimer _transfersRefreshTimer;

    private AppPage _currentPage = AppPage.Send;

    public MainWindow(CoordinatorHost coordinator)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        InitializeComponent();
        DataContext = this;
        _coordinator.Pairing.PairingRequested += OnPairingRequested;
        _coordinator.InboundTransfers.TransferOffered += OnTransferOffered;
        _coordinator.InboundTransfers.BatchOffered += OnBatchOffered;
        Closed += OnWindowClosed;

        // The queue and the inbound state both move while the user watches this page, so poll
        // only while it is visible instead of refreshing every page on a timer.
        _transfersRefreshTimer = new DispatcherTimer { Interval = TransferTuning.DesktopTransfersRefreshInterval };
        _transfersRefreshTimer.Tick += OnTransfersRefreshTick;

        RefreshTrustedDevices();
        ServiceStatusText.Text = _coordinator.IsRunning ? "服务运行中" : "服务未启动";
        ServiceDot.Fill = TryFindResource(_coordinator.IsRunning ? "SuccessBrush" : "DangerBrush") as Brush
                          ?? Brushes.Gray;
        ServiceStatusText.ToolTip = $"本机服务地址：{_coordinator.Endpoint}";
        ShowPage(AppPage.Send);
    }

    public ObservableCollection<PendingFileItem> PendingFiles { get; } = [];

    public ObservableCollection<TrustedDeviceInfo> TrustedDevices { get; } = [];

    public ObservableCollection<TrustedDeviceRow> TrustedDeviceRows { get; } = [];

    public ObservableCollection<TransferRow> OutboundRows { get; } = [];

    public ObservableCollection<TransferRow> InboundRows { get; } = [];

    public ObservableCollection<HistoryRow> HistoryRows { get; } = [];

    // ---------------------------------------------------------------- 导航

    private void OnNavClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        ShowPage(button.Name switch
        {
            nameof(NavDevicesButton) => AppPage.Devices,
            nameof(NavTransfersButton) => AppPage.Transfers,
            nameof(NavHistoryButton) => AppPage.History,
            nameof(NavSettingsButton) => AppPage.Settings,
            _ => AppPage.Send
        });
    }

    private void ShowPage(AppPage page)
    {
        _currentPage = page;

        PageSend.Visibility = page == AppPage.Send ? Visibility.Visible : Visibility.Collapsed;
        PageDevices.Visibility = page == AppPage.Devices ? Visibility.Visible : Visibility.Collapsed;
        PageTransfers.Visibility = page == AppPage.Transfers ? Visibility.Visible : Visibility.Collapsed;
        PageHistory.Visibility = page == AppPage.History ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = page == AppPage.Settings ? Visibility.Visible : Visibility.Collapsed;

        (PageTitleText.Text, PageSubtitleText.Text) = page switch
        {
            AppPage.Devices => (
                "设备",
                "已配对、可直接互传的设备。移除后该设备需要重新扫码配对。"),
            AppPage.Transfers => (
                "传输任务",
                "进行中的传输。发送队列会保存到本地，重启程序后仍留在队列里的文件会自动恢复。"),
            AppPage.History => (
                "历史记录",
                "手机与电脑之间收发过的文件，两个方向都记录。"),
            AppPage.Settings => (
                "设置",
                "服务地址、目录与证书信息（当前为只读）。"),

            // Send is the default page: two sentences, split at the sentence boundary so the wrap
            // lands where it reads, instead of orphaning the last few characters on a second line.
            _ => (
                "发送文件",
                "电脑 → 手机：选好设备与文件后点「发送」，再在手机上点「接收电脑文件」。"
                + Environment.NewLine
                + "手机 → 电脑：在手机上发送后，这里会弹窗让你确认。")
        };

        HighlightNav(page);

        switch (page)
        {
            case AppPage.Devices:
                RefreshDeviceRows();
                break;
            case AppPage.Transfers:
                RefreshTransferRows();
                _transfersRefreshTimer.Start();
                break;
            case AppPage.History:
                RefreshHistoryRows();
                break;
            case AppPage.Settings:
                RefreshSettings();
                break;
        }

        if (page != AppPage.Transfers)
        {
            _transfersRefreshTimer.Stop();
        }
    }

    private void HighlightNav(AppPage page)
    {
        var highlight = new SolidColorBrush(Color.FromRgb(0x27, 0x3B, 0x63));
        var entries = new (AppPage Page, Button Button)[]
        {
            (AppPage.Send, NavSendButton),
            (AppPage.Devices, NavDevicesButton),
            (AppPage.Transfers, NavTransfersButton),
            (AppPage.History, NavHistoryButton),
            (AppPage.Settings, NavSettingsButton)
        };

        foreach (var (entryPage, button) in entries)
        {
            var isActive = entryPage == page;
            button.Background = isActive ? highlight : Brushes.Transparent;
            button.FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    // ---------------------------------------------------------------- 发送文件

    private void OnChooseFilesClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            Multiselect = true,
            Title = "选择要发送的文件"
        };

        if (dialog.ShowDialog(this) == true)
        {
            AddFiles(dialog.FileNames);
        }
    }

    private void OnFilesDragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFilesDropped(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            AddFiles(paths);
        }
    }

    private void OnClearFilesClicked(object sender, RoutedEventArgs e)
    {
        PendingFiles.Clear();
        UpdateQueueSummary();
    }

    private async void OnSendClicked(object sender, RoutedEventArgs e)
    {
        if (DeviceComboBox.SelectedItem is not TrustedDeviceInfo device || PendingFiles.Count == 0)
        {
            return;
        }

        SendButton.IsEnabled = false;
        SendButton.Content = "正在校验…";
        // Progress<double> posts back to the WPF dispatcher, so multi-gigabyte hashing shows
        // percentage movement on the button instead of a frozen "正在校验…".
        var hashProgress = new Progress<double>(percent =>
            SendButton.Content = percent >= 100 ? "校验完成…" : $"正在校验 {percent:0}%");
        var queuedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in PendingFiles.ToArray())
            {
                await _coordinator.OutboundTransfers.EnqueueAsync(
                    device.DeviceId,
                    file.FullPath,
                    hashProgress);
                queuedPaths.Add(file.FullPath);
            }

            foreach (var file in PendingFiles.Where(file => queuedPaths.Contains(file.FullPath)).ToArray())
            {
                PendingFiles.Remove(file);
            }

            MessageBox.Show(
                $"已将 {queuedPaths.Count} 个文件加入发送队列。\n手机保持应用打开后会主动下载，" +
                "可在左侧「传输任务」查看进度。",
                "已加入发送队列",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            foreach (var file in PendingFiles.Where(file => queuedPaths.Contains(file.FullPath)).ToArray())
            {
                PendingFiles.Remove(file);
            }

            MessageBox.Show(
                $"已有 {queuedPaths.Count} 个文件成功入队，后续文件处理失败：{exception.Message}",
                "发送队列错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SendButton.Content = "发送";
            UpdateQueueSummary();
        }
    }

    private void OnDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSendButton();
    }

    private void OnCreatePairingClicked(object sender, RoutedEventArgs e)
    {
        var bootstrap = _coordinator.CreatePairingBootstrap();
        var json = JsonSerializer.Serialize(bootstrap, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        var window = new PairingWindow(bootstrap, json)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void AddFiles(IEnumerable<string> paths)
    {
        var existingPaths = PendingFiles
            .Select(item => item.FullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths.Where(File.Exists))
        {
            var fullPath = Path.GetFullPath(path);
            if (!existingPaths.Add(fullPath))
            {
                continue;
            }

            var file = new FileInfo(fullPath);
            PendingFiles.Add(new PendingFileItem(
                fullPath,
                file.Name,
                file.DirectoryName ?? string.Empty,
                FormatSize(file.Length)));
        }

        UpdateQueueSummary();
    }

    private void UpdateQueueSummary()
    {
        EmptyQueueText.Visibility = PendingFiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        QueueSummaryText.Text = $"{PendingFiles.Count} 个文件";
        UpdateSendButton();
    }

    private void UpdateSendButton()
    {
        var hasDevice = DeviceComboBox.SelectedItem is TrustedDeviceInfo;
        SendButton.IsEnabled = PendingFiles.Count > 0 && hasDevice;

        // "发送" stays greyed out until both a device and a file are present. Say which half is
        // missing, otherwise a disabled button reads as a broken application.
        PairingHintText.Text = TrustedDevices.Count == 0
            ? "还没有配对过的手机。点右侧「创建配对信息」，用手机扫码；手机提出请求后这里会弹窗，确认后设备才会出现在左边的列表里。"
            : !hasDevice
                ? "请在上方选择一台可信设备。"
                : PendingFiles.Count == 0
                    ? "请先添加要发送的文件。"
                    : "已就绪：点「发送」入队，随后在手机上点「接收电脑文件」。";

        SendButton.ToolTip = SendButton.IsEnabled
            ? "把「待发送文件」加入发送队列，手机会主动下载"
            : PairingHintText.Text;
    }

    // ---------------------------------------------------------------- 设备

    private void OnRefreshDevicesClicked(object sender, RoutedEventArgs e) => RefreshDeviceRows();

    private void RefreshDeviceRows()
    {
        TrustedDeviceRows.Clear();
        foreach (var device in TrustedDevices)
        {
            var paired = device.PairedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            var lastSeen = device.LastSeenUtc is { } seen
                ? $"最近活动 {seen.ToLocalTime():yyyy-MM-dd HH:mm}"
                : "尚未连接过";
            TrustedDeviceRows.Add(new TrustedDeviceRow(
                device.DeviceId,
                device.DisplayName,
                $"{device.Platform} · 配对于 {paired} · {lastSeen}"));
        }

        EmptyDevicesText.Visibility = TrustedDeviceRows.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void OnRemoveDeviceClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TrustedDeviceRow row)
        {
            return;
        }

        var confirmation = MessageBox.Show(
            $"确定移除设备「{row.DisplayName}」吗？\n\n移除后该设备需要重新扫码配对，队列中发给它的文件会失效。",
            "移除可信设备",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _coordinator.TrustedDevices.RemoveAsync(row.DeviceId);

            // Without this the device's queued transfers would sit at "queued" until the
            // application restarts, because it can no longer authenticate to download them.
            foreach (var transfer in _coordinator.OutboundTransfers.GetAll()
                         .Where(item => item.DeviceId == row.DeviceId))
            {
                await _coordinator.OutboundTransfers.CancelAsync(transfer.TransferId, row.DeviceId);
            }

            RefreshTrustedDevices();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"移除失败：{exception.Message}",
                "局域传输",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    // ---------------------------------------------------------------- 传输任务

    private void OnRefreshTransfersClicked(object sender, RoutedEventArgs e) => RefreshTransferRows();

    private void OnTransfersRefreshTick(object? sender, EventArgs e) => RefreshTransferRows();

    private void RefreshTransferRows()
    {
        OutboundRows.Clear();
        foreach (var transfer in _coordinator.OutboundTransfers.GetAll()
                     .Where(item => IsActiveTransfer(item.State)))
        {
            OutboundRows.Add(CreateTransferRow(
                transfer.Manifest,
                transfer.DeviceId,
                transfer.State,
                progressChunks: transfer.ChunksServed));
        }

        InboundRows.Clear();
        foreach (var transfer in _coordinator.InboundTransfers.GetAll()
                     .Where(item => IsActiveTransfer(item.State)))
        {
            InboundRows.Add(CreateTransferRow(
                transfer.Manifest,
                transfer.DeviceId,
                transfer.State,
                transfer.ExpiresAtUtc,
                transfer.ReceivedChunks));
        }

        EmptyOutboundText.Visibility = OutboundRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyInboundText.Visibility = InboundRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private TransferRow CreateTransferRow(
        FileManifest manifest,
        Guid deviceId,
        TransferState state,
        DateTimeOffset expiresAtUtc = default,
        int progressChunks = 0)
    {
        var stateText = DescribeState(state, expiresAtUtc);
        // A percentage on the row is what keeps a multi-gigabyte transfer from reading as
        // "stuck"; 0% during the first chunk is still better than a bare "传输中".
        if (state is TransferState.Transferring or TransferState.Verifying or TransferState.Completed)
        {
            var percent = manifest.ChunkCount == 0
                ? 100
                : Math.Min(100, progressChunks * 100 / manifest.ChunkCount);
            stateText = $"{stateText} · {percent}%";
        }

        return new TransferRow(
            manifest.FileName,
            $"{FindDeviceName(deviceId)} · {FormatSize(manifest.Length)}",
            stateText,
            ResolveStateSurface(state),
            ResolveStateForeground(state),
            $"{manifest.ChunkCount} 个分块");
    }

    // The row carries its own brushes instead of relying on a converter so the same state text
    // reads the same way everywhere, and stays legible in both the sent and received lists.
    private static Brush ResolveStateSurface(TransferState state) => state switch
    {
        TransferState.Transferring or TransferState.Verifying =>
            new SolidColorBrush(Color.FromRgb(0xFF, 0xF5, 0xDC)),
        TransferState.Completed =>
            new SolidColorBrush(Color.FromRgb(0xE4, 0xF6, 0xEC)),
        TransferState.Failed or TransferState.Rejected or TransferState.Canceled =>
            new SolidColorBrush(Color.FromRgb(0xFD, 0xEC, 0xEA)),
        _ => new SolidColorBrush(Color.FromRgb(0xEE, 0xF1, 0xF7))
    };

    private static Brush ResolveStateForeground(TransferState state) => state switch
    {
        TransferState.Transferring or TransferState.Verifying =>
            new SolidColorBrush(Color.FromRgb(0x8A, 0x61, 0x0E)),
        TransferState.Completed =>
            new SolidColorBrush(Color.FromRgb(0x0F, 0x6B, 0x40)),
        TransferState.Failed or TransferState.Rejected or TransferState.Canceled =>
            new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B)),
        _ => new SolidColorBrush(Color.FromRgb(0x4F, 0x5E, 0x76))
    };

    // Named "IsActiveTransfer" rather than "IsActive" to avoid hiding Window.IsActive.
    private static bool IsActiveTransfer(TransferState state) =>
        state is not (TransferState.Completed or TransferState.Failed or
            TransferState.Rejected or TransferState.Canceled);

    private string FindDeviceName(Guid deviceId) =>
        TrustedDevices.FirstOrDefault(device => device.DeviceId == deviceId)?.DisplayName
        ?? "已移除的设备";

    private static string DescribeState(TransferState state, DateTimeOffset expiresAtUtc = default)
    {
        // An unanswered prompt is not open-ended any more, so say how long is left instead of
        // letting the row read as if it will wait forever.
        if (state == TransferState.WaitingForApproval && expiresAtUtc != default)
        {
            var remaining = expiresAtUtc - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                return $"等待在此电脑上确认 · 还剩 {Math.Ceiling(remaining.TotalMinutes):0} 分钟";
            }
        }

        return state switch
        {
            TransferState.WaitingForApproval => "等待在此电脑上确认",
            TransferState.Queued => "已入队",
            TransferState.Transferring => "传输中",
            TransferState.Paused => "已暂停",
            TransferState.WaitingForConnection => "等待手机连接",
            TransferState.Verifying => "正在校验 SHA-256",
            TransferState.Completed => "已完成",
            TransferState.Failed => "失败",
            TransferState.Rejected => "已拒绝",
            TransferState.Canceled => "已取消",
            _ => state.ToString()
        };
    }

    // ---------------------------------------------------------------- 历史记录

    private void OnRefreshHistoryClicked(object sender, RoutedEventArgs e) => RefreshHistoryRows();

    private void RefreshHistoryRows()
    {
        HistoryRows.Clear();

        // Both directions come from the same append-only log now. A send the phone downloaded is as
        // visible here as a receive, and a file the user later moved or renamed no longer vanishes
        // from the history the way a directory scan used to lose it.
        foreach (var entry in _coordinator.History.Read())
        {
            HistoryRows.Add(new HistoryRow(
                entry.FileName,
                DescribeHistoryEntry(entry),
                FormatSize(entry.Length),
                DescribeOutcome(entry.Outcome),
                ResolveOutcomeSurface(entry.Outcome),
                ResolveOutcomeForeground(entry.Outcome),
                string.IsNullOrWhiteSpace(entry.Error) ? null : entry.Error));
        }

        EmptyHistoryText.Text = "还没有传输记录。手机与电脑之间收发的每个文件都会记在这里。";
        EmptyHistoryText.Visibility = HistoryRows.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>
    /// Direction, counterpart and time on one line. The outcome is a badge on the right rather than
    /// a word buried in this string, and the error text becomes the row's tooltip: that is the
    /// detail you want on hover, not a fourth thing competing for the same line.
    /// </summary>
    private string DescribeHistoryEntry(TransferHistoryEntry entry)
    {
        var device = TrustedDevices.FirstOrDefault(item => item.DeviceId == entry.DeviceId);
        var who = device?.DisplayName ?? entry.DeviceId.ToString()[..8];
        var direction = entry.Direction == TransferDirection.Inbound ? "手机 → 电脑" : "电脑 → 手机";
        return $"{direction} · {who} · {entry.AtUtc.ToLocalTime():MM-dd HH:mm}";
    }

    private static string DescribeOutcome(TransferOutcome outcome) => outcome switch
    {
        TransferOutcome.Completed => "已完成",
        TransferOutcome.Failed => "失败",
        TransferOutcome.Rejected => "未接收",
        TransferOutcome.Canceled => "已取消",
        _ => outcome.ToString()
    };

    // Matches the colour language used by the transfer rows and by the phone, so a finished
    // transfer reads the same wherever it is shown.
    private static Brush ResolveOutcomeSurface(TransferOutcome outcome) => outcome switch
    {
        TransferOutcome.Completed => new SolidColorBrush(Color.FromRgb(0xE4, 0xF6, 0xEC)),
        TransferOutcome.Failed => new SolidColorBrush(Color.FromRgb(0xFC, 0xEB, 0xEB)),
        TransferOutcome.Rejected => new SolidColorBrush(Color.FromRgb(0xFF, 0xF5, 0xDC)),
        _ => new SolidColorBrush(Color.FromRgb(0xEE, 0xF1, 0xF7))
    };

    private static Brush ResolveOutcomeForeground(TransferOutcome outcome) => outcome switch
    {
        TransferOutcome.Completed => new SolidColorBrush(Color.FromRgb(0x0F, 0x6B, 0x40)),
        TransferOutcome.Failed => new SolidColorBrush(Color.FromRgb(0xA3, 0x2D, 0x2D)),
        TransferOutcome.Rejected => new SolidColorBrush(Color.FromRgb(0x8A, 0x61, 0x0E)),
        _ => new SolidColorBrush(Color.FromRgb(0x69, 0x75, 0x8C))
    };

    // ---------------------------------------------------------------- 设置

    private void RefreshSettings()
    {
        SettingEndpointText.Text = _coordinator.Endpoint;
        SettingPortText.Text = _coordinator.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        SettingReceiveDirText.Text = _coordinator.ReceiveDirectory;
        SettingDataDirText.Text = _coordinator.DataDirectory;
        SettingLogDirText.Text = Path.Combine(_coordinator.DataDirectory, "logs");
        SettingCertText.Text = _coordinator.CertificateSha256 ?? "（协调服务未启动）";
        SettingProtocolText.Text = "1（协议版本 1）";
    }

    private void OnOpenReceiveFolderClicked(object sender, RoutedEventArgs e) =>
        OpenFolder(_coordinator.ReceiveDirectory);

    private void OnOpenDataFolderClicked(object sender, RoutedEventArgs e) =>
        OpenFolder(_coordinator.DataDirectory);

    private void OnOpenLogFolderClicked(object sender, RoutedEventArgs e) =>
        OpenFolder(Path.Combine(_coordinator.DataDirectory, "logs"));

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"无法打开目录：{exception.Message}",
                "局域传输",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    // ---------------------------------------------------------------- 配对与接收

    private void OnPairingRequested(object? sender, PairingRequestInfo request)
    {
        DispatchSafe(() => HandlePairingRequestAsync(request));
    }

    private async Task HandlePairingRequestAsync(PairingRequestInfo request)
    {
        // The dialog counts down the phone's remaining patience instead of letting the user answer
        // whenever: approving after it ran out produced a credential the phone never received,
        // after which every transfer from that device failed with an opaque 401.
        var deadline = request.ExpiresAtUtc == default
            ? request.RequestedAtUtc.AddMinutes(2)
            : request.ExpiresAtUtc;

        var dialog = new PairingApprovalWindow(request, deadline) { Owner = this };
        var approved = dialog.ShowDialog() == true;

        if (dialog.TimedOut)
        {
            MessageBox.Show(
                "没有来得及确认：手机已经停止等待了。\n\n" +
                "请在手机上重新点「配对」扫描电脑上的二维码，然后尽快在弹出的窗口里点「信任此设备」。",
                "配对已超时",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!approved)
        {
            _coordinator.Pairing.Reject(request.RequestId);
            return;
        }

        if (await _coordinator.Pairing.ApproveAsync(request.RequestId))
        {
            RefreshTrustedDevices();
            return;
        }

        // ApproveAsync refuses once the request expired, so a late click must not look like
        // success — otherwise the user believes the phone was added while it was not.
        MessageBox.Show(
            "这次配对没有完成：手机已经不再等待确认了。\n\n" +
            "请在手机上重新点「配对」并扫描电脑上的二维码，然后尽快在弹出的窗口里点「信任此设备」。",
            "配对未完成",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void DispatchSafe(Func<Task> operation)
    {
        // Dispatcher.InvokeAsync(Func<Task>) does not observe the inner task; catch here so
        // approval/rejection failures surface to the user instead of vanishing.
        _ = Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await operation();
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    $"操作失败：{exception.Message}",
                    "局域传输",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        });
    }

    private void RefreshTrustedDevices()
    {
        // Rebuilding the item source clears the ComboBox selection. Remember the current target
        // so a pairing that happens while the user is looking at the list does not silently
        // switch the destination back to the first device.
        var previousDeviceId = (DeviceComboBox.SelectedItem as TrustedDeviceInfo)?.DeviceId;

        TrustedDevices.Clear();
        foreach (var device in _coordinator.TrustedDevices.GetAll())
        {
            TrustedDevices.Add(device);
        }

        var restoredIndex = -1;
        if (previousDeviceId is { } deviceId)
        {
            for (var index = 0; index < TrustedDevices.Count; index++)
            {
                if (TrustedDevices[index].DeviceId == deviceId)
                {
                    restoredIndex = index;
                    break;
                }
            }
        }

        // Fall back to the first entry only when nothing usable was selected before.
        DeviceComboBox.SelectedIndex = restoredIndex >= 0
            ? restoredIndex
            : TrustedDevices.Count > 0
                ? 0
                : -1;

        // Keep the header counter and the first-run guidance in step with the device list.
        DeviceCountText.Text = $"{TrustedDevices.Count} 台设备";
        PairingCallout.Visibility = TrustedDevices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DevicePickerCard.Visibility = TrustedDevices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        RefreshDeviceRows();
        UpdateSendButton();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _transfersRefreshTimer.Stop();
        _coordinator.Pairing.PairingRequested -= OnPairingRequested;
        _coordinator.InboundTransfers.TransferOffered -= OnTransferOffered;
        _coordinator.InboundTransfers.BatchOffered -= OnBatchOffered;
    }

    private void OnBatchOffered(object? sender, InboundBatchInfo batch)
    {
        DispatchSafe(() => HandleBatchOfferAsync(batch));
    }

    /// <summary>
    /// Answers a batch in one dialog instead of one message box per file. Declining is still
    /// per-file: unchecking a row rejects exactly that file and accepts the rest.
    /// </summary>
    private async Task HandleBatchOfferAsync(InboundBatchInfo batch)
    {
        var device = TrustedDevices.FirstOrDefault(item => item.DeviceId == batch.DeviceId);
        var deadline = batch.ExpiresAtUtc == default
            ? DateTimeOffset.UtcNow + TransferTuning.ClientApprovalWait
            : batch.ExpiresAtUtc;

        var dialog = new BatchApprovalWindow(
            batch,
            device?.DisplayName ?? batch.DeviceId.ToString(),
            deadline)
        {
            Owner = this
        };

        var confirmed = dialog.ShowDialog() == true;

        if (dialog.TimedOut)
        {
            await _coordinator.InboundTransfers.RejectBatchAsync(batch.BatchId, batch.DeviceId);
            MessageBox.Show(
                "没有来得及确认：手机已经停止等待了。\n\n" +
                "在手机上重新点「发送」即可再来一次。",
                "接收请求已失效",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!confirmed)
        {
            await _coordinator.InboundTransfers.RejectBatchAsync(batch.BatchId, batch.DeviceId);
            return;
        }

        try
        {
            await _coordinator.InboundTransfers.ApproveBatchAsync(
                batch.BatchId,
                batch.DeviceId,
                dialog.AcceptedTransferIds);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"无法准备接收文件：{exception.Message}",
                "文件接收失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnTransferOffered(object? sender, InboundTransferInfo transfer)
    {
        DispatchSafe(() => HandleTransferOfferAsync(transfer));
    }

    private async Task HandleTransferOfferAsync(InboundTransferInfo transfer)
    {
        var device = TrustedDevices.FirstOrDefault(item => item.DeviceId == transfer.DeviceId);
        var deadline = transfer.ExpiresAtUtc == default
            ? DateTimeOffset.UtcNow + TransferTuning.ClientApprovalWait
            : transfer.ExpiresAtUtc;
        var remaining = deadline - DateTimeOffset.UtcNow;
        var patienceNote = remaining > TimeSpan.Zero
            ? $"手机只等待大约 {(int)Math.Ceiling(remaining.TotalMinutes)} 分钟，超过后这次发送会自动取消。\n\n"
            : string.Empty;

        var result = MessageBox.Show(
            $"是否接收文件？\n\n设备：{device?.DisplayName ?? transfer.DeviceId.ToString()}\n" +
            $"文件：{transfer.Manifest.FileName}\n大小：{FormatSize(transfer.Manifest.Length)}\n\n" +
            patienceNote +
            "文件通过 SHA-256 校验后才会保存到下载目录。",
            "文件接收请求",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            await _coordinator.InboundTransfers.RejectAsync(transfer.TransferId);
            return;
        }

        try
        {
            // ApproveAsync refuses once the offer has expired, so a late "yes" must not be
            // reported as success — otherwise the file appears to be on its way while the
            // phone has already given up.
            if (await _coordinator.InboundTransfers.ApproveAsync(transfer.TransferId))
            {
                return;
            }

            MessageBox.Show(
                "这次接收没有完成：手机已经不再等待了。\n\n" +
                "在手机上重新点「发送」即可再来一次；只要你在手机开始等待前点「是」，就能正常接收。",
                "接收请求已失效",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"无法准备接收文件：{exception.Message}",
                "文件接收失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
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

    private enum AppPage
    {
        Send,
        Devices,
        Transfers,
        History,
        Settings
    }
}

public sealed record PendingFileItem(
    string FullPath,
    string FileName,
    string DirectoryName,
    string SizeText);

public sealed record TrustedDeviceRow(
    Guid DeviceId,
    string DisplayName,
    string DetailText);

public sealed record TransferRow(
    string FileName,
    string DetailText,
    string StateText,
    Brush StateSurface,
    Brush StateForeground,
    string SizeText);

public sealed record HistoryRow(
    string FileName,
    string DetailText,
    string SizeText,
    string StateText,
    Brush StateSurface,
    Brush StateForeground,
    string? ErrorText);
