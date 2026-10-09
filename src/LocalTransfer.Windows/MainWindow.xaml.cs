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
using LocalTransfer.Contracts.Transfers;
using LocalTransfer.Coordinator;
using LocalTransfer.Coordinator.Devices;
using LocalTransfer.Coordinator.Pairing;
using LocalTransfer.Coordinator.Transfers;
using Microsoft.Win32;
// WinForms is referenced by this project as well, so the WPF types below need explicit aliases.
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
        Closed += OnWindowClosed;

        // The queue and the inbound state both move while the user watches this page, so poll
        // only while it is visible instead of refreshing every page on a timer.
        _transfersRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _transfersRefreshTimer.Tick += OnTransfersRefreshTick;

        RefreshTrustedDevices();
        ServiceStatusText.Text = $"本机服务 {_coordinator.Endpoint}";
        ShowPage(AppPage.Send);
    }

    public ObservableCollection<PendingFileItem> PendingFiles { get; } = [];

    public ObservableCollection<TrustedDeviceInfo> TrustedDevices { get; } = [];

    public ObservableCollection<TrustedDeviceRow> TrustedDeviceRows { get; } = [];

    public ObservableCollection<TransferRow> OutboundRows { get; } = [];

    public ObservableCollection<TransferRow> InboundRows { get; } = [];

    public ObservableCollection<ReceivedFileRow> ReceivedRows { get; } = [];

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
                "进行中的传输。发送队列只保存在内存里，退出程序后需要重新排队。"),
            AppPage.History => (
                "历史记录",
                $"电脑已经收到的文件，保存在 {_coordinator.ReceiveDirectory}。"),
            AppPage.Settings => (
                "设置",
                "服务地址、目录与证书信息（当前为只读）。"),
            _ => (
                "发送文件",
                "电脑 → 手机：选好设备与文件后点「发送」，再在手机上点「接收电脑文件」。" +
                "手机 → 电脑：在手机上发送后，这里会弹窗让你确认。")
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
        var queuedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in PendingFiles.ToArray())
            {
                await _coordinator.OutboundTransfers.EnqueueAsync(device.DeviceId, file.FullPath);
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
            OutboundRows.Add(new TransferRow(
                transfer.Manifest.FileName,
                $"{FindDeviceName(transfer.DeviceId)} · {DescribeState(transfer.State)}",
                FormatSize(transfer.Manifest.Length)));
        }

        InboundRows.Clear();
        foreach (var transfer in _coordinator.InboundTransfers.GetAll()
                     .Where(item => IsActiveTransfer(item.State)))
        {
            InboundRows.Add(new TransferRow(
                transfer.Manifest.FileName,
                $"{FindDeviceName(transfer.DeviceId)} · {DescribeState(transfer.State)}",
                FormatSize(transfer.Manifest.Length)));
        }

        EmptyOutboundText.Visibility = OutboundRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyInboundText.Visibility = InboundRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Named "IsActiveTransfer" rather than "IsActive" to avoid hiding Window.IsActive.
    private static bool IsActiveTransfer(TransferState state) =>
        state is not (TransferState.Completed or TransferState.Failed or
            TransferState.Rejected or TransferState.Canceled);

    private string FindDeviceName(Guid deviceId) =>
        TrustedDevices.FirstOrDefault(device => device.DeviceId == deviceId)?.DisplayName
        ?? "已移除的设备";

    private static string DescribeState(TransferState state) => state switch
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

    // ---------------------------------------------------------------- 历史记录

    private void OnRefreshHistoryClicked(object sender, RoutedEventArgs e) => RefreshHistoryRows();

    private void RefreshHistoryRows()
    {
        ReceivedRows.Clear();
        string? error = null;
        try
        {
            var directory = _coordinator.ReceiveDirectory;
            if (Directory.Exists(directory))
            {
                // Finished files only: the receiver keeps ".part" payloads and checkpoint JSON in
                // a ".localtransfer" subdirectory, and this listing does not recurse.
                var entries = new List<ReceivedFileRow>();
                foreach (var path in Directory.EnumerateFiles(directory))
                {
                    try
                    {
                        var file = new FileInfo(path);
                        entries.Add(new ReceivedFileRow(
                            file.Name,
                            FormatSize(file.Length),
                            file.LastWriteTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                            file.FullName));
                    }
                    catch (Exception)
                    {
                        // A file that disappeared between listing and inspection is skipped.
                    }
                }

                entries.Sort((left, right) => string.CompareOrdinal(right.TimeText, left.TimeText));
                foreach (var entry in entries)
                {
                    ReceivedRows.Add(entry);
                }
            }
        }
        catch (Exception exception)
        {
            // Reading history must never take the window down.
            error = exception.Message;
        }

        EmptyHistoryText.Text = error is null
            ? "还没有收到过文件。手机向电脑发送后，会把文件保存到接收目录。"
            : $"无法读取接收目录：{error}";
        EmptyHistoryText.Visibility = ReceivedRows.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // ---------------------------------------------------------------- 设置

    private void RefreshSettings()
    {
        SettingEndpointText.Text = _coordinator.Endpoint;
        SettingPortText.Text = _coordinator.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        SettingReceiveDirText.Text = _coordinator.ReceiveDirectory;
        SettingDataDirText.Text = _coordinator.DataDirectory;
        SettingCertText.Text = _coordinator.CertificateSha256 ?? "（协调服务未启动）";
        SettingProtocolText.Text = "1（协议版本 1）";
    }

    private void OnOpenReceiveFolderClicked(object sender, RoutedEventArgs e) =>
        OpenFolder(_coordinator.ReceiveDirectory);

    private void OnOpenDataFolderClicked(object sender, RoutedEventArgs e) =>
        OpenFolder(_coordinator.DataDirectory);

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
        var result = MessageBox.Show(
            $"是否信任设备？\n\n名称：{request.Device.DisplayName}\n平台：{request.Device.Platform}\n" +
            $"设备ID：{request.Device.DeviceId}",
            "设备配对请求",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            await _coordinator.Pairing.ApproveAsync(request.RequestId);
            RefreshTrustedDevices();
        }
        else
        {
            _coordinator.Pairing.Reject(request.RequestId);
        }
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

        RefreshDeviceRows();
        UpdateSendButton();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _transfersRefreshTimer.Stop();
        _coordinator.Pairing.PairingRequested -= OnPairingRequested;
        _coordinator.InboundTransfers.TransferOffered -= OnTransferOffered;
    }

    private void OnTransferOffered(object? sender, InboundTransferInfo transfer)
    {
        DispatchSafe(() => HandleTransferOfferAsync(transfer));
    }

    private async Task HandleTransferOfferAsync(InboundTransferInfo transfer)
    {
        var device = TrustedDevices.FirstOrDefault(item => item.DeviceId == transfer.DeviceId);
        var result = MessageBox.Show(
            $"是否接收文件？\n\n设备：{device?.DisplayName ?? transfer.DeviceId.ToString()}\n" +
            $"文件：{transfer.Manifest.FileName}\n大小：{FormatSize(transfer.Manifest.Length)}\n\n" +
            "文件通过 SHA-256 校验后才会保存到下载目录。",
            "文件接收请求",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            try
            {
                await _coordinator.InboundTransfers.ApproveAsync(transfer.TransferId);
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
        else
        {
            await _coordinator.InboundTransfers.RejectAsync(transfer.TransferId);
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
    string SizeText);

public sealed record ReceivedFileRow(
    string FileName,
    string SizeText,
    string TimeText,
    string FullPath);
