using System;
using System.Collections.Generic;
using System.Diagnostics;

public class SynchronizationBenchmark
{
    public (TimeSpan elapsed, decimal balance) BenchmarkNoSync(
        BankAccount account,
        List<decimal> transactions
    )
    {
        var processor = new TransactionProcessor();
        var sw = Stopwatch.StartNew();
        decimal result = processor.ProcessTransactionsConcurrently(account, transactions);
        sw.Stop();
        return (sw.Elapsed, result);
    }

    public (TimeSpan elapsed, decimal balance) BenchmarkWithLock(
        BankAccount account,
        List<decimal> transactions
    )
    {
        var processor = new TransactionProcessor();
        var sw = Stopwatch.StartNew();
        decimal result = processor.ProcessTransactionsWithLock(account, transactions);
        sw.Stop();
        return (sw.Elapsed, result);
    }

    public (TimeSpan elapsed, decimal balance) BenchmarkWithMonitor(
        BankAccount account,
        List<decimal> transactions
    )
    {
        var processor = new TransactionProcessor();
        var sw = Stopwatch.StartNew();
        decimal result = processor.ProcessTransactionsWithMonitor(account, transactions);
        sw.Stop();
        return (sw.Elapsed, result);
    }

    public void CompareAllApproaches(
        BankAccount accountNoSync,
        BankAccount accountLock,
        BankAccount accountMonitor,
        List<decimal> transactions,
        decimal expectedBalance
    )
    {
        var (noSyncTime, noSyncBalance) = BenchmarkNoSync(accountNoSync, transactions);
        bool noSyncCorrect = Math.Abs(noSyncBalance - expectedBalance) < 0.001m;
        double noSyncMs = noSyncTime.TotalMilliseconds;

        var (lockTime, lockBalance) = BenchmarkWithLock(accountLock, transactions);
        bool lockCorrect = Math.Abs(lockBalance - expectedBalance) < 0.001m;
        double lockMs = lockTime.TotalMilliseconds;

        var (monitorTime, monitorBalance) = BenchmarkWithMonitor(accountMonitor, transactions);
        bool monitorCorrect = Math.Abs(monitorBalance - expectedBalance) < 0.001m;
        double monitorMs = monitorTime.TotalMilliseconds;

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
