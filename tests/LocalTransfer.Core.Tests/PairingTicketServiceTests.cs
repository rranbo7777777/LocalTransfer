using LocalTransfer.Core.Security;
using LocalTransfer.Core.Time;

namespace LocalTransfer.Core.Tests;

public sealed class PairingTicketServiceTests
{
    [Fact]
    public void Ticket_CanOnlyBeConsumedOnce()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
        var service = new PairingTicketService(clock);
        var ticket = service.Create(TimeSpan.FromMinutes(2));

        Assert.True(service.TryConsume(ticket.Secret));
        Assert.False(service.TryConsume(ticket.Secret));
    }

    [Fact]
    public void ExpiredTicket_IsRejected()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
        var service = new PairingTicketService(clock);
        var ticket = service.Create(TimeSpan.FromMinutes(2));
        clock.UtcNow = clock.UtcNow.AddMinutes(3);

        Assert.False(service.TryConsume(ticket.Secret));
    }

    private sealed class FakeClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }
}
