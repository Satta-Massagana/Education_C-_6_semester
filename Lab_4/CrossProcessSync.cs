using System;
using System.Threading;

namespace Lab4;

public static class CrossProcessSync
{
    public static void ExecuteWithGlobalLock(string mutexName, Action action)
    {
        if (string.IsNullOrWhiteSpace(mutexName))
        {
            throw new ArgumentException("Имя мьютекса не может быть пустым.", nameof(mutexName));
        }

        if (action is null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        using Mutex mutex = new Mutex(false, mutexName, out _);
        bool lockTaken = false;

        try
        {
            lockTaken = mutex.WaitOne();
            action();
        }
        catch (AbandonedMutexException)
        {
            // Если предыдущий владелец завершился аварийно, считаем мьютекс захваченным.
            lockTaken = true;
            action();
        }
        finally
        {
            if (lockTaken)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    public static bool TryExecuteWithGlobalLock(string mutexName, Action action, int timeoutMs)
    {
        if (string.IsNullOrWhiteSpace(mutexName))
        {
            throw new ArgumentException("Имя мьютекса не может быть пустым.", nameof(mutexName));
        }

        if (action is null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        using Mutex mutex = new Mutex(false, mutexName, out _);
        bool lockTaken = false;

        try
        {
            lockTaken = mutex.WaitOne(timeoutMs);
            if (!lockTaken)
            {
                return false;
            }

            action();
            return true;
        }
        catch (AbandonedMutexException)
        {
            lockTaken = true;
            action();
            return true;
        }
        finally
        {
            if (lockTaken)
            {
                mutex.ReleaseMutex();
            }
        }
    }
}
