using System.Net;

namespace MapleLedger.Core.Lan;

/// <summary>
/// Slows down passcode guessing: a few wrong tries per device address, and a cap across all
/// addresses (an address on a home network is easy to change). A right passcode clears that
/// address's count, not the overall one.
/// </summary>
public sealed class PasscodeThrottle(TimeProvider clock)
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    public const int PerAddress = 5;
    public const int Overall = 30;

    private readonly Dictionary<string, Queue<DateTimeOffset>> _byAddress = [];
    private readonly Queue<DateTimeOffset> _all = new();
    private readonly Lock _lock = new();

    public bool IsLockedOut(IPAddress? address)
    {
        lock (_lock)
        {
            var now = clock.GetUtcNow();
            Trim(_all, now);
            if (_all.Count >= Overall) return true;
            return _byAddress.TryGetValue(Key(address), out var queue) && Trim(queue, now) >= PerAddress;
        }
    }

    public void RecordFailure(IPAddress? address)
    {
        lock (_lock)
        {
            var now = clock.GetUtcNow();
            if (!_byAddress.TryGetValue(Key(address), out var queue))
            {
                // Forget addresses whose tries have all expired, so the table can't grow without end.
                foreach (var (key, old) in _byAddress.ToList())
                {
                    if (Trim(old, now) == 0) _byAddress.Remove(key);
                }

                _byAddress[Key(address)] = queue = new Queue<DateTimeOffset>();
            }

            queue.Enqueue(now);
            _all.Enqueue(now);
        }
    }

    public void RecordSuccess(IPAddress? address)
    {
        lock (_lock)
        {
            _byAddress.Remove(Key(address));
        }
    }

    private static int Trim(Queue<DateTimeOffset> queue, DateTimeOffset now)
    {
        while (queue.Count > 0 && now - queue.Peek() >= Window) queue.Dequeue();
        return queue.Count;
    }

    private static string Key(IPAddress? address) =>
        address is null ? "unknown" : (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
}
