using LocalTransfer.Contracts.Devices;
using LocalTransfer.Contracts.Protocol;
using LocalTransfer.Coordinator.Devices;

namespace LocalTransfer.IntegrationTests;

public sealed class TrustedDeviceStoreTests
{
    [Fact]
    public async Task SuccessfulValidation_RecordsLastSeenAndPersistsIt()
    {
        using var directory = new TempDirectory();
        var store = new TrustedDeviceStore(directory.Path);
        var descriptor = new DeviceDescriptor(
            Guid.NewGuid(),
            "LastSeen 测试设备",
            "Android",
            ProtocolConstants.CurrentVersion,
            DeviceCapabilities.Upload | DeviceCapabilities.Download);
        await store.AddOrUpdateAsync(descriptor, "credential-value");

        // 修复前的行为：GetAll 返回的 LastSeenUtc 恒为 null，设备页永远显示「尚未连接过」。
        Assert.Null(store.GetAll().Single(device => device.DeviceId == descriptor.DeviceId).LastSeenUtc);

        Assert.True(store.ValidateCredential(descriptor.DeviceId, "credential-value"));

        // 落盘是节流后的后台任务，轮询等待它完成。
        var deadline = DateTime.UtcNow.AddSeconds(5);
        DateTimeOffset? lastSeen = null;
        while (DateTime.UtcNow < deadline)
        {
            // 直接轮询磁盘：内存中的更新是同步的，而写盘是节流后的后台任务。
            // store 在构造时读盘一次，所以每一轮都要重新实例化才能看到新写入的值。
            var reloaded = new TrustedDeviceStore(directory.Path);
            lastSeen = reloaded.GetAll()
                .Single(device => device.DeviceId == descriptor.DeviceId)
                .LastSeenUtc;
            if (lastSeen is not null)
            {
                break;
            }

            await Task.Delay(100);
        }

        Assert.NotNull(lastSeen);

        // 时间戳必须已经持久化，重启后设备页才能显示最近活动。
        var final = new TrustedDeviceStore(directory.Path);
        Assert.Equal(
            lastSeen,
            final.GetAll().Single(device => device.DeviceId == descriptor.DeviceId).LastSeenUtc);
    }

    [Fact]
    public async Task FailedValidation_DoesNotTouchLastSeen()
    {
        using var directory = new TempDirectory();
        var store = new TrustedDeviceStore(directory.Path);
        var descriptor = new DeviceDescriptor(
            Guid.NewGuid(),
            "错误凭据测试设备",
            "Android",
            ProtocolConstants.CurrentVersion,
            DeviceCapabilities.Upload);
        await store.AddOrUpdateAsync(descriptor, "credential-value");

        Assert.False(store.ValidateCredential(descriptor.DeviceId, "wrong-credential"));
        await Task.Delay(500);

        Assert.Null(store.GetAll().Single(device => device.DeviceId == descriptor.DeviceId).LastSeenUtc);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"LocalTransfer.StoreTests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                TestCleanup.DeleteDirectory(Path);
            }
        }
    }
}
