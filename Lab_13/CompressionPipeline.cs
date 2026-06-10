using System.Collections.Concurrent;
using System.Threading.Tasks.Dataflow;

namespace Lab_13;

public sealed class CompressionPipeline
{
    private long acceptedItems;
    private long completedItems;

    public async Task<IReadOnlyList<WorkerCompressionResult>> RunAsync(
        IEnumerable<CompressionWorkItem> workItems,
        Func<CompressionWorkItem, CancellationToken, Task<WorkerCompressionResult>> processor,
        CancellationToken cancellationToken
    )
    {
        var results = new ConcurrentBag<WorkerCompressionResult>();

        var options = new ExecutionDataflowBlockOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            BoundedCapacity = Environment.ProcessorCount * 2,
        };

        var prepareBlock = new TransformBlock<CompressionWorkItem, CompressionWorkItem>(
            item =>
            {
                // Dataflow-стадия отделяет подготовку от сетевой отправки и ограничивает давление на память.
                Interlocked.Increment(ref acceptedItems);
                return item;
            },
            options
        );

        var processBlock = new TransformBlock<CompressionWorkItem, WorkerCompressionResult>(
            item => processor(item, cancellationToken),
            options
        );

        var collectBlock = new ActionBlock<WorkerCompressionResult>(
            result =>
            {
                results.Add(result);
                Interlocked.Increment(ref completedItems);
            },
            new ExecutionDataflowBlockOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = 1,
            }
        );

        var linkOptions = new DataflowLinkOptions { PropagateCompletion = true };
        prepareBlock.LinkTo(processBlock, linkOptions);
        processBlock.LinkTo(collectBlock, linkOptions);

        foreach (var item in workItems)
        {
            await prepareBlock.SendAsync(item, cancellationToken);
        }

        prepareBlock.Complete();
        await collectBlock.Completion;

        return results
            .OrderBy(result => result.PartIndex)
            .ThenBy(result => result.WorkerId)
            .ToArray();
    }

    public (long Accepted, long Completed) GetCounters()
    {
        return (Interlocked.Read(ref acceptedItems), Interlocked.Read(ref completedItems));
    }
}
