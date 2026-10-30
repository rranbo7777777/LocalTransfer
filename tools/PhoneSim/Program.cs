// PhoneSim —— 在电脑上模拟手机端行为的联调工具。
// MAUI 手机 App 调用的就是 LocalTransfer.Client 这套库；本工具用同一代码路径，
// 用于在无真机/模拟器的环境下对电脑端做端到端验证（配对、批量上传、主动下载）。
//
// 用法：
//   PhoneSim pair                 读取剪贴板里的配对信息（电脑端「复制配对信息」的产物），发起配对
//   PhoneSim upload <目录>        把目录里所有文件作为一批上传（触发电脑端整批批准弹窗）
//   PhoneSim download <目录>      拉取电脑发送队列里的文件并保存（手机「接收电脑文件」的行为）
//
// 配对结果保存在 %TEMP%\lt-check\connection.json，upload/download 复用，无需重复配对。
// 环境变量 LT_ENDPOINT_OVERRIDE 可把连接地址改写为指定值（证书指纹固定不受影响）。
using System.Security.Cryptography;
using System.IO;
using System.Text.Json;
using System.Windows;
using LocalTransfer.Client;
using LocalTransfer.Contracts.Devices;
using LocalTransfer.Contracts.Pairing;
using LocalTransfer.Contracts.Protocol;

internal static class Program
{
    private static readonly string StateFile = Path.Combine(
        Path.GetTempPath(), "lt-check", "connection.json");

    [STAThread]
    private static int Main(string[] args)
    {
        // async Task Main 会丢失 [STAThread]（编译器生成的同步包装不在 STA 线程上），
        // 而剪贴板读取必须在 STA 线程，所以入口保持同步。
        try
        {
            return MainAsync(args).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAILED " + exception.GetType().Name + ": " + exception.Message);
            Console.Error.WriteLine(exception.ToString());
            return 1;
        }
    }

    private static async Task<int> MainAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("用法: PhoneSim pair|upload <目录>|download <目录>");
            return 2;
        }

        try
        {
            return args[0] switch
            {
                "pair" => await PairAsync(),
                "upload" => await UploadAsync(args[1]),
                "download" => await DownloadAllAsync(args[1]),
                _ => 2,
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAILED " + exception.GetType().Name + ": " + exception.Message);
            Console.Error.WriteLine(exception.ToString());
            return 1;
        }
    }

    private static string? EndpointOverride =>
        Environment.GetEnvironmentVariable("LT_ENDPOINT_OVERRIDE") is { Length: > 0 } value
            ? value
            : null;

    private static async Task<int> PairAsync()
    {
        var json = Clipboard.GetText();
        if (string.IsNullOrWhiteSpace(json))
        {
            Console.Error.WriteLine("剪贴板没有配对信息");
            return 3;
        }

        var bootstrap = JsonSerializer.Deserialize<PairingBootstrap>(
            json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (bootstrap is null)
        {
            Console.Error.WriteLine("配对信息解析失败");
            return 3;
        }

        if (EndpointOverride is { } endpoint)
        {
            bootstrap = bootstrap with { Endpoint = endpoint };
        }

        Console.WriteLine("PAIRING -> " + bootstrap.Endpoint);
        var device = new DeviceDescriptor(
            Guid.NewGuid(),
            "自动化检查-安卓模拟",
            "Android",
            ProtocolConstants.CurrentVersion,
            DeviceCapabilities.Upload | DeviceCapabilities.Download | DeviceCapabilities.Resume);

        var connection = await PairingClient.PairAsync(bootstrap, device);

        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        await File.WriteAllTextAsync(StateFile, JsonSerializer.Serialize(new ConnectionRecord(
            connection.Endpoint,
            connection.CertificateSha256,
            connection.DeviceId,
            connection.Credential)));
        Console.WriteLine("PAIRED " + connection.Endpoint + " device=" + connection.DeviceId);
        return 0;
    }

    private static async Task<int> UploadAsync(string directory)
    {
        using var client = CreateClient();
        var files = Directory.GetFiles(directory);
        if (files.Length == 0)
        {
            Console.Error.WriteLine("没有可上传的文件: " + directory);
            return 2;
        }

        var items = new List<BatchUploadItem>();
        foreach (var file in files)
        {
            var bytes = await File.ReadAllBytesAsync(file);
            var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            Console.WriteLine("STAGE " + Path.GetFileName(file) + " " + bytes.LongLength + "B " + sha256);
            items.Add(new BatchUploadItem(
                Path.GetFileName(file),
                _ => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false)),
                Sha256Hex: sha256,
                Length: bytes.LongLength));
        }

        var progress = new Progress<TransferProgress>(value =>
            Console.WriteLine(
                $"PROGRESS {value.FileName} {value.BytesTransferred}/{value.TotalBytes}"));
        var result = await client.UploadBatchAsync(items, progress);

        Console.WriteLine("UPLOAD " + JsonSerializer.Serialize(new
        {
            completed = result.CompletedIndexes,
            rejected = result.RejectedIndexes,
            failed = result.Failed.Select(failure => new
            {
                failure.Index,
                failure.FileName,
                failure.Reason,
            }),
        }));

        return result.CompletedIndexes.Count == items.Count ? 0 : 4;
    }

    private static async Task<int> DownloadAllAsync(string destination)
    {
        using var client = CreateClient();
        Directory.CreateDirectory(destination);

        var available = await client.GetAvailableDownloadsAsync();
        Console.WriteLine("AVAILABLE " + available.Count);
        if (available.Count == 0)
        {
            return 5;
        }

        foreach (var transfer in available)
        {
            var progress = new Progress<TransferProgress>(value =>
                Console.WriteLine(
                    $"PROGRESS {value.FileName} {value.BytesTransferred}/{value.TotalBytes}"));
            var finalPath = await client.DownloadAsync(transfer, destination, progress);
            Console.WriteLine("DOWNLOADED " + finalPath);
        }

        var remaining = await client.GetAvailableDownloadsAsync();
        Console.WriteLine("REMAINING " + remaining.Count);
        return remaining.Count == 0 ? 0 : 4;
    }

    private static LocalTransferClient CreateClient()
    {
        if (!File.Exists(StateFile))
        {
            throw new InvalidOperationException("尚未配对：先运行 PhoneSim pair");
        }

        var record = JsonSerializer.Deserialize<ConnectionRecord>(
            File.ReadAllText(StateFile), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (record is null)
        {
            throw new InvalidOperationException("配对状态文件损坏，请重新配对");
        }

        var endpoint = EndpointOverride ?? record.Endpoint;
        var connection = new CoordinatorConnection(
            endpoint, record.CertificateSha256, record.DeviceId, record.Credential);
        return new LocalTransferClient(
            connection, message => Console.Error.WriteLine("LOG " + message));
    }

    private sealed record ConnectionRecord(
        string Endpoint, string CertificateSha256, Guid DeviceId, string Credential);
}
