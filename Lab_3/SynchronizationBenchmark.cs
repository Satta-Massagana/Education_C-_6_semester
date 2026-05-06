using System;
using System.Collections.Generic;
using System.Diagnostics;

public class SynchronizationBenchmark
{
    public (TimeSpan elapsed, decimal balance, bool isCorrect) BenchmarkNoSync(
        BankAccount account,
        List<decimal> transactions,
        decimal expectedBalance
    )
    {
        var processor = new TransactionProcessor();
        var sw = Stopwatch.StartNew();
        decimal result = processor.ProcessTransactionsConcurrently(account, transactions);
        sw.Stop();
        bool isCorrect = Math.Abs(result - expectedBalance) < 0.001m;
        return (sw.Elapsed, result, isCorrect);
    }

    public (TimeSpan elapsed, decimal balance, bool isCorrect) BenchmarkWithLock(
        BankAccount account,
        List<decimal> transactions,
        decimal expectedBalance
    )
    {
        var processor = new TransactionProcessor();
        var sw = Stopwatch.StartNew();
        decimal result = processor.ProcessTransactionsWithLock(account, transactions);
        sw.Stop();
        bool isCorrect = Math.Abs(result - expectedBalance) < 0.001m;
        return (sw.Elapsed, result, isCorrect);
    }

    public (TimeSpan elapsed, decimal balance, bool isCorrect) BenchmarkWithMonitor(
        BankAccount account,
        List<decimal> transactions,
        decimal expectedBalance
    )
    {
        var processor = new TransactionProcessor();
        var sw = Stopwatch.StartNew();
        decimal result = processor.ProcessTransactionsWithMonitor(account, transactions);
        sw.Stop();
        bool isCorrect = Math.Abs(result - expectedBalance) < 0.001m;
        return (sw.Elapsed, result, isCorrect);
    }

    public void CompareAllApproaches(
        BankAccount accountNoSync,
        BankAccount accountLock,
        BankAccount accountMonitor,
        List<decimal> transactions,
        decimal expectedBalance
    )
    {
        var (noSyncTime, noSyncBalance, noSyncCorrect) = BenchmarkNoSync(
            accountNoSync,
            transactions,
            expectedBalance
        );
        double noSyncMs = noSyncTime.TotalMilliseconds;

        var (lockTime, lockBalance, lockCorrect) = BenchmarkWithLock(
            accountLock,
            transactions,
            expectedBalance
        );
        double lockMs = lockTime.TotalMilliseconds;

        var (monitorTime, monitorBalance, monitorCorrect) = BenchmarkWithMonitor(
            accountMonitor,
            transactions,
            expectedBalance
        );
        double monitorMs = monitorTime.TotalMilliseconds;

        Console.WriteLine("=== Сравнение подходов к синхронизации ===");
        Console.WriteLine(
            $"Без синхронизации: {noSyncMs:F2} мс, результат: {(noSyncCorrect ? "корректный" : "некорректный")}"
        );
        Console.WriteLine(
            $"С использованием lock: {lockMs:F2} мс, результат: {(lockCorrect ? "корректный" : "некорректный")}"
        );
        Console.WriteLine(
            $"С использованием Monitor: {monitorMs:F2} мс, результат: {(monitorCorrect ? "корректный" : "некорректный")}"
        );
        Console.WriteLine();

        Console.WriteLine($"Без синхронизации:");
        Console.WriteLine($"  Время: {noSyncMs:F2} мс");
        Console.WriteLine($"  Итоговый баланс: {noSyncBalance:C}");
        Console.WriteLine($"  Корректность: {(noSyncCorrect ? "Да" : "Нет")}");
        Console.WriteLine($"  Гонки данных: {(noSyncCorrect ? "Нет" : "Да")}");

        Console.WriteLine($"\nС использованием lock:");
        Console.WriteLine($"  Время: {lockMs:F2} мс");
        Console.WriteLine($"  Итоговый баланс: {lockBalance:C}");
        Console.WriteLine($"  Корректность: {(lockCorrect ? "Да" : "Нет")}");
        double lockOverhead = noSyncMs > 0 ? (lockMs - noSyncMs) / noSyncMs * 100 : 0;
        Console.WriteLine($"  Накладные расходы: {lockOverhead:F2}%");

        Console.WriteLine($"\nС использованием Monitor:");
        Console.WriteLine($"  Время: {monitorMs:F2} мс");
        Console.WriteLine($"  Итоговый баланс: {monitorBalance:C}");
        Console.WriteLine($"  Корректность: {(monitorCorrect ? "Да" : "Нет")}");
        double monitorOverhead = noSyncMs > 0 ? (monitorMs - noSyncMs) / noSyncMs * 100 : 0;
        Console.WriteLine($"  Накладные расходы: {monitorOverhead:F2}%");

        Console.WriteLine("\nСравнение производительности:");
        double speedup = lockMs / monitorMs;
        Console.WriteLine($"  Ускорение lock vs Monitor: {speedup:F2}x");
        Console.WriteLine($"  Накладные расходы lock: {lockOverhead:F2}%");
        Console.WriteLine($"  Накладные расходы Monitor: {monitorOverhead:F2}%");
    }
}
