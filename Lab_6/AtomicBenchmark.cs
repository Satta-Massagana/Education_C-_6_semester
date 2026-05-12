using System.Diagnostics;
using System.Globalization;

namespace Lab_6;

public static class AtomicBenchmark
{
    public static bool LastLockFreeStackCorrect { get; private set; }

    private sealed class LockedCounter
    {
        private readonly object _gate = new();
        private long _value;

        public void Increment()
        {
            lock (_gate)
            {
                _value++;
            }
        }

        public void Decrement()
        {
            lock (_gate)
            {
                _value--;
            }
        }

        public long Value
        {
            get
            {
                lock (_gate)
                {
                    return _value;
                }
            }
        }
    }

    public static (long ElapsedMs, long FinalValue, bool Correct) BenchmarkInterlockedCounter(
        int operationCount,
        int threadCount
    )
    {
        if (operationCount < 0 || threadCount <= 0)
            throw new ArgumentOutOfRangeException();

        var counter = new AtomicCounter(0);
        var incTotal = operationCount / 2;
        var decTotal = operationCount - incTotal;
        var incPerThread = Distribute(incTotal, threadCount);
        var decPerThread = Distribute(decTotal, threadCount);

        var sw = Stopwatch.StartNew();
        RunThreads(
            threadCount,
            tid =>
            {
                for (var i = 0; i < incPerThread[tid]; i++)
                    counter.Increment();
                for (var i = 0; i < decPerThread[tid]; i++)
                    counter.Decrement();
            }
        );
        sw.Stop();

        var final = counter.Value;
        var correct = final == 0;
        return (sw.ElapsedMilliseconds, final, correct);
    }

    public static (long ElapsedMs, long FinalValue, bool Correct) BenchmarkLockedCounter(
        int operationCount,
        int threadCount
    )
    {
        if (operationCount < 0 || threadCount <= 0)
            throw new ArgumentOutOfRangeException();

        var counter = new LockedCounter();
        var incTotal = operationCount / 2;
        var decTotal = operationCount - incTotal;
        var incPerThread = Distribute(incTotal, threadCount);
        var decPerThread = Distribute(decTotal, threadCount);

        var sw = Stopwatch.StartNew();
        RunThreads(
            threadCount,
            tid =>
            {
                for (var i = 0; i < incPerThread[tid]; i++)
                    counter.Increment();
                for (var i = 0; i < decPerThread[tid]; i++)
                    counter.Decrement();
            }
        );
        sw.Stop();

        var final = counter.Value;
        var correct = final == 0;
        return (sw.ElapsedMilliseconds, final, correct);
    }

    public static (long ElapsedMs, long SuccessfulOperations) BenchmarkLockFreeStack(
        int operationCount,
        int threadCount
    )
    {
        if (operationCount < 0 || threadCount <= 0)
            throw new ArgumentOutOfRangeException();

        var stack = new LockFreeStack<int>();
        var pushesPerThread = Distribute(operationCount, threadCount);
        long successfulPushes = 0;
        long successfulPops = 0;

        var sw = Stopwatch.StartNew();

        RunThreads(
            threadCount,
            tid =>
            {
                var n = pushesPerThread[tid];
                for (var i = 0; i < n; i++)
                {
                    stack.Push(i);
                    Interlocked.Increment(ref successfulPushes);
                }
            }
        );

        RunThreads(
            threadCount,
            _ =>
            {
                while (true)
                {
                    if (stack.TryPop(out _))
                    {
                        Interlocked.Increment(ref successfulPops);
                        continue;
                    }

                    if (stack.IsEmpty())
                        break;
                }
            }
        );

        sw.Stop();

        LastLockFreeStackCorrect =
            stack.IsEmpty()
            && Interlocked.Read(ref successfulPushes) == operationCount
            && Interlocked.Read(ref successfulPops) == operationCount;

        var successful =
            Interlocked.Read(ref successfulPushes) + Interlocked.Read(ref successfulPops);
        return (sw.ElapsedMilliseconds, successful);
    }

    public static void CompareAllApproaches()
    {
        const int counterOps = 1_000_000;
        const int counterThreads = 100;
        const int stackOps = 100_000;
        const int stackThreads = 50;

        var (tIc, vIc, cIc) = BenchmarkInterlockedCounter(counterOps, counterThreads);
        var (tLc, vLc, cLc) = BenchmarkLockedCounter(counterOps, counterThreads);
        var (tSt, succSt) = BenchmarkLockFreeStack(stackOps, stackThreads);

        var speedup = tLc > 0 ? (double)tLc / Math.Max(1, tIc) : 0;

        Console.WriteLine();
        Console.WriteLine("=== Сравнение атомарных операций и блокировок ===");
        Console.WriteLine("Interlocked Counter:");
        Console.WriteLine($"  Время выполнения: {tIc} мс");
        Console.WriteLine($"  Итоговое значение: {vIc}");
        Console.WriteLine($"  Корректность: {(cIc ? "Да" : "Нет")}");
        Console.WriteLine();
        Console.WriteLine("Locked Counter (с lock):");
        Console.WriteLine($"  Время выполнения: {tLc} мс");
        Console.WriteLine($"  Итоговое значение: {vLc}");
        Console.WriteLine($"  Корректность: {(cLc ? "Да" : "Нет")}");
        Console.WriteLine();
        Console.WriteLine("Lock-Free Stack:");
        Console.WriteLine($"  Время выполнения: {tSt} мс");
        Console.WriteLine($"  Успешные операции: {succSt}");
        Console.WriteLine();
        Console.WriteLine(
            $"Ускорение Interlocked vs Locked: {speedup.ToString("0.##", CultureInfo.InvariantCulture)}x"
        );
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

    private static void RunThreads(int threadCount, Action<int> body)
    {
        var threads = new Thread[threadCount];
        for (var i = 0; i < threadCount; i++)
        {
            var tid = i;
            threads[i] = new Thread(() => body(tid));
        }

        foreach (var t in threads)
            t.Start();
        foreach (var t in threads)
            t.Join();
    }
}
