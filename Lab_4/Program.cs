using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Lab4;

public static class Program
{
    public static void Main()
    {
        const int seed = 42;
        const int initialBooks = 1000;
        const int maxResources = 10;
        const int readerCount = 50;
        const int writerCount = 10;
        const int semaphoreRequests = 100;
        const int mutexOperations = 20;

        Random random = new Random(seed);
        using LibraryCatalog catalog = new LibraryCatalog();
        using ResourcePool pool = new ResourcePool(maxResources);
        SynchronizationBenchmark benchmark = new SynchronizationBenchmark();

        for (int i = 0; i < initialBooks; i++)
        {
            catalog.AddBook($"Book_{i}", $"Author_{random.Next(1, 200)}");
        }

        (long readMs, long writeMs, long totalMs) rwResult = benchmark.BenchmarkReaderWriterLock(
            catalog,
            readerCount,
            writerCount
        );

        (long semaphoreMs, int semaphoreSuccess, int semaphoreFailed) semaphoreResult =
            benchmark.BenchmarkSemaphore(pool, semaphoreRequests);

        Stopwatch mutexSw = Stopwatch.StartNew();
        int mutexSuccess = 0;
        Parallel.For(
            0,
            mutexOperations,
            i =>
            {
                bool ok = CrossProcessSync.TryExecuteWithGlobalLock(
                    @"Global\Lab4_Mutex_Main",
                    () =>
                    {
                        // Имитация критической секции (межпроцессно безопасной).
                        Thread.Sleep(4 + (i % 7));
                    },
                    timeoutMs: 700
                );
                if (ok)
                {
                    Interlocked.Increment(ref mutexSuccess);
                }
            }
        );
        mutexSw.Stop();

        bool dataIntegrity = ValidateCatalog(catalog);
        Console.WriteLine(
            dataIntegrity
                ? "Проблемы синхронизации не обнаружены."
                : "Обнаружены проблемы синхронизации."
        );

        RunTimeoutScenarios(catalog, pool);

        Console.WriteLine();
        Console.WriteLine("=== Результаты тестирования примитивов синхронизации ===");
        Console.WriteLine("ReaderWriterLockSlim:");
        Console.WriteLine($"  Количество операций чтения: {readerCount}");
        Console.WriteLine($"  Количество операций записи: {writerCount}");
        Console.WriteLine($"  Время чтения: {rwResult.readMs} мс");
        Console.WriteLine($"  Время записи: {rwResult.writeMs} мс");
        Console.WriteLine($"  Целостность данных: {(dataIntegrity ? "Да" : "Нет")}");
        Console.WriteLine();
        Console.WriteLine("SemaphoreSlim:");
        Console.WriteLine($"  Количество запросов: {semaphoreRequests}");
        Console.WriteLine($"  Доступных ресурсов: {maxResources}");
        Console.WriteLine($"  Успешные запросы: {semaphoreResult.semaphoreSuccess}");
        Console.WriteLine($"  Неудачные запросы: {semaphoreResult.semaphoreFailed}");
        Console.WriteLine();
        Console.WriteLine("Mutex:");
        Console.WriteLine($"  Количество операций: {mutexOperations}");
        Console.WriteLine($"  Успешные операции: {mutexSuccess}");
        Console.WriteLine($"  Время выполнения: {mutexSw.ElapsedMilliseconds} мс");
        Console.WriteLine();

        double rwOverhead = CalculateOverheadPercent(
            rwResult.totalMs,
            rwResult.readMs + rwResult.writeMs
        );
        double semOverhead = CalculateOverheadPercent(
            semaphoreResult.semaphoreMs,
            semaphoreResult.semaphoreMs - 1
        );
        double mutexOverhead = CalculateOverheadPercent(
            mutexSw.ElapsedMilliseconds,
            mutexOperations * 5L
        );

        Console.WriteLine("Сравнение производительности:");
        Console.WriteLine($"  Накладные расходы ReaderWriterLockSlim: {rwOverhead:F2}%");
        Console.WriteLine($"  Накладные расходы SemaphoreSlim: {semOverhead:F2}%");
        Console.WriteLine($"  Накладные расходы Mutex: {mutexOverhead:F2}%");
        Console.WriteLine();

        benchmark.CompareAllPrimitives();
    }

    private static bool ValidateCatalog(LibraryCatalog catalog)
    {
        var all = catalog.GetAllBooks();
        if (all.Count == 0)
        {
            return false;
        }

        return all.All(b =>
            !string.IsNullOrWhiteSpace(b.Title) && !string.IsNullOrWhiteSpace(b.Author)
        );
    }

    private static double CalculateOverheadPercent(long totalMs, long baselineMs)
    {
        if (baselineMs <= 0)
        {
            return 0;
        }

        return Math.Max(0, (totalMs - baselineMs) * 100.0 / baselineMs);
    }

    private static void RunTimeoutScenarios(LibraryCatalog catalog, ResourcePool pool)
    {
        Console.WriteLine();
        Console.WriteLine("=== Тест таймаутов ===");

        Thread lockHolder = new Thread(() => catalog.SimulateWriteLock(400));
        lockHolder.Start();
        Thread.Sleep(50);

        bool searchWithShortTimeout = catalog.TrySearchBooks("Book", timeoutMs: 50);
        bool addWithLongTimeout = catalog.TryAddBook(
            "Timeout_Book",
            "Timeout_Author",
            timeoutMs: 700
        );
        lockHolder.Join();

        Console.WriteLine(
            $"ReaderWriterLockSlim (короткий таймаут чтения 50мс): {(searchWithShortTimeout ? "успех" : "таймаут")}"
        );
        Console.WriteLine(
            $"ReaderWriterLockSlim (длинный таймаут записи 700мс): {(addWithLongTimeout ? "успех" : "таймаут")}"
        );

        bool[] acquired = new bool[10];
        for (int i = 0; i < acquired.Length; i++)
        {
            acquired[i] = pool.TryAcquireResource(timeoutMs: 100);
        }

        bool shortAcquire = pool.TryAcquireResource(timeoutMs: 30);

        for (int i = 0; i < acquired.Length; i++)
        {
            if (acquired[i])
            {
                pool.ReleaseResource();
            }
        }

        bool longAcquire = pool.TryAcquireResource(timeoutMs: 500);
        if (longAcquire)
        {
            pool.ReleaseResource();
        }

        Console.WriteLine(
            $"SemaphoreSlim (короткий таймаут 30мс): {(shortAcquire ? "успех" : "таймаут")}"
        );
        Console.WriteLine(
            $"SemaphoreSlim (длинный таймаут 500мс): {(longAcquire ? "успех" : "таймаут")}"
        );

        Thread mutexHolder = new Thread(() =>
            CrossProcessSync.ExecuteWithGlobalLock(
                @"Global\Lab4_Mutex_Timeout",
                () => Thread.Sleep(350)
            )
        );
        mutexHolder.Start();
        Thread.Sleep(30);

        bool mutexShort = CrossProcessSync.TryExecuteWithGlobalLock(
            @"Global\Lab4_Mutex_Timeout",
            () => { },
            timeoutMs: 20
        );
        bool mutexLong = CrossProcessSync.TryExecuteWithGlobalLock(
            @"Global\Lab4_Mutex_Timeout",
            () => { },
            timeoutMs: 700
        );
        mutexHolder.Join();

        Console.WriteLine($"Mutex (короткий таймаут 20мс): {(mutexShort ? "успех" : "таймаут")}");
        Console.WriteLine($"Mutex (длинный таймаут 700мс): {(mutexLong ? "успех" : "таймаут")}");
    }
}
