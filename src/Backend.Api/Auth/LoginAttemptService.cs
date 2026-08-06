using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Backend.Api.Auth;

public sealed class LoginAttemptService(IOptions<SessionOptions> options)
{
    private readonly SessionOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, AttemptState> _attempts = new(StringComparer.Ordinal);

    public bool IsBlocked(string identity, DateTimeOffset now)
    {
        if (!_attempts.TryGetValue(identity, out var state)) return false;
        if (state.BlockedUntil > now) return true;
        if (state.BlockedUntil != null || now - state.WindowStarted >= _options.LoginFailureWindow)
            _attempts.TryRemove(identity, out _);
        return false;
    }

    public void RecordFailure(string identity, DateTimeOffset now)
    {
        _attempts.AddOrUpdate(identity, _ => new AttemptState(now, 1, null), (_, old) =>
        {
            var state = now - old.WindowStarted >= _options.LoginFailureWindow ? new AttemptState(now, 0, null) : old;
            var failures = state.Failures + 1;
            return new AttemptState(state.WindowStarted, failures, failures >= _options.LoginMaxFailures
                ? now.Add(_options.LoginLockoutDuration) : null);
        });
    }

    public void RecordSuccess(string identity) => _attempts.TryRemove(identity, out _);

    public void Clear() => _attempts.Clear();

    private sealed record AttemptState(DateTimeOffset WindowStarted, int Failures, DateTimeOffset? BlockedUntil);
}
