using System.Collections.Concurrent;

namespace Lab_13;

public interface IWorkerSelectionStrategy
{
    IReadOnlyList<WorkerDescriptor> SelectWorkers(
        int count,
        ISet<string>? excludedWorkerIds = null
    );
}

public sealed class WorkerRuntimeState
{
    private int activeTasks;
    private long completedTasks;
    private long failedTasks;

    public WorkerRuntimeState(string id, string host, int port)
    {
        Id = id;
        Host = host;
        Port = port;
        LastHeartbeatUtc = DateTimeOffset.UtcNow;
    }

    public string Id { get; }
    public string Host { get; }
    public int Port { get; }
    public bool IsHealthy { get; private set; } = true;
    public DateTimeOffset LastHeartbeatUtc { get; private set; }
    public int ActiveTasks => activeTasks;
    public long CompletedTasks => completedTasks;
    public long FailedTasks => failedTasks;

    public void MarkHeartbeat(bool healthy)
    {
        IsHealthy = healthy;
        LastHeartbeatUtc = DateTimeOffset.UtcNow;
    }

    public void IncrementActive()
    {
        // Interlocked уменьшает накладные расходы синхронизации по сравнению с lock для простого счетчика.
        Interlocked.Increment(ref activeTasks);
    }

    public void DecrementActive()
    {
        Interlocked.Decrement(ref activeTasks);
    }

    public void MarkCompleted()
    {
        Interlocked.Increment(ref completedTasks);
    }

    public void MarkFailed()
    {
        Interlocked.Increment(ref failedTasks);
    }

    public WorkerDescriptor ToDescriptor()
    {
        return new WorkerDescriptor(
            Id,
            Host,
            Port,
            IsHealthy,
            LastHeartbeatUtc,
            ActiveTasks,
            CompletedTasks,
            FailedTasks
        );
    }
}

public sealed class WorkerRegistry
{
    private readonly ConcurrentDictionary<string, WorkerRuntimeState> workers = new();

    public void AddOrUpdate(string id, string host, int port)
    {
        workers.AddOrUpdate(
            id,
            _ => new WorkerRuntimeState(id, host, port),
            (_, existing) => existing
        );
    }

    public IReadOnlyList<WorkerDescriptor> GetSnapshot()
    {
        return workers
            .Values.Select(worker => worker.ToDescriptor())
            .OrderBy(worker => worker.Id)
            .ToArray();
    }

    public bool TryGet(string workerId, out WorkerRuntimeState? worker)
    {
        return workers.TryGetValue(workerId, out worker);
    }

    public void MarkHeartbeat(string workerId, bool healthy)
    {
        if (workers.TryGetValue(workerId, out var worker))
        {
            worker.MarkHeartbeat(healthy);
        }
    }

    public void IncrementActive(string workerId)
    {
        if (workers.TryGetValue(workerId, out var worker))
        {
            worker.IncrementActive();
        }
    }

    public void DecrementActive(string workerId)
    {
        if (workers.TryGetValue(workerId, out var worker))
        {
            worker.DecrementActive();
        }
    }

    public void MarkCompleted(string workerId)
    {
        if (workers.TryGetValue(workerId, out var worker))
        {
            worker.MarkCompleted();
        }
    }

    public void MarkFailed(string workerId)
    {
        if (workers.TryGetValue(workerId, out var worker))
        {
            worker.MarkFailed();
            worker.MarkHeartbeat(false);
        }
    }
}

public sealed class LeastLoadedWorkerSelectionStrategy : IWorkerSelectionStrategy
{
    private readonly WorkerRegistry registry;

    public LeastLoadedWorkerSelectionStrategy(WorkerRegistry registry)
    {
        this.registry = registry;
    }

    public IReadOnlyList<WorkerDescriptor> SelectWorkers(
        int count,
        ISet<string>? excludedWorkerIds = null
    )
    {
        var excluded = excludedWorkerIds ?? new HashSet<string>();

        // PLINQ параллельно фильтрует и сортирует снимок, что полезно при расширении кластера.
        return registry
            .GetSnapshot()
            .AsParallel()
            .Where(worker => worker.IsHealthy && !excluded.Contains(worker.Id))
            .OrderBy(worker => worker.ActiveTasks)
            .ThenBy(worker => worker.CompletedTasks)
            .Take(count)
            .ToArray();
    }
}
