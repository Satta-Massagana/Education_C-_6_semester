using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Lab4;

public sealed class SynchronizationBenchmark
{
    private readonly Random _random = new Random(42);

    public (long readMs, long writeMs, long totalMs) BenchmarkReaderWriterLock(
        LibraryCatalog catalog,
        int readerCount,
        int writerCount
    )
    {
        if (catalog is null)
        {
            throw new ArgumentNullException(nameof(catalog));
        }

        long readTicks = 0;
        long writeTicks = 0;
        Stopwatch total = Stopwatch.StartNew();

        Thread[] readers = new Thread[readerCount];
        Thread[] writers = new Thread[writerCount];

        for (int i = 0; i < readerCount; i++)
        {
            readers[i] = new Thread(() =>
            {
                Stopwatch sw = Stopwatch.StartNew();
                _ = catalog.SearchBooks("Book");
                sw.Stop();
                Interlocked.Add(ref readTicks, sw.ElapsedTicks);
            });
        }

        for (int i = 0; i < writerCount; i++)
        {
            int local = i;
            writers[i] = new Thread(() =>
            {
                Stopwatch sw = Stopwatch.StartNew();
                string title = $"WriterBook_{local}_{_random.Next(1, 100000)}";
                catalog.TryAddBook(title, "BenchmarkWriter", timeoutMs: 500);
                sw.Stop();
                Interlocked.Add(ref writeTicks, sw.ElapsedTicks);
            });
        }

        foreach (Thread writer in writers)
        {
            writer.Start();
        }

        foreach (Thread reader in readers)
        {
            reader.Start();
        }

        foreach (Thread writer in writers)
        {
            writer.Join();
        }

        foreach (Thread reader in readers)
        {
            reader.Join();
        }

        total.Stop();

        long readMs = readTicks * 1000 / Stopwatch.Frequency;
        long writeMs = writeTicks * 1000 / Stopwatch.Frequency;
        return (readMs, writeMs, total.ElapsedMilliseconds);
    }

    public (long elapsedMs, int success, int failed) BenchmarkSemaphore(
        ResourcePool pool,
        int requestCount
    )
    {
        if (pool is null)
        {
            throw new ArgumentNullException(nameof(pool));
        }

        int success = 0;
        int failed = 0;
        Stopwatch sw = Stopwatch.StartNew();

        Parallel.For(
            0,
            requestCount,
            i =>
            {
                bool acquired = pool.TryAcquireResource(timeoutMs: 200);
                if (!acquired)
                {
                    Interlocked.Increment(ref failed);
                    return;
                }

                try
                {
                    Thread.Sleep(5 + (i % 11));
                    Interlocked.Increment(ref success);
                }
                finally
                {
                    pool.ReleaseResource();
                }
            }
        );

        sw.Stop();
        return (sw.ElapsedMilliseconds, success, failed);
    }

    public void CompareAllPrimitives()
    {
        using LibraryCatalog catalog = new LibraryCatalog();
        for (int i = 0; i < 1000; i++)
        {
            catalog.AddBook($"Book {i}", $"Author {i % 50}");
        }

        using ResourcePool pool = new ResourcePool(10);

        (long readMs, long writeMs, long totalMs) rw = BenchmarkReaderWriterLock(catalog, 50, 10);
        (long semMs, int semOk, int semFail) sem = BenchmarkSemaphore(pool, 100);

        Stopwatch mutexSw = Stopwatch.StartNew();
        int mutexSuccess = 0;
        Parallel.For(
            0,
            20,
            i =>
            {
                bool ok = CrossProcessSync.TryExecuteWithGlobalLock(
                    @"Global\Lab4_Mutex_Demo",
                    () => Thread.Sleep(3 + (i % 5)),
                    timeoutMs: 500
                );
                if (ok)
                {
                    Interlocked.Increment(ref mutexSuccess);
                }
            }
        );
        mutexSw.Stop();

        Console.WriteLine("=== Сравнение примитивов синхронизации ===");
        Console.WriteLine("ReaderWriterLockSlim:");
        Console.WriteLine($"  Чтение: {rw.readMs} мс");
        Console.WriteLine($"  Запись: {rw.writeMs} мс");
        Console.WriteLine($"  Общее время: {rw.totalMs} мс");
        Console.WriteLine();
        Console.WriteLine("SemaphoreSlim:");
        Console.WriteLine($"  Время выполнения: {sem.semMs} мс");
        Console.WriteLine($"  Успешные запросы: {sem.semOk}");
        Console.WriteLine($"  Неудачные запросы: {sem.semFail}");
        Console.WriteLine();
        Console.WriteLine("Mutex:");
        Console.WriteLine($"  Время выполнения: {mutexSw.ElapsedMilliseconds} мс");
        Console.WriteLine($"  Успешные операции: {mutexSuccess}");
        Console.WriteLine();
    }
}
