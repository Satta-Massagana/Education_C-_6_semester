using System.Collections.Concurrent;

namespace Lab5.ConcurrentCollections;

public sealed class TaskItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Description { get; init; }
    public required Action Action { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

public sealed class TaskQueueManager
{
    public BlockingCollection<TaskItem> TaskQueue { get; }

    public TaskQueueManager(int boundedCapacity)
    {
        if (boundedCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(boundedCapacity),
                "Queue capacity must be positive."
            );
        }

        TaskQueue = new BlockingCollection<TaskItem>(boundedCapacity);
    }

    public bool AddTask(string description, Action taskAction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(taskAction);

        if (TaskQueue.IsAddingCompleted)
        {
            return false;
        }

        TaskItem task = new() { Description = description, Action = taskAction };

        try
        {
            TaskQueue.Add(task);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public async Task<int> ProcessTasks(
        int workerCount,
        CancellationToken cancellationToken = default
    )
    {
        if (workerCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(workerCount),
                "Worker count must be positive."
            );
        }

        int processedTasks = 0;
        List<Task> workers = new(workerCount);

        for (int i = 0; i < workerCount; i++)
        {
            workers.Add(
                Task.Run(
                    () =>
                    {
                        foreach (
                            TaskItem taskItem in TaskQueue.GetConsumingEnumerable(cancellationToken)
                        )
                        {
                            try
                            {
                                taskItem.Action();
                                Interlocked.Increment(ref processedTasks);
                            }
                            catch
                            {
                                // Task failures are intentionally ignored for benchmark-focused processing.
                            }
                        }
                    },
                    cancellationToken
                )
            );
        }

        try
        {
            await Task.WhenAll(workers);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is expected when token is requested.
        }

        return processedTasks;
    }

    public void CompleteAdding()
    {
        TaskQueue.CompleteAdding();
    }

    public int GetPendingTaskCount()
    {
        return TaskQueue.Count;
    }
}
