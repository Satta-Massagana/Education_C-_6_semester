using System;
using System.Collections.Generic;

class Program
{
    static void Main(string[] args)
    {
        const int transactionCount = 1000000;
        var rand = new Random(42);
        var transactions = new List<decimal>();
        decimal expectedDelta = 0;
        for (int i = 0; i < transactionCount; i++)
        {
            decimal amount = (decimal)(rand.NextDouble() * 990 + 10);
            if (i % 2 == 0)
            {
                transactions.Add(amount);
                expectedDelta += amount;
            }
            else
            {
                transactions.Add(-amount);
                expectedDelta -= amount;
            }
        }

        decimal initialBalance = 100000m;
        decimal expectedBalance = initialBalance + expectedDelta;

        Console.WriteLine($"Количество транзакций: {transactionCount}");
        Console.WriteLine($"Ожидаемый итоговый баланс: {expectedBalance:C}\n");

        // Бенчмарк
        var benchmark = new SynchronizationBenchmark();
        var accountNoSync = new BankAccount(0, initialBalance);
        var accountLock = new BankAccount(0, initialBalance);
        var accountMonitor = new BankAccount(0, initialBalance);
        benchmark.CompareAllApproaches(
            accountNoSync,
            accountLock,
            accountMonitor,
            transactions,
            expectedBalance
        );

        // Тест переводов между счетами (deadlock и безопасные)
        var transferTest = new TransactionProcessor();
        var accounts = new List<BankAccount>();
        for (int i = 0; i < 10; i++)
            accounts.Add(new BankAccount(i, 10000));
        transferTest.ProcessConcurrentTransfers(accounts, 1000);
    }
}
