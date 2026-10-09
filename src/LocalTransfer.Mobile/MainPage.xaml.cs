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
	private bool _connectionLoadStarted;

	public MainPage()
	{
		InitializeComponent();
		SharedFileInbox.FilesAvailable += OnSharedFilesAvailable;
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		ConsumeSharedFiles();
		// OnAppearing fires again when modals close; without this guard a slow SecureStorage
		// load can complete after a fresh pairing and overwrite the new connection with stale data.
		if (_connection is not null || _connectionLoadStarted)
		{
			return;
		}

		_connectionLoadStarted = true;
		try
		{
			var connection = await MobileConnectionStore.LoadAsync();
			if (connection is not null && _connection is null)
			{
				SetConnection(connection);
			}
		}
		catch (Exception exception)
		{
			MobileDiagnostics.Log("secure-storage", exception);
			ShowDeferredAlert("无法读取安全凭据", exception.Message);
		}
	}

	/// <summary>
	/// Showing a modal dialog straight from <c>OnAppearing</c> races the Android page transition
	/// (the page is not attached to the window yet) and can itself throw a Java exception, which
	/// escapes this Java-invoked callback as "exception_wasthrown". Post the dialog instead, and
	/// never let reporting a failure become a new failure.
	/// </summary>
	private void ShowDeferredAlert(string title, string message)
	{
		MainThread.BeginInvokeOnMainThread(async () =>
		{
			try
			{
				await DisplayAlert(title, message, "确定");
			}
			catch (Exception exception)
			{
				MobileDiagnostics.Log("alert", exception);
			}
		});
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
				if (!await QrScannerPage.EnsureCameraPermissionAsync())
				{
					await DisplayAlert(
						"无法打开相机",
						"局域传输没有相机权限，无法扫描二维码。\n\n" +
						"可在手机「设置 → 应用 → 局域传输 → 权限」中授予相机权限；也可以改用电：在电脑上点「复制配对信息」，" +
						"把复制到的内容发到手机，再回到这里选择「粘贴配对信息」。",
						"确定");
					return;
				}

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
			ShowPairingStatus();
			var device = new DeviceDescriptor(
				// Reuse the identifier of an existing pairing when there is one: the desktop
				// keys trusted devices by this value, so a fresh identifier would register a
				// duplicate entry for the same phone instead of updating the existing one.
				MobileConnectionStore.GetOrCreateDeviceId(_connection?.DeviceId),
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
			if (_connection is null)
			{
				ShowDisconnectedStatus();
			}
			else
			{
				ShowConnectedStatus();
			}

			MobileDiagnostics.Log("pairing", exception);
			await DisplayAlert("配对失败", DescribePairingFailure(exception), "确定");
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
			MobileDiagnostics.Log("choose-files", exception);
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
				var progress = new Progress<TransferProgress>(value => ReportProgress("正在发送", value));
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
		catch (CoordinatorCredentialException exception)
		{
			MobileDiagnostics.Log("send", exception);
			await HandleCredentialRejectedAsync();
		}
		catch (Exception exception)
		{
			MobileDiagnostics.Log("send", exception);
			await DisplayAlert("发送失败", DescribeSendFailure(exception), "确定");
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

				var progress = new Progress<TransferProgress>(value => ReportProgress("正在接收", value));
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
		catch (CoordinatorCredentialException exception)
		{
			MobileDiagnostics.Log("receive", exception);
			await HandleCredentialRejectedAsync();
		}
		catch (Exception exception)
		{
			MobileDiagnostics.Log("receive", exception);
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

		ShowConnectedStatus();
		ComputerNameLabel.Text = new Uri(connection.Endpoint).Host;

		// The certificate fingerprint is the only thing that proves which computer is on the
		// other end, so keep a short form of it visible next to the address.
		var fingerprint = connection.CertificateSha256.Length > 12
			? connection.CertificateSha256[..12]
			: connection.CertificateSha256;
		ComputerDetailLabel.Text = $"{connection.Endpoint} · 证书 {fingerprint}…";
		PairButton.Text = "重新配对";
		UnbindButton.IsVisible = true;
		UpdateActions();
	}

	// ---------------------------------------------------------------- 连接状态外观

	private void ShowConnectedStatus()
	{
		ConnectionStatusLabel.Text = "已连接";
		ConnectionStatusLabel.TextColor = LookupColor("LtSuccessText", Color.FromArgb("#0F6B40"));
		ConnectionStatusPill.BackgroundColor = LookupColor("LtSuccessSurface", Color.FromArgb("#E4F6EC"));
	}

	private void ShowDisconnectedStatus()
	{
		ConnectionStatusLabel.Text = "未连接";
		ConnectionStatusLabel.TextColor = LookupColor("LtWarningText", Color.FromArgb("#8A610E"));
		ConnectionStatusPill.BackgroundColor = LookupColor("LtWarningSurface", Color.FromArgb("#FFF5DC"));
	}

	private void ShowPairingStatus()
	{
		ConnectionStatusLabel.Text = "等待电脑批准…";
		ConnectionStatusLabel.TextColor = LookupColor("LtTextSecondary", Color.FromArgb("#69758C"));
		ConnectionStatusPill.BackgroundColor = LookupColor("LtNeutralSurface", Color.FromArgb("#EEF1F7"));
	}

	private static Color LookupColor(string key, Color fallback) =>
		Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Color color
			? color
			: fallback;

	/// <summary>
	/// One place that turns a progress report into both the bar and the caption, so the two can
	/// never disagree about how far along a transfer is.
	/// </summary>
	private void ReportProgress(string verb, TransferProgress value)
	{
		var fraction = value.TotalBytes <= 0
			? 1d
			: Math.Clamp((double)value.BytesTransferred / value.TotalBytes, 0, 1);
		TransferProgressBar.Progress = fraction;
		TransferStatusLabel.Text = $"{verb} {value.FileName} · {(int)(fraction * 100)}%";
	}

	/// <summary>
	/// The desktop answered 401: this phone's credential is no longer in its trusted-device
	/// store, either because it was removed there or because a newer pairing replaced it.
	/// Keeping that dead credential would make every later transfer fail with an opaque 401, so
	/// drop it, return the screen to "not connected" and tell the user to pair again.
	/// </summary>
	private async Task HandleCredentialRejectedAsync()
	{
		MobileConnectionStore.Clear();
		_client?.Dispose();
		_client = null;
		_connection = null;
		// Stop OnAppearing from reloading a record that no longer exists.
		_connectionLoadStarted = true;
		ShowDisconnectedStatus();
		ComputerNameLabel.Text = "未配对";
		ComputerDetailLabel.Text = "请点右侧「配对」重新连接电脑";
		PairButton.Text = "配对";
		UnbindButton.IsVisible = false;
		UpdateActions();
		await DisplayAlert(
			"配对凭据已失效",
			"电脑已不再信任这台手机：可能是在电脑的「设备」页把本机移除了，或者这台手机在这台电脑上重新配对过。\n\n" +
			"请点「配对」，重新扫描电脑上的二维码；配对完成后就能继续互传文件。",
			"确定");
	}

	/// <summary>
	/// Unpairing used to be possible only from the desktop. Letting the phone drop its own copy
	/// of the credential is what makes "pair again from scratch" a repair the user can perform
	/// on their own when the two ends disagree.
	/// </summary>
	private async void OnUnbindClicked(object sender, EventArgs e)
	{
		var confirmed = await DisplayAlert(
			"解除配对",
			"这台手机会删除保存在系统安全存储里的配对凭据。之后需要重新扫描电脑二维码才能互传文件。\n\n" +
			"电脑上的「设备」列表里仍会保留这台设备的记录，可以在电脑上顺手移除。",
			"解除",
			"取消");
		if (!confirmed)
		{
			return;
		}

		MobileConnectionStore.Clear();
		_client?.Dispose();
		_client = null;
		_connection = null;
		ShowDisconnectedStatus();
		ComputerNameLabel.Text = "未配对";
		ComputerDetailLabel.Text = "先在电脑上点「创建配对信息」，再用手机扫描二维码";
		PairButton.Text = "配对";
		UnbindButton.IsVisible = false;
		UpdateActions();
	}

	private void SetBusy(bool isBusy, string status)
	{
		TransferStatusArea.IsVisible = isBusy;
		TransferProgressBar.Progress = 0;
		TransferStatusLabel.Text = status;
		PairButton.IsEnabled = !isBusy;
		UnbindButton.IsEnabled = !isBusy;

		if (isBusy)
		{
			SendButton.IsEnabled = false;
			ReceiveButton.IsEnabled = false;
			return;
		}

		// Going idle after a send has to refresh the caption too: the queue was just cleared,
		// so "发送 3 个文件到电脑" would otherwise stay on screen with nothing selected.
		UpdateActions();
	}

	private void UpdateActions()
	{
		SendButton.IsEnabled = _client is not null && _selectedFiles.Count > 0;
		SendButton.Text = _client is null
			? "连接电脑后发送"
			: _selectedFiles.Count == 0
				? "发送到电脑"
				: $"发送 {_selectedFiles.Count} 个文件到电脑";
		ReceiveButton.IsEnabled = _client is not null;
	}

	/// <summary>
	/// Pairing is the most common dead end on a phone, and the underlying exception messages are
	/// English and technical. Translate the ones the user can actually act on.
	/// </summary>
	private static string DescribePairingFailure(Exception exception) => exception switch
	{
		TimeoutException =>
			"电脑一直没有确认这次配对。配对信息只有 2 分钟有效期，超时后手机就停止等待了。\n\n" +
			"请在电脑上点「创建配对信息」，再用手机重新扫码，并尽快在电脑弹出的确认框里点「是」。",
		UnauthorizedAccessException =>
			"电脑拒绝了这次配对，或者二维码已经失效。请让电脑重新生成配对信息后再扫一次。",
		InvalidDataException =>
			"没有读到有效的配对信息。请确认扫的是电脑上「创建配对信息」显示的二维码，" +
			"也可以改用「粘贴配对信息」。",
		_ => exception.Message,
	};

	/// <summary>
	/// Sending has two dead ends that both come back as exceptions the user cannot read:
	/// the desktop never answered its approval prompt, or it answered "no".
	/// </summary>
	private static string DescribeSendFailure(Exception exception) => exception switch
	{
		TransferRejectedException =>
			"电脑没有接收这次发送：可能是电脑上的提示被点了「否」，也可能是提示一直没人点、自动失效了。\n\n" +
			"请让电脑上「局域传输」保持运行，然后重新发送；电脑弹出提示后点「是」即可。",
		TimeoutException =>
			"电脑一直没有确认这次发送，手机已经停止等待了。\n\n" +
			"请确认电脑上「局域传输」正在运行；弹出确认提示后尽快点「是」，然后重新发送。",
		_ => exception.Message,
	};

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

