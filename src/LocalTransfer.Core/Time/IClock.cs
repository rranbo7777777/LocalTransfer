namespace LocalTransfer.Core.Time;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
