using System;
using System.Threading;

namespace Lab4;

public sealed class ResourcePool : IDisposable
{
    private readonly SemaphoreSlim _semaphore;
    private readonly int _maxResources;

    public ResourcePool(int maxResources)
    {
        if (maxResources <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxResources),
                "Максимум ресурсов должен быть больше 0."
            );
        }

        _maxResources = maxResources;
        _semaphore = new SemaphoreSlim(maxResources, maxResources);
    }

    public int AvailableCount => _semaphore.CurrentCount;

    public void AcquireResource()
    {
        _semaphore.Wait();
    }

    public bool TryAcquireResource(int timeoutMs)
    {
        return _semaphore.Wait(timeoutMs);
    }

    public void ReleaseResource()
    {
        try
        {
            _semaphore.Release();
        }
        catch (SemaphoreFullException ex)
        {
            throw new InvalidOperationException(
                $"Освобождено больше ресурсов, чем выделено. Максимум: {_maxResources}.",
                ex
            );
        }
    }

    public void Dispose()
    {
        _semaphore.Dispose();
    }
}
