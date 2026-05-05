using System;

public class BankAccount
{
    private decimal _balance;
    private readonly object _lockObj = new object();

    public int Id { get; }

    public BankAccount(int id, decimal initialBalance = 0)
    {
        Id = id;
        _balance = initialBalance;
    }

    public decimal Balance => _balance;

    // --- Без синхронизации (с задержкой для увеличения вероятности гонки) ---
    public void Deposit(decimal amount)
    {
        decimal old = _balance;
        // Искусственная задержка для расширения окна гонки
        Thread.SpinWait(1000);
        _balance = old + amount;
    }

    public void Withdraw(decimal amount)
    {
        decimal old = _balance;
        Thread.SpinWait(1000);
        _balance = old - amount;
    }

    public void Transfer(BankAccount target, decimal amount)
    {
        Withdraw(amount);
        target.Deposit(amount);
    }

    // --- С lock ---
    public void DepositWithLock(decimal amount)
    {
        lock (_lockObj)
            _balance += amount;
    }

    public void WithdrawWithLock(decimal amount)
    {
        lock (_lockObj)
            _balance -= amount;
    }

    public void TransferWithLock(BankAccount target, decimal amount)
    {
        lock (_lockObj)
        {
            lock (target._lockObj)
            {
                _balance -= amount;
                target._balance += amount;
            }
        }
    }

    // --- С Monitor ---
    public void DepositWithMonitor(decimal amount)
    {
        Monitor.Enter(_lockObj);
        try
        {
            _balance += amount;
        }
        finally
        {
            Monitor.Exit(_lockObj);
        }
    }

    public void WithdrawWithMonitor(decimal amount)
    {
        Monitor.Enter(_lockObj);
        try
        {
            _balance -= amount;
        }
        finally
        {
            Monitor.Exit(_lockObj);
        }
    }

    public void TransferWithMonitor(BankAccount target, decimal amount)
    {
        Monitor.Enter(_lockObj);
        try
        {
            Monitor.Enter(target._lockObj);
            try
            {
                _balance -= amount;
                target._balance += amount;
            }
            finally
            {
                Monitor.Exit(target._lockObj);
            }
        }
        finally
        {
            Monitor.Exit(_lockObj);
        }
    }

    // --- С таймаутом ---
    public bool DepositWithTimeout(decimal amount, int timeoutMs)
    {
        if (Monitor.TryEnter(_lockObj, timeoutMs))
        {
            try
            {
                _balance += amount;
                return true;
            }
            finally
            {
                Monitor.Exit(_lockObj);
            }
        }
        return false;
    }

    public bool WithdrawWithTimeout(decimal amount, int timeoutMs)
    {
        if (Monitor.TryEnter(_lockObj, timeoutMs))
        {
            try
            {
                _balance -= amount;
                return true;
            }
            finally
            {
                Monitor.Exit(_lockObj);
            }
        }
        return false;
    }

    // --- Безопасный перевод с единообразным порядком захвата (по Id) ---
    public void TransferSafe(BankAccount target, decimal amount)
    {
        var firstLock = Id < target.Id ? _lockObj : target._lockObj;
        var secondLock = Id < target.Id ? target._lockObj : _lockObj;

        lock (firstLock)
        {
            lock (secondLock)
            {
                _balance -= amount;
                target._balance += amount;
            }
        }
    }

    // --- Опасный перевод с возможным deadlock (с задержкой для гарантии) ---
    public void TransferUnsafe(BankAccount target, decimal amount, bool reverseOrder)
    {
        if (!reverseOrder)
        {
            lock (_lockObj)
            {
                // Задержка после захвата первой блокировки
                Thread.Sleep(10);
                lock (target._lockObj)
                {
                    _balance -= amount;
                    target._balance += amount;
                }
            }
        }
        else
        {
            lock (target._lockObj)
            {
                Thread.Sleep(10);
                lock (_lockObj)
                {
                    _balance -= amount;
                    target._balance += amount;
                }
            }
        }
    }
}
