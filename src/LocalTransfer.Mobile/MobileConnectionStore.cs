using System.Text.Json;
using LocalTransfer.Client;

namespace LocalTransfer.Mobile;

internal static class MobileConnectionStore
{
    private const string ConnectionKey = "LocalTransfer.CoordinatorConnection";
    private const string DeviceIdKey = "LocalTransfer.DeviceId";

    public static async Task<CoordinatorConnection?> LoadAsync()
    {
        var json = await SecureStorage.Default.GetAsync(ConnectionKey);
        return string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<CoordinatorConnection>(json);
    }

    public static Task SaveAsync(CoordinatorConnection connection) =>
        SecureStorage.Default.SetAsync(ConnectionKey, JsonSerializer.Serialize(connection));

    public static Guid GetOrCreateDeviceId()
    {
        var value = Preferences.Default.Get(DeviceIdKey, string.Empty);
        if (Guid.TryParse(value, out var deviceId))
        {
            return deviceId;
        }

        deviceId = Guid.NewGuid();
        Preferences.Default.Set(DeviceIdKey, deviceId.ToString());
        return deviceId;
    }
}
