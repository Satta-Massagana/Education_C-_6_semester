// Бенчмарк для второго блока отчёта
// Сравниваем только словари: ConcurrentDictionary vs Synchronized Dictionary
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Lab5.ConcurrentCollections;

public readonly record struct BenchmarkResult(
    double ElapsedMs,
    int SuccessfulOperations,
    int FailedOperations
);

public sealed class CollectionBenchmark
{
    // Несколько прогонов и среднее время — чтобы убрать «скачки» из-за грубых миллисекунд
    private const int MeasurementRuns = 3;

    // ConcurrentDictionary: потокобезопасность внутри коллекции, без ручного lock
    public BenchmarkResult BenchmarkConcurrentDictionary(int operationCount, int keyPoolSize = 0) =>
        MeasureAverage(() =>
        {
            ConcurrentDictionary<int, int> dictionary = new();
            return RunWorkload(
                operationCount,
                ResolveKeyPoolSize(operationCount, keyPoolSize),
                key =>
                {
                    bool addOk = dictionary.TryAdd(key, key);
                    bool readOk = dictionary.TryGetValue(key, out _);
                    bool removeOk = dictionary.TryRemove(key, out _);
                    return (addOk, readOk, removeOk);
                }
            );
        });

    public async Task<BenchmarkResult> BenchmarkBlockingCollection(int taskCount, int workerCount)
    {
        List<double> runTimes = new(MeasurementRuns);
        int processedTasks = 0;

        for (int run = 0; run < MeasurementRuns; run++)
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
                    () => Interlocked.Increment(ref localProcessed)
                );

                if (!added)
                {
                    throw new InvalidOperationException(
                        "Could not add a task to BlockingCollection."
                    );
                }
            }

            queue.CompleteAdding();
            int processedByWorkers = await processingTask;
            sw.Stop();

            processedTasks = Math.Min(localProcessed, processedByWorkers);
            runTimes.Add(sw.Elapsed.TotalMilliseconds);
        }

        return new BenchmarkResult(
            ElapsedMs: runTimes.Average(),
            SuccessfulOperations: processedTasks,
            FailedOperations: 0
        );
    }

    // Synchronized Dictionary с lock.
    public BenchmarkResult BenchmarkSynchronizedDictionary(
        int operationCount,
        int keyPoolSize = 0
    ) =>
        MeasureAverage(() =>
        {
            Dictionary<int, int> dictionary = new();
            object sync = new();

            return RunWorkload(
                operationCount,
                ResolveKeyPoolSize(operationCount, keyPoolSize),
                key =>
                {
                    lock (sync)
                    {
                        bool addOk = false;
                        if (!dictionary.ContainsKey(key))
                        {
                            dictionary[key] = key;
                            addOk = true;
                        }

                        bool readOk = dictionary.TryGetValue(key, out _);
                        bool removeOk = dictionary.Remove(key);
                        return (addOk, readOk, removeOk);
                    }
                }
            );
        });

    public void CompareAllCollections()
    {
        const int operationCount = 100_000;

        // keyPoolSize = 0 => уникальные ключи (низкая конкуренция за один и тот же ключ).
        BenchmarkResult concurrent = BenchmarkConcurrentDictionary(operationCount);
        BenchmarkResult sync = BenchmarkSynchronizedDictionary(operationCount);

        double concurrentOpsPerSec = OperationsPerSecond(
            concurrent.SuccessfulOperations,
            concurrent.ElapsedMs
        );
        double syncOpsPerSec = OperationsPerSecond(sync.SuccessfulOperations, sync.ElapsedMs);

        // Разница во времени между подходами
        double syncOverheadPercent =
            sync.ElapsedMs <= 0
                ? 0
                : Math.Abs((sync.ElapsedMs - concurrent.ElapsedMs) / sync.ElapsedMs * 100.0);

        Console.WriteLine("=== Сравнение ConcurrentDictionary vs Synchronized Dictionary ===");
        Console.WriteLine(
            $"Среднее время рассчитано по {ReportFormat.Integer(MeasurementRuns)} прогонам."
        );
        Console.WriteLine();
        Console.WriteLine("ConcurrentDictionary:");
        Console.WriteLine(
            $"  Время выполнения (среднее из {ReportFormat.Integer(MeasurementRuns)}): {ReportFormat.Number(concurrent.ElapsedMs)} мс"
        );
        Console.WriteLine(
            $"  Успешные операции: {ReportFormat.Integer(concurrent.SuccessfulOperations)}"
        );
        Console.WriteLine(
            $"  Неудачные операции: {ReportFormat.Integer(concurrent.FailedOperations)}"
        );
        Console.WriteLine(
            $"  Производительность: {ReportFormat.Number(concurrentOpsPerSec)} {ReportFormat.OperationsPerSecond}"
        );
        Console.WriteLine();
        Console.WriteLine("Synchronized Dictionary (с lock):");
        Console.WriteLine(
            $"  Время выполнения (среднее из {ReportFormat.Integer(MeasurementRuns)}): {ReportFormat.Number(sync.ElapsedMs)} мс"
        );
        Console.WriteLine(
            $"  Успешные операции: {ReportFormat.Integer(sync.SuccessfulOperations)}"
        );
        Console.WriteLine($"  Неудачные операции: {ReportFormat.Integer(sync.FailedOperations)}");
        Console.WriteLine(
            $"  Производительность: {ReportFormat.Number(syncOpsPerSec)} {ReportFormat.OperationsPerSecond}"
        );
        Console.WriteLine();
        Console.WriteLine(DescribeComparison(concurrent.ElapsedMs, sync.ElapsedMs));
        Console.WriteLine(
            $"Накладные расходы синхронизации: {ReportFormat.Number(syncOverheadPercent)}%"
        );
    }

    private static BenchmarkResult MeasureAverage(Func<(int success, int failed)> workload)
    {
        List<double> runTimes = new(MeasurementRuns);
        int success = 0;
        int failed = 0;

        for (int run = 0; run < MeasurementRuns; run++)
        {
            Stopwatch sw = Stopwatch.StartNew();
            (int runSuccess, int runFailed) = workload();
            sw.Stop();

            success = runSuccess;
            failed = runFailed;
            runTimes.Add(sw.Elapsed.TotalMilliseconds);
        }

        return new BenchmarkResult(
            ElapsedMs: runTimes.Average(),
            SuccessfulOperations: success,
            FailedOperations: failed
        );
    }

    // Общая нагрузка для обоих словарей: add + read + remove на каждой итерации.
    // Одинаковый подсчёт через Interlocked — иначе сравнение времени нечестное.
    private static (int success, int failed) RunWorkload(
        int operationCount,
        int keyPoolSize,
        Func<int, (bool addOk, bool readOk, bool removeOk)> executeOnKey
    )
    {
        int success = 0;
        int failed = 0;

        Parallel.For(
            0,
            operationCount,
            i =>
            {
                int key = i % keyPoolSize;
                (bool addOk, bool readOk, bool removeOk) = executeOnKey(key);

                AccountOperation(addOk, ref success, ref failed);
                AccountOperation(readOk, ref success, ref failed);
                AccountOperation(removeOk, ref success, ref failed);
            }
        );

        return (success, failed);
    }

    private static void AccountOperation(bool isSuccessful, ref int success, ref int failed)
    {
        if (isSuccessful)
        {
            Interlocked.Increment(ref success);
        }
        else
        {
            Interlocked.Increment(ref failed);
        }
    }

    // keyPoolSize = 0 => каждый поток работает со своим ключом (i % operationCount == i).
    private static int ResolveKeyPoolSize(int operationCount, int keyPoolSize) =>
        keyPoolSize <= 0 ? operationCount : Math.Min(keyPoolSize, operationCount);

    private static double OperationsPerSecond(int operations, double elapsedMs) =>
        elapsedMs <= 0 ? operations : operations / (elapsedMs / 1000.0);

    private static string DescribeComparison(double concurrentMs, double synchronizedMs)
    {
        if (concurrentMs <= 0 || synchronizedMs <= 0)
        {
            return "Итог: недостаточно данных для расчета.";
        }

        const double threshold = 1.05;

        if (synchronizedMs / concurrentMs >= threshold)
        {
            double factor = synchronizedMs / concurrentMs;
            return $"Итог: ConcurrentDictionary быстрее в {ReportFormat.Number(factor)}x";
        }

        if (concurrentMs / synchronizedMs >= threshold)
        {
            double factor = concurrentMs / synchronizedMs;
            return $"Итог: Synchronized Dictionary быстрее в {ReportFormat.Number(factor)}x";
        }

        return "Итог: производительность примерно одинакова";
    }
}
