using System.Net;

namespace LocalTransfer.Coordinator;

public sealed class CoordinatorOptions
{
    public IPAddress ListenAddress { get; init; } = IPAddress.Any;

    public IPAddress? AdvertisedAddress { get; init; }

    public int Port { get; init; } = 53317;

    public string DataDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LocalTransfer");

    public string ReceiveDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads",
        "LocalTransfer");
}
