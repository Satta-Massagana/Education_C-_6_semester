using System.Diagnostics;

namespace Lab_11;

// Набор простых измерений для MPI-подобных операций.
public sealed class MpiBenchmark
{
    private readonly IReadOnlyList<MpiCommunicator> _communicators;

    public MpiBenchmark(IReadOnlyList<MpiCommunicator> communicators)
    {
        if (communicators.Count < 2)
        {
            throw new ArgumentException(
                "Для бенчмарка нужно минимум два узла.",
                nameof(communicators)
            );
        }

        _communicators = communicators;
    }

    public void WarmUp(int iterations, int messageSize, int gatherDataSize)
    {
        if (iterations <= 0)
        {
            return;
        }

        var payload = CreatePayload(messageSize);
        // Для Gather создаем такие же массивы, как в реальном замере, чтобы прогреть сериализацию похожих объектов.
        var localArrays = Enumerable
            .Range(0, _communicators.Count)
            .Select(rank => Enumerable.Repeat(rank, gatherDataSize).ToArray())
            .ToArray();

        for (var i = 0; i < iterations; i++)
        {
            // Прогрев не измеряется: он нужен, чтобы JIT, ThreadPool и TCP read-loop не попадали в основные замеры.
            var receiver = Task.Run(() => _communicators[1].Receive(0));
            _communicators[0].Send(1, payload);
            receiver.GetAwaiter().GetResult();

            RunOnAll(rank => _communicators[rank].Broadcast(rank == 0 ? payload : null, root: 0));
            RunOnAll(rank => _communicators[rank].Gather(localArrays[rank], root: 0));
            RunReduce((left, right) => Convert.ToInt32(left) + Convert.ToInt32(right));
            RunOnAll(rank => CollectiveOperations.ImplementBarrier(_communicators[rank]));
        }
    }

    public (double averageTime, double minTime, double maxTime) BenchmarkPointToPoint(
        int iterations,
        int messageSize
    )
    {
        var timings = new List<double>(iterations);
        var payload = CreatePayload(messageSize);

        for (var i = 0; i < iterations; i++)
        {
            // Receive запускается до Send, чтобы сообщение не потерялось и замер включал полный Send/Receive.
            var receiver = Task.Run(() => _communicators[1].Receive(0));
            var watch = Stopwatch.StartNew();

            _communicators[0].Send(1, payload);
            receiver.GetAwaiter().GetResult();

            watch.Stop();
            timings.Add(watch.Elapsed.TotalMilliseconds);
        }

        return (timings.Average(), timings.Min(), timings.Max());
    }

    public (double executionTime, int messageCount) BenchmarkBroadcast(
        int iterations,
        int messageSize
    )
    {
        // Короткая версия нужна основному выводу: там достаточно среднего времени и числа сообщений.
        var result = BenchmarkBroadcastDetailed(iterations, messageSize);
        return (result.timing.Average, result.messageCount);
    }

    public (BenchmarkTiming timing, int messageCount) BenchmarkBroadcastDetailed(
        int iterations,
        int messageSize
    )
    {
        var payload = CreatePayload(messageSize);
        var timings = new List<double>(iterations);

        for (var i = 0; i < iterations; i++)
        {
            var watch = Stopwatch.StartNew();

            // Все узлы должны вызвать Broadcast: root отправляет, остальные ждут и пересылают дальше.
            RunOnAll(rank => _communicators[rank].Broadcast(rank == 0 ? payload : null, root: 0));

            watch.Stop();
            timings.Add(watch.Elapsed.TotalMilliseconds);
        }

        // Для бинарного дерева нужно N - 1 пересылок, потому что каждый узел получает сообщение один раз.
        return (CalculateTiming(timings), _communicators.Count - 1);
    }

    public (double executionTime, long dataVolume) BenchmarkGather(int iterations, int dataSize)
    {
        // Короткая версия возвращает среднее время и полезный объем собранных данных.
        var result = BenchmarkGatherDetailed(iterations, dataSize);
        return (result.timing.Average, result.dataVolume);
    }

    public (BenchmarkTiming timing, long dataVolume) BenchmarkGatherDetailed(
        int iterations,
        int dataSize
    )
    {
        var timings = new List<double>(iterations);
        // Каждый узел отдает массив int[dataSize]; root в итоге получает по одному набору от каждого узла.
        var localArrays = Enumerable
            .Range(0, _communicators.Count)
            .Select(rank => Enumerable.Repeat(rank, dataSize).ToArray())
            .ToArray();

        for (var i = 0; i < iterations; i++)
        {
            var watch = Stopwatch.StartNew();

            // Gather вызывается на всех узлах одновременно, иначе дерево сбора будет ждать отсутствующих участников.
            RunOnAll(rank => _communicators[rank].Gather(localArrays[rank], root: 0));

            watch.Stop();
            timings.Add(watch.Elapsed.TotalMilliseconds);
        }

        var bytesPerOperation = (long)dataSize * sizeof(int) * _communicators.Count;
        return (CalculateTiming(timings), bytesPerOperation);
    }

    public (double executionTime, ReductionBenchmarkResult reductionResult) BenchmarkReduce(
        int iterations
    )
    {
        // Короткая версия нужна основному выводу; detailed-версия используется в масштабируемости.
        var result = BenchmarkReduceDetailed(iterations);
        return (result.timing.Average, result.reductionResult);
    }

    public (
        BenchmarkTiming timing,
        ReductionBenchmarkResult reductionResult
    ) BenchmarkReduceDetailed(int iterations)
    {
        var timings = new List<double>(iterations);
        var lastResult = new ReductionBenchmarkResult(0, 0, 0);

        for (var i = 0; i < iterations; i++)
        {
            var watch = Stopwatch.StartNew();

            // За один повтор проверяем три типовые редукции, поэтому в Program.cs время одной редукции делится на 3.
            var sum = RunReduce((left, right) => Convert.ToInt32(left) + Convert.ToInt32(right));
            var max = RunReduce(
                (left, right) => Math.Max(Convert.ToInt32(left), Convert.ToInt32(right))
            );
            var min = RunReduce(
                (left, right) => Math.Min(Convert.ToInt32(left), Convert.ToInt32(right))
            );

            watch.Stop();
            timings.Add(watch.Elapsed.TotalMilliseconds);
            lastResult = new ReductionBenchmarkResult(
                Convert.ToInt32(sum),
                Convert.ToInt32(max),
                Convert.ToInt32(min)
            );
        }

        return (CalculateTiming(timings), lastResult);
    }

    public double BenchmarkBarrier(int iterations)
    {
        // Для обычного вывода Barrier достаточно среднего времени.
        return BenchmarkBarrierDetailed(iterations).Average;
    }

    public BenchmarkTiming BenchmarkBarrierDetailed(int iterations)
    {
        var timings = new List<double>(iterations);

        for (var i = 0; i < iterations; i++)
        {
            var watch = Stopwatch.StartNew();

            // Barrier не передает пользовательские данные, а синхронизирует момент продолжения всех узлов.
            RunOnAll(rank => CollectiveOperations.ImplementBarrier(_communicators[rank]));

            watch.Stop();
            timings.Add(watch.Elapsed.TotalMilliseconds);
        }

        return CalculateTiming(timings);
    }

    private void RunOnAll(Action<int> operation)
    {
        // Коллективные операции должны стартовать на всех узлах, поэтому каждый rank запускается отдельной задачей.
        var tasks = Enumerable
            .Range(0, _communicators.Count)
            .Select(rank => Task.Run(() => operation(rank)))
            .ToArray();

        Task.WaitAll(tasks);
    }

    private object? RunReduce(Func<object, object, object> operation)
    {
        var results = new object?[_communicators.Count];

        // Только root возвращает результат Reduce; остальные узлы возвращают null.
        RunOnAll(rank =>
        {
            results[rank] = _communicators[rank]
                .Reduce(localValue: rank + 1, operation: operation, root: 0);
        });

        return results[0];
    }

    private static byte[] CreatePayload(int messageSize)
    {
        var payload = new byte[messageSize];

        // Заполняем массив повторяемым шаблоном, чтобы payload был не пустым, но быстро создавался.
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 251);
        }

        return payload;
    }

    private static BenchmarkTiming CalculateTiming(IReadOnlyList<double> timings)
    {
        if (timings.Count == 0)
        {
            return new BenchmarkTiming(0, 0, 0, 0);
        }

        // Медиана полезна для локальных запусков, где иногда появляются случайные выбросы по времени.
        var sorted = timings.OrderBy(value => value).ToArray();
        var middle = sorted.Length / 2;
        var median =
            sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];

        return new BenchmarkTiming(timings.Average(), sorted[0], sorted[^1], median);
    }
}

// Статистика времени выполнения операции по нескольким повторам.
public sealed record BenchmarkTiming(double Average, double Min, double Max, double Median)
{
    public BenchmarkTiming Divide(double divisor)
    {
        // Используется для Reduce: один замер включает sum, max и min, а в выводе нужно время одной редукции.
        return new BenchmarkTiming(
            Average / divisor,
            Min / divisor,
            Max / divisor,
            Median / divisor
        );
    }
}

// Результаты трех типовых редукций: сумма, максимум и минимум.
public sealed record ReductionBenchmarkResult(int Sum, int Max, int Min);
