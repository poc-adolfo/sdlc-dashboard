using System.Collections.Concurrent;

namespace Backend.Api.Services;

public enum AsyncJobStatus { Pending, Done, Error }

public sealed record AsyncJob<TResult>(AsyncJobStatus Status, TResult? Result, DateTime CreatedAt);

// Generic version of SpecChatJobStore's Pending/Done/Error + TTL-sweep shape (seção 5 do
// gate-ux-figma.md reaproveita o mesmo padrão job+polling em vez de inventar um novo por feature) -
// DesignSystemEndpoints e UxGateEndpoints cada um registra seu próprio AsyncJobStore<TResult> fechado
// (tipos de resultado diferentes), mas compartilham esta implementação em vez de 3 cópias quase
// idênticas. In-memory/Singleton, mesmo tier de SpecChatJobStore - um job só precisa sobreviver a
// alguns segundos de polling, não a um restart de processo.
public sealed class AsyncJobStore<TResult> where TResult : class
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<string, AsyncJob<TResult>> _jobs = new();

    public string Create()
    {
        Sweep();
        var id = Guid.NewGuid().ToString("N");
        _jobs[id] = new AsyncJob<TResult>(AsyncJobStatus.Pending, null, DateTime.UtcNow);
        return id;
    }

    public void Complete(string id, TResult result) => _jobs[id] = new AsyncJob<TResult>(AsyncJobStatus.Done, result, DateTime.UtcNow);

    public void Fail(string id) => _jobs[id] = new AsyncJob<TResult>(AsyncJobStatus.Error, null, DateTime.UtcNow);

    public AsyncJob<TResult>? Get(string id) => _jobs.TryGetValue(id, out var job) ? job : null;

    private void Sweep()
    {
        var cutoff = DateTime.UtcNow - Ttl;
        foreach (var (key, job) in _jobs)
            if (job.CreatedAt < cutoff) _jobs.TryRemove(key, out _);
    }
}
