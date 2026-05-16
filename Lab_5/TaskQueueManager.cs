// Очередь задач на BlockingCollection — сценарий «производитель / потребитель».
// Один поток (или несколько) кладёт задачи, другие потоки забирают и выполняют.
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

        // Ограниченная ёмкость: при переполнении Add будет ждать, пока потребитель освободит место.
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
                        // GetConsumingEnumerable: блокируется, пока в очереди есть элементы;
                        // завершается после CompleteAdding(), когда очередь опустеет.
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
                                // Ошибки внутри задачи не останавливают остальных обработчиков.
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
            // Отмена по токену — штатный способ остановить обработчиков.
        }

        return processedTasks;
    }

    // После этого новые задачи добавить нельзя; потребители доработают оставшиеся.
    public void CompleteAdding()
    {
        TaskQueue.CompleteAdding();
    }

    public int GetPendingTaskCount()
    {
        return TaskQueue.Count;
    }
}
