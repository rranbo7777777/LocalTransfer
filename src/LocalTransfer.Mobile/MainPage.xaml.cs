using System.Text.Json;
using LocalTransfer.Client;
using LocalTransfer.Contracts.Devices;
using LocalTransfer.Contracts.Pairing;
using LocalTransfer.Contracts.Protocol;

namespace LocalTransfer.Mobile;

public partial class MainPage : ContentPage
{
	private readonly List<SharedSourceFile> _selectedFiles = [];
	private CoordinatorConnection? _connection;
	private LocalTransferClient? _client;

	public MainPage()
	{
		InitializeComponent();
		SharedFileInbox.FilesAvailable += OnSharedFilesAvailable;
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		ConsumeSharedFiles();
		if (_connection is not null)
		{
			return;
		}

		try
		{
			var connection = await MobileConnectionStore.LoadAsync();
			if (connection is not null)
			{
				SetConnection(connection);
			}
		}
		catch (Exception exception)
		{
			await DisplayAlert("无法读取安全凭据", exception.Message, "确定");
		}
	}

	private async void OnPairClicked(object sender, EventArgs e)
	{
		PairButton.IsEnabled = false;
		try
		{
			var method = await DisplayActionSheet(
				"选择配对方式",
				"取消",
				null,
				"扫描电脑二维码",
				"粘贴配对信息");
			string? json;
			if (method == "扫描电脑二维码")
			{
				var scanner = new QrScannerPage();
				await Navigation.PushModalAsync(new NavigationPage(scanner));
				json = await scanner.WaitForResultAsync();
			}
			else if (method == "粘贴配对信息")
			{
				json = await Clipboard.Default.GetTextAsync();
			}
			else
			{
				return;
			}

			var bootstrap = string.IsNullOrWhiteSpace(json)
				? null
				: JsonSerializer.Deserialize<PairingBootstrap>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
			if (bootstrap is null)
			{
				throw new InvalidDataException("没有读取到有效的局域传输配对信息。");
			}

			ConnectionStatusLabel.Text = "等待电脑批准…";
			var device = new DeviceDescriptor(
				MobileConnectionStore.GetOrCreateDeviceId(),
				DeviceInfo.Current.Name,
				DeviceInfo.Current.Platform.ToString(),
				ProtocolConstants.CurrentVersion,
				DeviceCapabilities.Upload | DeviceCapabilities.Download | DeviceCapabilities.Resume);
			var connection = await PairingClient.PairAsync(bootstrap, device);
			await MobileConnectionStore.SaveAsync(connection);
			SetConnection(connection);
			await DisplayAlert("配对成功", "电脑已保存为可信设备，凭据已写入系统安全存储。", "确定");
		}
		catch (Exception exception)
		{
			ConnectionStatusLabel.Text = _connection is null ? "未连接" : "已连接";
			await DisplayAlert("配对失败", exception.Message, "确定");
		}
		finally
		{
			PairButton.IsEnabled = true;
		}
	}

	private async void OnChooseFilesClicked(object sender, EventArgs e)
	{
		try
		{
			var results = await FilePicker.Default.PickMultipleAsync(new PickOptions
			{
				PickerTitle = "选择要发送的文件"
			});

			if (results is null)
			{
				return;
			}

			var selected = new List<SharedSourceFile>();
			foreach (var result in results)
			{
				long? length = null;
				await using var stream = await result.OpenReadAsync();
				if (stream.CanSeek)
				{
					length = stream.Length;
				}

				selected.Add(new SharedSourceFile(
					result.FileName,
					string.IsNullOrWhiteSpace(result.ContentType) ? "未知类型" : result.ContentType,
					length,
					_ => result.OpenReadAsync()));
			}

			SetSelectedFiles(selected, replace: true);
		}
		catch (Exception exception)
		{
			await DisplayAlert("无法选择文件", exception.Message, "确定");
		}
	}

	private async void OnSendClicked(object sender, EventArgs e)
	{
		if (_client is null || _selectedFiles.Count == 0)
		{
			return;
		}

		SetBusy(true, "正在准备文件…");
		try
		{
			foreach (var file in _selectedFiles)
			{
				var progress = new Progress<TransferProgress>(value =>
				{
					var percentage = value.TotalBytes == 0
						? 100
						: (int)(value.BytesTransferred * 100 / value.TotalBytes);
					TransferStatusLabel.Text = $"正在发送 {value.FileName} · {percentage}%";
				});
				await _client.UploadAsync(
					file.FileName,
					file.OpenReadAsync,
					progress: progress);
			}

			_selectedFiles.Clear();
			SelectedFilesView.ItemsSource = null;
			SelectedFilesView.IsVisible = false;
			EmptySelectionLabel.IsVisible = true;
			SelectedFilesSummary.Text = "0 个文件";
			await DisplayAlert("发送完成", "所有文件均已通过 SHA-256 校验并保存到电脑。", "确定");
		}
		catch (Exception exception)
		{
			await DisplayAlert("发送失败", exception.Message, "确定");
		}
		finally
		{
			SetBusy(false, string.Empty);
		}
	}

	private void OnSharedFilesAvailable(object? sender, EventArgs e)
	{
		MainThread.BeginInvokeOnMainThread(ConsumeSharedFiles);
	}

	private void ConsumeSharedFiles()
	{
		var files = SharedFileInbox.Drain();
		if (files.Count > 0)
		{
			SetSelectedFiles(files, replace: false);
		}
	}

	private void SetSelectedFiles(IEnumerable<SharedSourceFile> files, bool replace)
	{
		if (replace)
		{
			_selectedFiles.Clear();
		}

		_selectedFiles.AddRange(files);
		var items = _selectedFiles
			.Select(file => new SelectedFileItem(
				file.FileName,
				string.IsNullOrWhiteSpace(file.ContentType) ? "未知类型" : file.ContentType,
				file.Length.HasValue ? FormatSize(file.Length.Value) : "大小未知"))
			.ToArray();
		SelectedFilesView.ItemsSource = items;
		SelectedFilesView.IsVisible = items.Length > 0;
		EmptySelectionLabel.IsVisible = items.Length == 0;
		SelectedFilesSummary.Text = $"{items.Length} 个文件";
		UpdateActions();
	}

	private async void OnReceiveClicked(object sender, EventArgs e)
	{
		if (_client is null)
		{
			return;
		}

		SetBusy(true, "正在检查电脑发送队列…");
		try
		{
			var transfers = await _client.GetAvailableDownloadsAsync();
			if (transfers.Count == 0)
			{
				await DisplayAlert("没有待接收文件", "电脑当前没有为这台手机排队的文件。", "确定");
				return;
			}

			var destination = Path.Combine(FileSystem.AppDataDirectory, "Received");
			foreach (var transfer in transfers)
			{
				var accept = await DisplayAlert(
					"接收电脑文件",
					$"{transfer.Manifest.FileName}\n{FormatSize(transfer.Manifest.Length)}",
					"接收",
					"稍后");
				if (!accept)
				{
					continue;
				}

				var progress = new Progress<TransferProgress>(value =>
				{
					var percentage = value.TotalBytes == 0
						? 100
						: (int)(value.BytesTransferred * 100 / value.TotalBytes);
					TransferStatusLabel.Text = $"正在接收 {value.FileName} · {percentage}%";
				});
				var finalPath = await _client.DownloadAsync(transfer, destination, progress);
				var share = await DisplayAlert(
					"接收完成",
					$"{transfer.Manifest.FileName} 已完成校验。是否打开系统分享/保存菜单？",
					"打开",
					"完成");
				if (share)
				{
					await Share.Default.RequestAsync(new ShareFileRequest
					{
						Title = "保存或分享接收的文件",
						File = new ShareFile(finalPath)
					});
				}
			}
		}
		catch (Exception exception)
		{
			await DisplayAlert("接收失败", exception.Message, "确定");
		}
		finally
		{
			SetBusy(false, string.Empty);
		}
	}

	private void SetConnection(CoordinatorConnection connection)
	{
		_client?.Dispose();
		_connection = connection;
		_client = new LocalTransferClient(connection);
		ConnectionStatusLabel.Text = "已连接";
		ComputerNameLabel.Text = new Uri(connection.Endpoint).Host;
		ComputerDetailLabel.Text = connection.Endpoint;
		UpdateActions();
	}

	private void SetBusy(bool isBusy, string status)
	{
		SendButton.IsEnabled = !isBusy && _client is not null && _selectedFiles.Count > 0;
		ReceiveButton.IsEnabled = !isBusy && _client is not null;
		PairButton.IsEnabled = !isBusy;
		TransferStatusLabel.IsVisible = isBusy;
		TransferStatusLabel.Text = status;
	}

	private void UpdateActions()
	{
		SendButton.IsEnabled = _client is not null && _selectedFiles.Count > 0;
		SendButton.Text = _client is null ? "连接电脑后发送" : "发送到电脑";
		ReceiveButton.IsEnabled = _client is not null;
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

public sealed record SelectedFileItem(string FileName, string ContentType, string SizeText);

