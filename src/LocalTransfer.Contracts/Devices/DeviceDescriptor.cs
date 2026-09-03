namespace LocalTransfer.Contracts.Devices;

public sealed record DeviceDescriptor(
    Guid DeviceId,
    string DisplayName,
    string Platform,
    int ProtocolVersion,
    DeviceCapabilities Capabilities);
