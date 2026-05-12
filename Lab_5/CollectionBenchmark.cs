using System.Collections.Concurrent;
using System.Diagnostics;

namespace Lab5.ConcurrentCollections;

public sealed class CollectionBenchmark
{
    public (
        long elapsedMs,
        int successfulOperations,
        int failedOperations
    ) BenchmarkConcurrentDictionary(int operationCount)
    {
        ConcurrentDictionary<int, int> dictionary = new();
        int success = 0;
        int failed = 0;

        Stopwatch sw = Stopwatch.StartNew();

        Parallel.For(
            0,
            operationCount,
            i =>
            {
                bool addOk = dictionary.TryAdd(i, i);
                if (addOk)
                    Interlocked.Increment(ref success);
                else
                    Interlocked.Increment(ref failed);

                bool readOk = dictionary.TryGetValue(i, out _);
                if (readOk)
                    Interlocked.Increment(ref success);
                else
                    Interlocked.Increment(ref failed);

                bool removeOk = dictionary.TryRemove(i, out _);
                if (removeOk)
                    Interlocked.Increment(ref success);
                else
                    Interlocked.Increment(ref failed);
            }
        );

        sw.Stop();
        return (sw.ElapsedMilliseconds, success, failed);
    }

    public async Task<(long elapsedMs, int processedTasks)> BenchmarkBlockingCollection(
        int taskCount,
        int workerCount
    )
    {
        TaskQueueManager queue = new(Math.Max(taskCount, 1));
        int localProcessed = 0;
        using CancellationTokenSource cts = new();

        Stopwatch sw = Stopwatch.StartNew();
        Task<int> processingTask = queue.ProcessTasks(workerCount, cts.Token);

        for (int i = 0; i < taskCount; i++)
        {
            bool added = queue.AddTask(
                $"Task-{i}",
                () =>
                {
                    Interlocked.Increment(ref localProcessed);
                }
            );

            if (!added)
            {
                throw new InvalidOperationException("Could not add a task to BlockingCollection.");
            }
        }

        queue.CompleteAdding();
        int processedByWorkers = await processingTask;
        sw.Stop();

        return (sw.ElapsedMilliseconds, Math.Min(localProcessed, processedByWorkers));
    }

    public (long elapsedMs, int successfulOperations) BenchmarkSynchronizedDictionary(
        int operationCount
    )
    {
        Dictionary<int, int> dictionary = new();
        object sync = new();
        int success = 0;

        Stopwatch sw = Stopwatch.StartNew();

        Parallel.For(
            0,
            operationCount,
            i =>
            {
                lock (sync)
                {
                    if (!dictionary.ContainsKey(i))
                    {
                        dictionary[i] = i;
                        success++;
                    }

                    if (dictionary.TryGetValue(i, out _))
                    {
                        success++;
                    }

                    if (dictionary.Remove(i))
                    {
                        success++;
                    }
                }
            }
        );

        sw.Stop();
        return (sw.ElapsedMilliseconds, success);
    }

    public async Task CompareAllCollections()
    {
        const int operationCount = 100_000;
        const int taskCount = 50_000;
        const int workerCount = 10;

        (long concurrentMs, int concurrentSuccess, int concurrentFail) =
            BenchmarkConcurrentDictionary(operationCount);
        (long blockingMs, int processedTasks) = await BenchmarkBlockingCollection(
            taskCount,
            workerCount
        );
        (long syncMs, int syncSuccess) = BenchmarkSynchronizedDictionary(operationCount);

        double concurrentOpsPerSec =
            concurrentMs == 0 ? concurrentSuccess : concurrentSuccess / (concurrentMs / 1000.0);
        double blockingTasksPerSec =
            blockingMs == 0 ? processedTasks : processedTasks / (blockingMs / 1000.0);
        double syncOpsPerSec = syncMs == 0 ? syncSuccess : syncSuccess / (syncMs / 1000.0);
        double speedup = syncMs == 0 ? 0 : (double)syncMs / Math.Max(concurrentMs, 1);

        Console.WriteLine("=== Сравнение потокобезопасных коллекций ===");
        Console.WriteLine("ConcurrentDictionary:");
        Console.WriteLine($"  Время выполнения: {concurrentMs} мс");
        Console.WriteLine($"  Успешные операции: {concurrentSuccess}");
        Console.WriteLine($"  Неудачные операции: {concurrentFail}");
        Console.WriteLine($"  Производительность: {concurrentOpsPerSec:F2} операций/сек");
        Console.WriteLine();
        Console.WriteLine("BlockingCollection:");
        Console.WriteLine($"  Время выполнения: {blockingMs} мс");
        Console.WriteLine($"  Обработанные задачи: {processedTasks}");
        Console.WriteLine($"  Производительность: {blockingTasksPerSec:F2} задач/сек");
        Console.WriteLine();
        Console.WriteLine("Synchronized Dictionary (с lock):");
        Console.WriteLine($"  Время выполнения: {syncMs} мс");
        Console.WriteLine($"  Успешные операции: {syncSuccess}");
        Console.WriteLine($"  Производительность: {syncOpsPerSec:F2} операций/сек");
        Console.WriteLine();
        Console.WriteLine($"Ускорение ConcurrentDictionary vs Synchronized: {speedup:F2}x");
    }
}
