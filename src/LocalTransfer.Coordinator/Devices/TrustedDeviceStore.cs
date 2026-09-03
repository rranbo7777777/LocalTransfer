using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalTransfer.Contracts.Devices;

namespace LocalTransfer.Coordinator.Devices;

public sealed record TrustedDeviceInfo(
    Guid DeviceId,
    string DisplayName,
    string Platform,
    DateTimeOffset PairedAtUtc,
    DateTimeOffset? LastSeenUtc,
    bool AutoAccept);

internal sealed record StoredTrustedDevice(
    Guid DeviceId,
    string DisplayName,
    string Platform,
    string CredentialHashHex,
    DateTimeOffset PairedAtUtc,
    DateTimeOffset? LastSeenUtc,
    bool AutoAccept);

public sealed class TrustedDeviceStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<Guid, StoredTrustedDevice> _devices;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly string _path;

    public TrustedDeviceStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "trusted-devices.json");
        _devices = Load(_path).ToDictionary(device => device.DeviceId);
    }

    public IReadOnlyList<TrustedDeviceInfo> GetAll()
    {
        lock (_gate)
        {
            return _devices.Values
                .OrderBy(device => device.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Select(ToInfo)
                .ToArray();
        }
    }

    public bool ValidateCredential(Guid deviceId, string credential)
    {
        if (string.IsNullOrWhiteSpace(credential))
        {
            return false;
        }

        StoredTrustedDevice? device;
        lock (_gate)
        {
            _devices.TryGetValue(deviceId, out device);
        }

        if (device is null)
        {
            return false;
        }

        var actualHash = SHA256.HashData(Encoding.UTF8.GetBytes(credential));
        return CryptographicOperations.FixedTimeEquals(
            actualHash,
            Convert.FromHexString(device.CredentialHashHex));
    }

    public async Task AddOrUpdateAsync(
        DeviceDescriptor descriptor,
        string credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(credential);

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var replacement = new StoredTrustedDevice(
                descriptor.DeviceId,
                descriptor.DisplayName,
                descriptor.Platform,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential))),
                DateTimeOffset.UtcNow,
                null,
                false);
            StoredTrustedDevice[] snapshot;
            lock (_gate)
            {
                snapshot = _devices.Values
                    .Where(device => device.DeviceId != descriptor.DeviceId)
                    .Append(replacement)
                    .ToArray();
            }

            await SaveAsync(snapshot, cancellationToken);
            lock (_gate)
            {
                _devices[descriptor.DeviceId] = replacement;
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<bool> RemoveAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            StoredTrustedDevice[] snapshot;
            lock (_gate)
            {
                if (!_devices.ContainsKey(deviceId))
                {
                    return false;
                }

                snapshot = _devices.Values.Where(device => device.DeviceId != deviceId).ToArray();
            }

            await SaveAsync(snapshot, cancellationToken);
            lock (_gate)
            {
                _devices.Remove(deviceId);
            }

            return true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static IReadOnlyList<StoredTrustedDevice> Load(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<StoredTrustedDevice[]>(json, SerializerOptions)
            ?? throw new InvalidDataException("The trusted device store is invalid.");
    }

    private async Task SaveAsync(
        IReadOnlyList<StoredTrustedDevice> devices,
        CancellationToken cancellationToken)
    {
        var temporaryPath = $"{_path}.new";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, devices, SerializerOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static TrustedDeviceInfo ToInfo(StoredTrustedDevice device) =>
        new(
            device.DeviceId,
            device.DisplayName,
            device.Platform,
            device.PairedAtUtc,
            device.LastSeenUtc,
            device.AutoAccept);
}
