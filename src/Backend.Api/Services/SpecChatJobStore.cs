using System.Collections.Concurrent;

namespace Backend.Api.Services;

public enum SpecChatJobStatus { Pending, Done, Error }

public sealed record SpecChatJob(SpecChatJobStatus Status, string? Reply, bool Finalized, DateTime CreatedAt);

// Backing store for the async spec-chat flow (SpecProjectEndpoints.Chat/GetChatJob): the outbound call
// to the Hermes `specs` api_server can take well over 30s for a rich answer, which used to be the
// browser-facing request's own HttpClient timeout - now that call runs in a background Task started by
// the POST handler, and the frontend polls GET .../chat/{requestId} for the result instead of blocking
// on one long-lived HTTP request. In-memory/Singleton is enough for this single-operator/single-process
// pilot (same tier as SessionService/LoginAttemptService) - a job only needs to survive a few seconds of
// polling, not a process restart.
public sealed class SpecChatJobStore
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<string, SpecChatJob> _jobs = new();

    public string Create()
    {
        Sweep();
        var id = Guid.NewGuid().ToString("N");
        _jobs[id] = new SpecChatJob(SpecChatJobStatus.Pending, null, false, DateTime.UtcNow);
        return id;
    }

    public void Complete(string id, string reply, bool finalized) => _jobs[id] = new SpecChatJob(SpecChatJobStatus.Done, reply, finalized, DateTime.UtcNow);

    public void Fail(string id) => _jobs[id] = new SpecChatJob(SpecChatJobStatus.Error, null, false, DateTime.UtcNow);

    public SpecChatJob? Get(string id) => _jobs.TryGetValue(id, out var job) ? job : null;

    private void Sweep()
    {
        var cutoff = DateTime.UtcNow - Ttl;
        foreach (var (key, job) in _jobs)
            if (job.CreatedAt < cutoff) _jobs.TryRemove(key, out _);
    }
}
