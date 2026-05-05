using System;
using System.Collections.Generic;
using System.Threading;

public class TransactionProcessor
{
    // Без синхронизации (используем пул потоков)
    public decimal ProcessTransactionsConcurrently(BankAccount account, List<decimal> transactions)
    {
        var done = new CountdownEvent(transactions.Count);
        foreach (var amount in transactions)
        {
            var localAmount = amount;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (localAmount > 0)
                    account.Deposit(localAmount);
                else
                    account.Withdraw(-localAmount);
                done.Signal();
            });
        }
        done.Wait();
        return account.Balance;
    }

    // С lock (используем пул потоков)
    public decimal ProcessTransactionsWithLock(BankAccount account, List<decimal> transactions)
    {
        var done = new CountdownEvent(transactions.Count);
        foreach (var amount in transactions)
        {
            var localAmount = amount;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (localAmount > 0)
                    account.DepositWithLock(localAmount);
                else
                    account.WithdrawWithLock(-localAmount);
                done.Signal();
            });
        }
        done.Wait();
        return account.Balance;
    }

    // С Monitor (используем пул потоков)
    public decimal ProcessTransactionsWithMonitor(BankAccount account, List<decimal> transactions)
    {
        var done = new CountdownEvent(transactions.Count);
        foreach (var amount in transactions)
        {
            var localAmount = amount;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (localAmount > 0)
                    account.DepositWithMonitor(localAmount);
                else
                    account.WithdrawWithMonitor(-localAmount);
                done.Signal();
            });
        }
        done.Wait();
        return account.Balance;
    }

    // Демонстрация deadlock и безопасных переводов (увеличено количество операций)
    public void ProcessConcurrentTransfers(List<BankAccount> accounts, int transferCount)
    {
        Console.WriteLine("\n=== Демонстрация deadlock ===");
        var accountA = new BankAccount(1, 1000);
        var accountB = new BankAccount(2, 1000);

        // Запускаем два потока, каждый выполняет множество опасных переводов
        var t1 = new Thread(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                accountA.TransferUnsafe(accountB, 10, false);
            }
        });
        var t2 = new Thread(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                accountB.TransferUnsafe(accountA, 10, true);
            }
        });

        t1.Start();
        t2.Start();

        if (!t1.Join(5000) || !t2.Join(5000))
            Console.WriteLine("Обнаружен deadlock! Потоки не завершились за 5 секунд.");
        else
            Console.WriteLine("Deadlock не произошёл (это маловероятно).");

        Console.WriteLine("\n=== Безопасные переводы с упорядочиванием блокировок ===");
        var safeAccounts = new List<BankAccount>();
        for (int i = 0; i < 10; i++)
            safeAccounts.Add(new BankAccount(i, 10000));

        var done = new CountdownEvent(transferCount);
        var rand = new Random(42);
        for (int i = 0; i < transferCount; i++)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                int fromIdx = rand.Next(safeAccounts.Count);
                int toIdx = rand.Next(safeAccounts.Count);
                if (fromIdx == toIdx)
                {
                    done.Signal();
                    return;
                }
                decimal amount = (decimal)(rand.NextDouble() * 100 + 1);
                safeAccounts[fromIdx].TransferSafe(safeAccounts[toIdx], amount);
                done.Signal();
            });
        }
        done.Wait();

        decimal totalBalance = 0;
        foreach (var acc in safeAccounts)
            totalBalance += acc.Balance;
        Console.WriteLine(
            $"Всего счетов: {safeAccounts.Count}, суммарный баланс: {totalBalance} (ожидалось: 100000)"
        );
    }
}
