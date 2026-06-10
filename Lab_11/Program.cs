using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Lab_11;

public static class Program
{
    public static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        CultureInfo.CurrentCulture = new CultureInfo("ru-RU");

        const int nodeCount = 4;
        const int basePort = 9000;
        const int messageSize = 1024;
        const int benchmarkIterations = 20;
        const int gatherNumbersPerNode = 100;
        // Warm-up отдельно прогревает JIT, ThreadPool, TCP read-loop и сериализацию; в статистику он не входит.
        const int warmUpIterations = 3;

        // LocalCluster поднимает несколько MpiNode связь между ними по TCP.
        using var cluster = LocalCluster.Create(nodeCount, basePort);
        var benchmark = new MpiBenchmark(cluster.Communicators);

        Console.WriteLine("Конфигурация кластера:");
        Console.WriteLine($"  Количество узлов: {nodeCount}");
        Console.WriteLine($"  Порты: {string.Join(", ", cluster.Ports)}");
        Console.WriteLine();

        benchmark.WarmUp(warmUpIterations, messageSize, gatherNumbersPerNode);

        // Основные тесты возвращают средние значения по benchmarkIterations измерениям.
        var pointToPoint = benchmark.BenchmarkPointToPoint(benchmarkIterations, messageSize);
        var broadcast = benchmark.BenchmarkBroadcast(benchmarkIterations, messageSize);
        var gather = benchmark.BenchmarkGather(benchmarkIterations, gatherNumbersPerNode);
        var reduce = benchmark.BenchmarkReduce(benchmarkIterations);
        var barrier = benchmark.BenchmarkBarrier(benchmarkIterations);
        // Масштабируемость измеряется на отдельных кластерах 2/4/8/16 узлов.
        var scalability = MeasureScalability(
            messageSize,
            warmUpIterations,
            gatherNumbersPerNode,
            benchmarkIterations
        );
        var pointToPointMessages = 1;
        var broadcastMessages = broadcast.messageCount;
        var gatherSourceCount = nodeCount - 1;
        var gatherMessages = nodeCount - 1;
        var reduceOperationsPerIteration = 3;
        var reduceMessagesPerOperation = nodeCount - 1;
        // BenchmarkReduce за один повтор считает sum, max и min, поэтому время делим на 3 для одной редукции.
        var reduceSingleOperationTime = reduce.executionTime / reduceOperationsPerIteration;
        var barrierMessages = 2 * (nodeCount - 1);
        var pointToPointBytes = messageSize;
        var broadcastBytes = messageSize * (nodeCount - 1);
        // Для сравнения Broadcast нормализуется по числу получателей: полное время / количество доставок.
        var broadcastAverageTimePerReceiver = broadcast.executionTime / broadcastMessages;
        // Gather нормализуется по числу узлов-источников, которые отправляют данные к root.
        var gatherAverageTimePerSource = gather.executionTime / gatherSourceCount;
        var reduceBytesPerOperation = (nodeCount - 1) * sizeof(int);
        // Reduce нормализуется по числу сетевых сообщений дерева сбора.
        var reduceAverageTimePerSource = reduceSingleOperationTime / reduceMessagesPerOperation;

        Console.WriteLine("=== Результаты тестирования MPI-подобного обмена сообщениями ===");
        Console.WriteLine();

        PrintOperationProfile(
            "Точка-точка",
            "среднее время полного Send/Receive",
            pointToPoint.averageTime,
            pointToPointMessages,
            pointToPointBytes,
            "одно прикладное сообщение между двумя узлами",
            new[]
            {
                "Узел-источник: 0",
                "Узел-получатель: 1",
                $"Объем данных: {messageSize} байт",
                $"Минимальная задержка: {FormatMs(pointToPoint.minTime)}",
                $"Максимальная задержка: {FormatMs(pointToPoint.maxTime)}",
            }
        );
        PrintOperationProfile(
            "Broadcast",
            "среднее время полного Broadcast",
            broadcast.executionTime,
            broadcastMessages,
            broadcastBytes,
            "бинарное дерево, сообщения частично идут параллельно",
            new[]
            {
                "Узел-источник (root): 0",
                $"Объем одного сообщения: {messageSize} байт",
                $"Сообщений от root: {Math.Min(2, nodeCount - 1)}",
                $"Всего сообщений в кластере: {broadcastMessages}",
            },
            extraMetrics: new[]
            {
                $"Среднее время доставки одному узлу (Broadcast / {broadcastMessages}): {FormatMs(broadcastAverageTimePerReceiver)}",
            }
        );
        PrintOperationProfile(
            "Gather",
            "среднее время полного Gather",
            gather.executionTime,
            gatherMessages,
            gather.dataVolume,
            "дерево сбора к root, промежуточные узлы агрегируют данные",
            new[]
            {
                $"Узлы-источники: {string.Join(", ", Enumerable.Range(1, nodeCount - 1))}",
                "Узел-получатель (root): 0",
                $"Собрано наборов данных на root: {nodeCount}",
            },
            extraMetrics: new[]
            {
                $"Среднее время получения данных от одного узла (Gather / {gatherSourceCount}): {FormatMs(gatherAverageTimePerSource)}",
            }
        );
        PrintOperationProfile(
            "Reduce",
            "среднее время полного Reduce",
            reduceSingleOperationTime,
            reduceMessagesPerOperation,
            reduceBytesPerOperation,
            "дерево сбора к root, промежуточные узлы считают агрегированные данные",
            new[]
            {
                $"Узлы-участники: {string.Join(", ", Enumerable.Range(0, nodeCount))}",
                "Узел-получатель (root): 0",
                "Чисел на узел: 1 (значение rank + 1)",
                $"Результат редукции (сумма): {reduce.reductionResult.Sum}",
                $"Результат редукции (максимум): {reduce.reductionResult.Max}",
                $"Результат редукции (минимум): {reduce.reductionResult.Min}",
                $"Время выполнения трех редукций: {FormatMs(reduce.executionTime)}",
            },
            extraMetrics: new[]
            {
                $"Среднее время обработки данных от одного узла (Reduce / {reduceMessagesPerOperation}): {FormatMs(reduceAverageTimePerSource)}",
            }
        );
        PrintOperationProfile(
            "Barrier",
            "среднее время полного Barrier",
            barrier,
            barrierMessages,
            0,
            "служебная синхронизация без пользовательских данных",
            new[]
            {
                "Узел-координатор: 0",
                $"Узлы-участники: {string.Join(", ", Enumerable.Range(0, nodeCount))}",
                "Объем данных: служебные сообщения",
                $"Сообщений за операцию: {barrierMessages} ({nodeCount - 1} READY + {nodeCount - 1} GO)",
            }
        );
        PrintComparisons(
            pointToPoint.averageTime,
            broadcastAverageTimePerReceiver,
            gatherAverageTimePerSource,
            reduceAverageTimePerSource
        );
        PrintScalabilityProfile(scalability, messageSize);
    }

    private static List<ScalabilityMeasurement> MeasureScalability(
        int messageSize,
        int warmUpIterations,
        int gatherNumbersPerNode,
        int benchmarkIterations
    )
    {
        var result = new List<ScalabilityMeasurement>();
        var basePorts = new Dictionary<int, int>
        {
            [2] = 9100,
            [4] = 9200,
            [8] = 9300,
            [16] = 9400,
        };

        foreach (var size in new[] { 2, 4, 8, 16 })
        {
            // Для каждого размера создаем новый кластер на своем диапазоне портов.
            using var cluster = LocalCluster.Create(size, basePorts[size]);
            var benchmark = new MpiBenchmark(cluster.Communicators);
            benchmark.WarmUp(warmUpIterations, messageSize, gatherNumbersPerNode);

            // Detailed-версии возвращают average/min/max/median по всем повторам.
            var broadcast = benchmark.BenchmarkBroadcastDetailed(benchmarkIterations, messageSize);
            var gather = benchmark.BenchmarkGatherDetailed(
                benchmarkIterations,
                gatherNumbersPerNode
            );
            var reduce = benchmark.BenchmarkReduceDetailed(benchmarkIterations);
            var barrier = benchmark.BenchmarkBarrierDetailed(benchmarkIterations);

            result.Add(
                new ScalabilityMeasurement(
                    size,
                    broadcast.timing,
                    gather.timing,
                    // ReduceDetailed измеряет сразу три операции: sum, max, min. Для одной редукции делим статистику на 3.
                    reduce.timing.Divide(3),
                    barrier
                )
            );
        }

        return result;
    }

    private static string FormatMs(double value)
    {
        return $"{value:0.###} мс";
    }

    private static string FormatTiming(BenchmarkTiming timing)
    {
        // В масштабируемости выводим несколько статистик, потому что на localhost часто бывают выбросы.
        return $"average {FormatMs(timing.Average)}, min {FormatMs(timing.Min)}, max {FormatMs(timing.Max)}, median {FormatMs(timing.Median)}";
    }

    private static string FormatThroughput(long bytes, double milliseconds)
    {
        if (milliseconds <= 0)
        {
            return "н/д";
        }

        var megabytes = bytes / 1024d / 1024d;
        var seconds = milliseconds / 1000d;
        // Пропускная способность считается по полезному объему данных, а не по служебному JSON-протоколу.
        return $"{megabytes / seconds:0.###} МБ/с";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes == 0)
        {
            return "нет пользовательских данных";
        }

        if (bytes < 1024)
        {
            return $"{bytes} байт";
        }

        return $"{bytes / 1024d:0.###} КБ";
    }

    private static void PrintOperationProfile(
        string operationName,
        string averageTimeLabel,
        double operationTime,
        int messageCount,
        long payloadBytes,
        string note,
        IEnumerable<string> details,
        IEnumerable<string>? extraMetrics = null
    )
    {
        // Все операции печатаются одним методом, чтобы формат вывода был одинаковым.
        Console.WriteLine($"  {operationName}:");
        foreach (var detail in details)
        {
            Console.WriteLine($"    {detail}");
        }

        Console.WriteLine($"    {averageTimeLabel}: {FormatMs(operationTime)}");
        if (extraMetrics is not null)
        {
            foreach (var metric in extraMetrics)
            {
                Console.WriteLine($"    {metric}");
            }
        }

        Console.WriteLine($"    сообщений за операцию: {messageCount}");
        Console.WriteLine($"    полезный объем данных: {FormatBytes(payloadBytes)}");

        if (payloadBytes > 0)
        {
            Console.WriteLine(
                $"    пропускная способность: {FormatThroughput(payloadBytes, operationTime)}"
            );
        }

        Console.WriteLine($"    примечание: {note}");
        Console.WriteLine();
    }

    private static void PrintScalabilityProfile(
        IEnumerable<ScalabilityMeasurement> scalability,
        int messageSize
    )
    {
        var measurements = scalability.ToArray();

        Console.WriteLine("  Масштабируемость:");
        PrintScalabilityOperation("Broadcast", measurements, item => item.BroadcastTime);
        PrintScalabilityOperation("Gather", measurements, item => item.GatherTime);
        PrintScalabilityOperation("Reduce", measurements, item => item.ReduceTime);
        PrintScalabilityOperation("Barrier", measurements, item => item.BarrierTime);

        Console.WriteLine();
    }

    private static void PrintScalabilityOperation(
        string operationName,
        IEnumerable<ScalabilityMeasurement> measurements,
        Func<ScalabilityMeasurement, BenchmarkTiming> selectTiming
    )
    {
        Console.WriteLine($"    {operationName}:");
        foreach (var item in measurements)
        {
            Console.WriteLine(
                $"        {FormatNodeCount(item.NodeCount)}: {FormatTiming(selectTiming(item))}"
            );
        }

        Console.WriteLine();
    }

    private static string FormatNodeCount(int nodeCount)
    {
        return nodeCount switch
        {
            2 or 3 or 4 => $"{nodeCount} узла",
            _ => $"{nodeCount} узлов",
        };
    }

    private static void PrintComparisons(
        double pointToPointTime,
        double broadcastTimePerReceiver,
        double gatherTimePerSource,
        double reduceTimePerSource
    )
    {
        Console.WriteLine("  Сравнение:");
        // Сравниваем нормализованные значения: Broadcast/Gather/Reduce делятся на число получателей или источников.
        Console.WriteLine(
            $"    {CompareRelativeTime("Точка-точка", pointToPointTime, "Broadcast", broadcastTimePerReceiver)}"
        );
        Console.WriteLine(
            $"    {CompareRelativeTime("Точка-точка", pointToPointTime, "Gather", gatherTimePerSource)}"
        );
        Console.WriteLine(
            $"    {CompareRelativeTime("Точка-точка", pointToPointTime, "Reduce", reduceTimePerSource)}"
        );
        Console.WriteLine(
            $"    {CompareRelativeTime("Gather", gatherTimePerSource, "Reduce", reduceTimePerSource)}"
        );
        Console.WriteLine();
    }

    private static string CompareRelativeTime(
        string firstName,
        double firstTime,
        string secondName,
        double secondTime
    )
    {
        // Метод выбирает более медленную операцию и показывает коэффициент отличия.
        if (Math.Abs(firstTime - secondTime) < 0.0001)
        {
            return $"{firstName} и {secondName} показали примерно одинаковое время";
        }

        if (firstTime > secondTime)
        {
            return $"{firstName} медленнее {secondName} в {firstTime / secondTime:0.##} раз(а)";
        }

        return $"{secondName} медленнее {firstName} в {secondTime / firstTime:0.##} раз(а)";
    }

    private sealed record ScalabilityMeasurement(
        int NodeCount,
        BenchmarkTiming BroadcastTime,
        BenchmarkTiming GatherTime,
        BenchmarkTiming ReduceTime,
        BenchmarkTiming BarrierTime
    );

    // Локальная симуляция кластера: несколько MpiNode с TCP-подключениями.
    private sealed class LocalCluster : IDisposable
    {
        private readonly List<MpiNode> _nodes;

        public IReadOnlyList<MpiCommunicator> Communicators { get; }
        public IReadOnlyList<int> Ports { get; }

        private LocalCluster(
            List<MpiNode> nodes,
            List<MpiCommunicator> communicators,
            List<int> ports
        )
        {
            _nodes = nodes;
            Communicators = communicators;
            Ports = ports;
        }

        public static LocalCluster Create(int nodeCount, int basePort)
        {
            // Узлы получают последовательные порты: basePort, basePort + 1, ...
            var ports = Enumerable.Range(0, nodeCount).Select(offset => basePort + offset).ToList();
            var nodes = Enumerable
                .Range(0, nodeCount)
                .Select(rank => new MpiNode(rank, ports[rank]))
                .ToList();

            foreach (var node in nodes)
            {
                node.Start();
            }

            // Небольшая пауза дает TcpListener время начать принимать подключения.
            Thread.Sleep(150);

            for (var source = 0; source < nodeCount; source++)
            {
                for (var destination = source + 1; destination < nodeCount; destination++)
                {
                    // Достаточно подключаться только source -> destination: принимающая сторона тоже регистрирует соединение.
                    nodes[source].ConnectToNode(destination, "127.0.0.1", ports[destination]);
                }
            }

            WaitForFullMesh(nodes, expectedConnections: nodeCount - 1);

            var communicators = nodes
                .Select((node, rank) => new MpiCommunicator(rank, nodeCount).AttachNode(node))
                .ToList();

            return new LocalCluster(nodes, communicators, ports);
        }

        private static void WaitForFullMesh(IReadOnlyList<MpiNode> nodes, int expectedConnections)
        {
            var watch = Stopwatch.StartNew();

            // Ждем, пока каждый узел увидит подключения ко всем остальным узлам.
            while (watch.Elapsed < TimeSpan.FromSeconds(5))
            {
                if (nodes.All(node => node.ConnectionCount == expectedConnections))
                {
                    return;
                }

                Thread.Sleep(20);
            }

            var actual = string.Join(
                ", ",
                nodes.Select(node => $"node {node.NodeId}: {node.ConnectionCount}")
            );
            throw new TimeoutException(
                $"Не удалось собрать полную mesh-топологию. Подключения: {actual}"
            );
        }

        public void Dispose()
        {
            foreach (var node in _nodes)
            {
                node.Stop();
            }

            Thread.Sleep(100);
        }
    }
}
