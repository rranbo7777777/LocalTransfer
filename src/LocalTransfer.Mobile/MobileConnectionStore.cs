using System.Text.Json;
using LocalTransfer.Client;

namespace LocalTransfer.Mobile;

internal static class MobileConnectionStore
{
    private const string ConnectionKey = "LocalTransfer.CoordinatorConnection";
    private const string DeviceIdKey = "LocalTransfer.DeviceId";

    /// <summary>
    /// Loads the persisted pairing. Returns <see langword="null"/> (and drops the entry) when it
    /// is missing or incomplete: a truncated record would otherwise deserialize with a null
    /// credential and turn every request into a confusing 401 instead of a "pair again" prompt.
    /// </summary>
    public static async Task<CoordinatorConnection?> LoadAsync()
    {
        var json = await SecureStorage.Default.GetAsync(ConnectionKey);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        CoordinatorConnection? connection;
        try
        {
            connection = JsonSerializer.Deserialize<CoordinatorConnection>(json);
        }
        catch (JsonException)
        {
            Clear();
            return null;
        }

        if (connection is null || !IsComplete(connection))
        {
            Clear();
            return null;
        }

        return connection;
    }

    public static Task SaveAsync(CoordinatorConnection connection) =>
        SecureStorage.Default.SetAsync(ConnectionKey, JsonSerializer.Serialize(connection));

    /// <summary>Drops the stored pairing so the next launch starts from "not connected".</summary>
    public static void Clear() => SecureStorage.Default.Remove(ConnectionKey);

    /// <summary>
    /// The identifier this phone pairs with. Pairing always reuses the identifier of an existing
    /// connection when one is present, because the desktop keys trusted devices by this value:
    /// generating a fresh identifier would register a second, duplicate entry for the same phone.
    /// </summary>
    public static Guid GetOrCreateDeviceId(Guid? preferred = null)
    {
        if (preferred is { } candidate && candidate != Guid.Empty)
        {
            Preferences.Default.Set(DeviceIdKey, candidate.ToString());
            return candidate;
        }

        var value = Preferences.Default.Get(DeviceIdKey, string.Empty);
        if (Guid.TryParse(value, out var deviceId) && deviceId != Guid.Empty)
        {
            return deviceId;
        }

        deviceId = Guid.NewGuid();
        Preferences.Default.Set(DeviceIdKey, deviceId.ToString());
        return deviceId;
    }

    private static bool IsComplete(CoordinatorConnection connection) =>
        !string.IsNullOrWhiteSpace(connection.Endpoint) &&
        !string.IsNullOrWhiteSpace(connection.CertificateSha256) &&
        !string.IsNullOrWhiteSpace(connection.Credential) &&
        connection.DeviceId != Guid.Empty;
}
