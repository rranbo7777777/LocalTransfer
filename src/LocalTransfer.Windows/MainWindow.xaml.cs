using System.Text;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using LocalTransfer.Coordinator;
using LocalTransfer.Coordinator.Devices;
using LocalTransfer.Coordinator.Pairing;
using LocalTransfer.Coordinator.Transfers;
using LocalTransfer.Contracts.Pairing;
using Microsoft.Win32;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace LocalTransfer.Windows;

public partial class MainWindow : Window
{
    private readonly CoordinatorHost _coordinator;

    public MainWindow(CoordinatorHost coordinator)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        InitializeComponent();
        DataContext = this;
        _coordinator.Pairing.PairingRequested += OnPairingRequested;
        _coordinator.InboundTransfers.TransferOffered += OnTransferOffered;
        Closed += OnWindowClosed;
        RefreshTrustedDevices();
        ServiceStatusText.Text = $"本机服务 {_coordinator.Endpoint}";
    }

    public ObservableCollection<PendingFileItem> PendingFiles { get; } = [];

    public ObservableCollection<TrustedDeviceInfo> TrustedDevices { get; } = [];

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
                $"已将 {queuedPaths.Count} 个文件加入发送队列。\n手机保持应用打开后会主动下载。",
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

    private void OnDeviceSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
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
        SendButton.IsEnabled = PendingFiles.Count > 0 && DeviceComboBox.SelectedItem is TrustedDeviceInfo;
    }

    private void OnPairingRequested(object? sender, PairingRequestInfo request)
    {
        _ = Dispatcher.InvokeAsync(
            () => HandlePairingRequestAsync(request),
            DispatcherPriority.Normal);
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

    private void RefreshTrustedDevices()
    {
        TrustedDevices.Clear();
        foreach (var device in _coordinator.TrustedDevices.GetAll())
        {
            TrustedDevices.Add(device);
        }

        if (TrustedDevices.Count > 0)
        {
            DeviceComboBox.SelectedIndex = 0;
        }

        UpdateSendButton();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _coordinator.Pairing.PairingRequested -= OnPairingRequested;
        _coordinator.InboundTransfers.TransferOffered -= OnTransferOffered;
    }

    private void OnTransferOffered(object? sender, InboundTransferInfo transfer)
    {
        _ = Dispatcher.InvokeAsync(
            () => HandleTransferOfferAsync(transfer),
            DispatcherPriority.Normal);
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
}

public sealed record PendingFileItem(
    string FullPath,
    string FileName,
    string DirectoryName,
    string SizeText);
