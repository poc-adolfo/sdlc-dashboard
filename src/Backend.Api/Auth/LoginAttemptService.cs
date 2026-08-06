using Microsoft.Extensions.Options;

namespace Backend.Api.Auth;

public sealed class LoginAttemptService(IOptions<SessionOptions> options)
{
    private readonly SessionOptions _options = options.Value;
    private readonly object _gate = new();
    private readonly Dictionary<string, AttemptState> _attempts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AttemptState> _accountAttempts = new(StringComparer.Ordinal);

    public bool IsBlocked(string identity, DateTimeOffset now, bool account = false)
    {
        lock (_gate)
        {
            RemoveExpired(now);
            var store = account ? _accountAttempts : _attempts;
            if (!store.TryGetValue(identity, out var state)) return false;
            if (state.BlockedUntil.HasValue)
            {
                if (state.BlockedUntil.Value > now) return true;
                store.Remove(identity);
                return false;
            }

            return state.Failures >= (account ? _options.AccountLoginMaxFailures : _options.LoginMaxFailures);
        }
    }

    public bool IsAccountBlocked(string username, DateTimeOffset now) => IsBlocked("account:" + username, now, account: true);

    public void RecordAccountFailure(string username, DateTimeOffset now) => RecordFailure("account:" + username, now, account: true);

    public void RecordFailure(string identity, DateTimeOffset now, bool account = false)
    {
        lock (_gate)
        {
            RemoveExpired(now);
            var store = account ? _accountAttempts : _attempts;
            var maxFailures = account ? _options.AccountLoginMaxFailures : _options.LoginMaxFailures;
            var lockout = account ? _options.AccountLoginLockoutDuration : _options.LoginLockoutDuration;
            if (!store.TryGetValue(identity, out var old) || now - old.WindowStarted >= _options.LoginFailureWindow)
                old = new AttemptState(now, 0, null, now);
            var failures = old.Failures + 1;
            store[identity] = new AttemptState(old.WindowStarted, failures, failures >= maxFailures ? now.Add(lockout) : null, now);
            if (!account) TrimIfNeeded();
            else while (_accountAttempts.Count > _options.MaxTrackedLoginIdentities) _accountAttempts.Remove(_accountAttempts.MinBy(x => x.Value.LastTouched).Key);
        }
    }

    public void RecordSuccess(params string[] identities)
    {
        lock (_gate) foreach (var identity in identities) { _attempts.Remove(identity); _accountAttempts.Remove(identity); }
    }

    public int TrackedEntryCount { get { lock (_gate) { RemoveExpired(DateTimeOffset.UtcNow); return _attempts.Count; } } }
    public void Clear() { lock (_gate) { _attempts.Clear(); _accountAttempts.Clear(); } }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var item in _accountAttempts.Where(x => now - x.Value.LastTouched >= _options.LoginAttemptEntryTtl).ToArray()) _accountAttempts.Remove(item.Key);
        foreach (var item in _attempts.Where(x => now - x.Value.LastTouched >= _options.LoginAttemptEntryTtl).ToArray())
            _attempts.Remove(item.Key);
    }

    private void TrimIfNeeded()
    {
        while (_attempts.Count > _options.MaxTrackedLoginIdentities)
        {
            var oldest = _attempts.MinBy(x => x.Value.LastTouched);
            if (oldest.Key is null) break;
            _attempts.Remove(oldest.Key);
        }
    }

    private sealed record AttemptState(DateTimeOffset WindowStarted, int Failures, DateTimeOffset? BlockedUntil, DateTimeOffset LastTouched);
}
