using System.Diagnostics;
using System.Globalization;

namespace Lab_6;

internal static class Program
{
    private const int CounterOperations = 1_000_000;
    private const int CounterThreads = 100;
    private const int StackOperations = 100_000;
    private const int StackThreads = 50;
    private const int StatisticsRequests = 50_000;
    private const int StatisticsThreads = 200;
    private const int RandomSeed = 42;

    private static void Main()
    {
        Thread.CurrentThread.Name = "Main";

        var (icMs, icFinal, icCorrect) = AtomicBenchmark.BenchmarkInterlockedCounter(
            CounterOperations,
            CounterThreads
        );
        var (lcMs, lcFinal, lcCorrect) = AtomicBenchmark.BenchmarkLockedCounter(
            CounterOperations,
            CounterThreads
        );
        var (stMs, stOps) = AtomicBenchmark.BenchmarkLockFreeStack(StackOperations, StackThreads);
        var stCorrect = AtomicBenchmark.LastLockFreeStackCorrect;

        var (statMs, statOk, statFail) = RunStatisticsBenchmark();

        var speedup = lcMs > 0 ? (double)lcMs / Math.Max(1, icMs) : 0;
        var overheadPct = lcMs > 0 ? (lcMs - icMs) * 100.0 / lcMs : 0;

        var n0 = new NumberFormatInfo { NumberGroupSeparator = ",", NumberDecimalDigits = 0 };

        Console.WriteLine();
        Console.WriteLine("=== Результаты тестирования атомарных операций ===");
        Console.WriteLine("Interlocked Counter:");
        Console.WriteLine($"  Количество операций: {CounterOperations.ToString("N", n0)}");
        Console.WriteLine($"  Количество потоков: {CounterThreads}");
        Console.WriteLine($"  Время выполнения: {icMs} мс");
        Console.WriteLine($"  Итоговое значение: {icFinal}");
        Console.WriteLine($"  Корректность: {(icCorrect ? "Да" : "Нет")}");
        Console.WriteLine();
        Console.WriteLine("Locked Counter:");
        Console.WriteLine($"  Количество операций: {CounterOperations.ToString("N", n0)}");
        Console.WriteLine($"  Количество потоков: {CounterThreads}");
        Console.WriteLine($"  Время выполнения: {lcMs} мс");
        Console.WriteLine($"  Итоговое значение: {lcFinal}");
        Console.WriteLine($"  Корректность: {(lcCorrect ? "Да" : "Нет")}");
        Console.WriteLine();
        Console.WriteLine("Lock-Free Stack:");
        Console.WriteLine($"  Количество операций: {StackOperations.ToString("N", n0)}");
        Console.WriteLine($"  Количество потоков: {StackThreads}");
        Console.WriteLine($"  Время выполнения: {stMs} мс");
        Console.WriteLine($"  Успешные операции: {stOps}");
        Console.WriteLine($"  Корректность: {(stCorrect ? "Да" : "Нет")}");
        Console.WriteLine();
        Console.WriteLine("Statistics Tracker:");
        Console.WriteLine($"  Количество запросов: {StatisticsRequests.ToString("N", n0)}");
        Console.WriteLine($"  Количество потоков: {StatisticsThreads}");
        Console.WriteLine($"  Время выполнения: {statMs} мс");
        Console.WriteLine($"  Успешные запросы: {statOk}");
        Console.WriteLine($"  Неудачные запросы: {statFail}");
        Console.WriteLine();
        Console.WriteLine("Сравнение производительности:");
        Console.WriteLine(
            $"  Interlocked vs Locked: {speedup.ToString("0.##", CultureInfo.InvariantCulture)}x"
        );
        Console.WriteLine(
            $"  Накладные расходы блокировок: {overheadPct.ToString("0.##", CultureInfo.InvariantCulture)}%"
        );

        var anyIssue = !icCorrect || !lcCorrect || !stCorrect;
        Console.WriteLine();
        Console.WriteLine(
            anyIssue
                ? "Обнаружены возможные проблемы синхронизации или расхождения итоговых значений."
                : "Проблем синхронизации не обнаружено: счётчики сошлись с ожиданием, стек пуст после полного извлечения."
        );

        AtomicBenchmark.CompareAllApproaches();
    }

    private static (long ElapsedMs, long Successful, long Failed) RunStatisticsBenchmark()
    {
        var tracker = new StatisticsTracker();
        tracker.Reset();

        var perThread = Distribute(StatisticsRequests, StatisticsThreads);
        var sw = Stopwatch.StartNew();

        var threads = new Thread[StatisticsThreads];
        for (var i = 0; i < StatisticsThreads; i++)
        {
            var tid = i;
            threads[i] = new Thread(() =>
            {
                var rng = new Random(RandomSeed + tid);
                var n = perThread[tid];
                for (var j = 0; j < n; j++)
                {
                    var success = rng.Next(2) == 0;
                    var processing = rng.Next(0, 50);
                    tracker.RecordRequest(success, processing);
                }
            });
        }

        foreach (var t in threads)
            t.Start();
        foreach (var t in threads)
            t.Join();

        sw.Stop();

        return (sw.ElapsedMilliseconds, tracker.SuccessfulRequests, tracker.FailedRequests);
    }

    private static int[] Distribute(int total, int threads)
    {
        var per = new int[threads];
        var baseCount = total / threads;
        var rem = total % threads;
        for (var i = 0; i < threads; i++)
            per[i] = baseCount + (i < rem ? 1 : 0);
        return per;
    }
}
