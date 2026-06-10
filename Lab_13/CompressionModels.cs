using System.Collections.Concurrent;

namespace Lab_13;

public enum CompressionAlgorithmType
{
    GZip,
    Deflate,
    Brotli,
}

public enum DistributionMode
{
    SingleWorker,
    SplitAcrossWorkers,
    FullCopyToAllWorkers,
}

public sealed record WorkerDescriptor(
    string Id,
    string Host,
    int Port,
    bool IsHealthy,
    DateTimeOffset LastHeartbeatUtc,
    int ActiveTasks,
    long CompletedTasks,
    long FailedTasks
);

public sealed record TcpRequestEnvelope(
    string Command,
    string WorkerId,
    string JobId,
    int PartIndex,
    int TotalParts,
    string Algorithm,
    string FileName,
    long PayloadLength
);

public sealed record TcpResponseEnvelope(
    string Command,
    bool Success,
    string WorkerId,
    string JobId,
    int PartIndex,
    long PayloadLength,
    long OriginalBytes,
    long CompressedBytes,
    long WorkerElapsedMilliseconds,
    string? ErrorMessage
);

public sealed record CompressionWorkItem(
    string JobId,
    string FileName,
    CompressionAlgorithmType Algorithm,
    DistributionMode Mode,
    WorkerDescriptor Worker,
    int PartIndex,
    int TotalParts,
    byte[] Payload
);

public sealed record WorkerCompressionResult(
    bool Success,
    string WorkerId,
    int PartIndex,
    int TotalParts,
    byte[] CompressedPayload,
    long OriginalBytes,
    long CompressedBytes,
    long WorkerElapsedMilliseconds,
    long NetworkElapsedMilliseconds,
    string? ErrorMessage
);

public sealed class WorkerProgress
{
    public string WorkerId { get; init; } = string.Empty;
    public int PartIndex { get; init; }
    public int TotalParts { get; init; }
    public string Status { get; set; } = "Ожидает";
    public int ProgressPercent { get; set; }
    public long OriginalBytes { get; set; }
    public long CompressedBytes { get; set; }
    public long WorkerElapsedMilliseconds { get; set; }
    public long NetworkElapsedMilliseconds { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class CompressionJob
{
    private long completedParts;

    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
    public string FileName { get; init; } = string.Empty;
    public CompressionAlgorithmType Algorithm { get; init; }
    public DistributionMode Mode { get; init; }
    public long OriginalBytes { get; init; }
    public long CompressedBytes { get; set; }
    public long SequentialElapsedMilliseconds { get; set; }
    public long FrontExchangeMilliseconds { get; set; }
    public long WorkerExchangeMilliseconds { get; set; }
    public long TotalElapsedMilliseconds { get; set; }
    public int TotalParts { get; set; }
    public string Status { get; set; } = "Создана";
    public string? OutputPath { get; set; }
    public string? DownloadUrl { get; set; }
    public ConcurrentDictionary<string, WorkerProgress> WorkerProgress { get; } = new();
    public ConcurrentQueue<string> Logs { get; } = new();
    public bool IsFinished => Status is "Завершена" or "Ошибка" or "Отменена";

    public double CompressionRatio =>
        OriginalBytes == 0 ? 0 : 1 - ((double)CompressedBytes / OriginalBytes);

    public long MarkPartCompleted()
    {
        // Interlocked гарантирует корректный счетчик завершенных частей при параллельной записи.
        return Interlocked.Increment(ref completedParts);
    }

    public void AddLog(string message)
    {
        var entry = $"{DateTime.Now:HH:mm:ss.fff} | {message}";
        Logs.Enqueue(entry);

        // Ограничиваем журнал, чтобы UI не держал бесконечный список сообщений в памяти.
        while (Logs.Count > 120 && Logs.TryDequeue(out _)) { }
    }
}

public sealed class CompressionJobStore
{
    private readonly ConcurrentDictionary<string, CompressionJob> jobs = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> cancellations = new();
    private readonly BlockingCollection<string> operationLog = new(new ConcurrentQueue<string>());

    public CompressionJob Add(CompressionJob job, CancellationTokenSource cancellation)
    {
        jobs[job.Id] = job;
        cancellations[job.Id] = cancellation;
        AddLog($"Задача {job.Id} добавлена в хранилище.");
        return job;
    }

    public bool TryGet(string id, out CompressionJob? job) => jobs.TryGetValue(id, out job);

    public IReadOnlyList<CompressionJob> GetRecentJobs()
    {
        return jobs.Values.OrderByDescending(job => job.Id).Take(10).ToArray();
    }

    public void Cancel(string jobId)
    {
        if (cancellations.TryGetValue(jobId, out var cancellation))
        {
            // CancellationTokenSource дает управляемую отмену без ручного управления потоками.
            cancellation.Cancel();
            AddLog($"Запрошена отмена задачи {jobId}.");
        }
    }

    public void Complete(string jobId)
    {
        if (cancellations.TryRemove(jobId, out var cancellation))
        {
            cancellation.Dispose();
        }
    }

    public void AddLog(string message)
    {
        operationLog.Add($"{DateTime.Now:HH:mm:ss.fff} | {message}");
    }

    public IReadOnlyList<string> GetGlobalLog()
    {
        return operationLog.ToArray().TakeLast(200).ToArray();
    }
}
