using System.Collections.Concurrent;
using System.Security.Cryptography;
using LocalTransfer.Core.Time;

namespace LocalTransfer.Core.Security;

public sealed record PairingTicket(string Secret, DateTimeOffset ExpiresAtUtc);

public sealed class PairingTicketService
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _tickets = new(StringComparer.Ordinal);
    private readonly IClock _clock;

    public PairingTicketService(IClock? clock = null)
    {
        _clock = clock ?? SystemClock.Instance;
    }

    public PairingTicket Create(TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }

        RemoveExpiredTickets();

        while (true)
        {
            var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            var expiresAtUtc = _clock.UtcNow.Add(lifetime);

            if (_tickets.TryAdd(Hash(secret), expiresAtUtc))
            {
                return new PairingTicket(secret, expiresAtUtc);
            }
        }
    }

    public bool TryConsume(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return false;
        }

        return _tickets.TryRemove(Hash(secret), out var expiresAtUtc) && expiresAtUtc > _clock.UtcNow;
    }

    private void RemoveExpiredTickets()
    {
        var now = _clock.UtcNow;
        foreach (var ticket in _tickets)
        {
            if (ticket.Value <= now)
            {
                _tickets.TryRemove(ticket.Key, out _);
            }
        }
    }

    private static string Hash(string secret) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret)));
}
