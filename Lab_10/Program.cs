namespace Lab10;

internal static class Program
{
    private const string Host = "127.0.0.1";
    private const int BenchmarkParallelClients = 8;
    private const int BenchmarkMessages = 50000;
    private const int BenchmarkMessagesPerClientSameTotal =
        BenchmarkMessages / BenchmarkParallelClients;
    private const int BenchmarkMessageSize = 1024;
    private const int BenchmarkThroughputMessageSize = 10 * 1024;
    private const int BenchmarkThroughputDurationSeconds = 3;
    private const int BenchmarkLatencyIterations = 100;

    private static async Task Main()
    {
        using var benchmark = new NetworkBenchmark(Host);

        var test1 = await benchmark
            .BenchmarkSingleClientAsync(BenchmarkMessages, BenchmarkMessageSize)
            .ConfigureAwait(false);
        var test2 = await benchmark
            .BenchmarkMultipleClientsAsync(
                BenchmarkParallelClients,
                BenchmarkMessages,
                BenchmarkMessageSize
            )
            .ConfigureAwait(false);
        var test3 = await benchmark
            .BenchmarkMultipleClientsAsync(
                BenchmarkParallelClients,
                BenchmarkMessagesPerClientSameTotal,
                BenchmarkMessageSize
            )
            .ConfigureAwait(false);
        var test4 = await benchmark
            .BenchmarkThroughputAsync(
                BenchmarkThroughputDurationSeconds,
                BenchmarkThroughputMessageSize
            )
            .ConfigureAwait(false);
        var latency = await benchmark
            .CompareLatencyAsync(BenchmarkLatencyIterations)
            .ConfigureAwait(false);

        var reliability =
            test2.SentMessages == 0 ? 100.0 : test2.ReceivedMessages * 100.0 / test2.SentMessages;

        PrintSummary(latency, reliability, test1, test2, test3, test4);
    }

    private static void PrintSummary(
        (double AverageMs, double MinMs, double MaxMs) latency,
        double deliveryPercent,
        BenchmarkResult test1,
        BenchmarkResult test2,
        BenchmarkResult test3,
        BenchmarkResult test4
    )
    {
        Console.WriteLine();
        Console.WriteLine("=== Результаты тестирования сетевого обмена ===");
        Console.WriteLine();
        Console.WriteLine("Тесты:");
        PrintTestResult("Тест 1 (один клиент)", test1);
        PrintTestResult(
            "Тест 2 (множество клиентов — одинаковое число сообщений на клиента)",
            test2,
            showPerClientTime: true
        );
        PrintTestResult("Тест 3 (множество клиентов — одинаковое общее число сообщений)", test3);
        PrintTestResult("Тест 4 (пропускная способность)", test4);
        Console.WriteLine();
        Console.WriteLine("Сравнение параллельной передачи с тестом 1:");
        PrintParallelSpeedup(
            "Тест 2",
            test1,
            test2,
            $"{BenchmarkParallelClients} клиентов × {BenchmarkMessages} сообщений",
            usePerClientTime: true
        );
        PrintParallelSpeedup(
            "Тест 3",
            test1,
            test3,
            $"{BenchmarkParallelClients} клиентов × {BenchmarkMessagesPerClientSameTotal} сообщений "
                + $"(≈{BenchmarkMessages} всего)"
        );
        Console.WriteLine();
        Console.WriteLine("Сравнение передачи с тестом 1:");
        PrintSpeedComparison(
            "Тест 4",
            test1,
            test4,
            $"payload {BenchmarkMessageSize / 1024} КБ vs {BenchmarkThroughputMessageSize / 1024} КБ"
        );
        Console.WriteLine();
        Console.WriteLine("Производительность:");
        Console.WriteLine($"  Пропускная способность: {test4.SpeedMbitPerSec:F2} Мбит/с");
        Console.WriteLine($"  Средняя задержка: {latency.AverageMs:F2} мс");
        Console.WriteLine($"  Минимальная задержка: {latency.MinMs:F2} мс");
        Console.WriteLine($"  Максимальная задержка: {latency.MaxMs:F2} мс");
        Console.WriteLine($"  Надежность доставки: {deliveryPercent:F2}%");
    }

    private static void PrintSpeedComparison(
        string testName,
        BenchmarkResult baseline,
        BenchmarkResult other,
        string description
    )
    {
        var ratio = other.SpeedMbitPerSec / baseline.SpeedMbitPerSec;

        if (ratio >= 1)
        {
            Console.WriteLine(
                $"  {testName} ({description}): быстрее в {ratio:F2} раз по скорости"
            );
        }
        else
        {
            Console.WriteLine(
                $"  {testName} ({description}): медленнее в {(1 / ratio):F2} раз по скорости"
            );
        }
    }

    private static void PrintParallelSpeedup(
        string testName,
        BenchmarkResult baseline,
        BenchmarkResult parallel,
        string description,
        bool usePerClientTime = false
    )
    {
        var parallelSeconds = parallel.Elapsed.TotalSeconds;
        if (usePerClientTime && parallel.ClientCount > 0)
            parallelSeconds /= parallel.ClientCount;

        var ratio = baseline.Elapsed.TotalSeconds / parallelSeconds;

        if (ratio >= 1)
        {
            Console.WriteLine($"  {testName} ({description}): быстрее в {ratio:F2} раз");
        }
        else
        {
            Console.WriteLine($"  {testName} ({description}): медленнее в {(1 / ratio):F2} раз");
        }
    }

    private static void PrintTestResult(
        string title,
        BenchmarkResult result,
        bool showPerClientTime = false
    )
    {
        Console.WriteLine($"  {title}:");
        Console.WriteLine($"    Клиентов: {result.ClientCount}");
        Console.WriteLine(
            $"    Сообщений: {result.SentMessages} отправлено, {result.ReceivedMessages} получено"
        );
        Console.WriteLine($"    Полезная нагрузка: {result.SentMegabytes:F2} МБ");
        Console.WriteLine(
            $"    Передано (NetworkMessage overhead): {result.TransmittedMegabytes:F2} МБ"
        );
        Console.WriteLine(
            $"    Передано (TCP/IP overhead): {result.TransmittedWithTcpIpMegabytes:F2} МБ"
        );
        Console.WriteLine($"    Скорость сервера: {result.SpeedMbitPerSec:F2} Мбит/с");

        if (showPerClientTime && result.ClientCount > 1)
        {
            Console.WriteLine($"    Время (общее): {result.Elapsed.TotalSeconds:F2} сек");
            Console.WriteLine(
                $"    Время (на одного клиента): {result.Elapsed.TotalSeconds / result.ClientCount:F2} сек"
            );
        }
        else
        {
            Console.WriteLine($"    Время: {result.Elapsed.TotalSeconds:F2} сек");
        }
    }
}
