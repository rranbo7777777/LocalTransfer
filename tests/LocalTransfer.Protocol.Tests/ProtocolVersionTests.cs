using LocalTransfer.Contracts.Protocol;

namespace LocalTransfer.Protocol.Tests;

public sealed class ProtocolVersionTests
{
    [Fact]
    public void CurrentVersion_IsWithinSupportedRange()
    {
        Assert.True(ProtocolConstants.CurrentVersion >= ProtocolConstants.MinimumSupportedVersion);
    }
}
