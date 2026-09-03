namespace LocalTransfer.Contracts.Devices;

[Flags]
public enum DeviceCapabilities
{
    None = 0,
    Upload = 1 << 0,
    Download = 1 << 1,
    Resume = 1 << 2,
    Sha256 = 1 << 3,
    BackgroundTransfer = 1 << 4
}
