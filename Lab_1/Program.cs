using System;
using System.Diagnostics;
using System.Linq;

class Program
{
    static void Main()
    {
        // Генерация данных и заполнение массива
        const int dataSize = 10_000_000;
        const int threadCount = 4;
        var random = new Random(42);
        var data = new decimal[dataSize];

        for (int i = 0; i < dataSize; i++)
        {
            data[i] = (decimal)(1.0 + random.NextDouble() * 999.0);
        }

        // Console.WriteLine("Первые 5 элементов data:");
        // for (int i = 0; i < Math.Min(5, data.Length); i++)
        // {
        //     Console.WriteLine($"data[{i}] = {data[i]:F2}");
        // }
        // Console.WriteLine();

        // Вызов обработки данных и подсчёта результатов
        var (seqTime, seqResult) = PerformanceMeter.MeasureExecutionTime(
            () => DataProcessor.ProcessDataSequential(data),
            "Последовательная"
        );

        var (parTime, parResult) = PerformanceMeter.MeasureExecutionTime(
            () => DataProcessor.ProcessDataParallel(data, threadCount),
            $"Параллельная ({threadCount} потоков)"
        );

        bool resultsMatch = PerformanceMeter.CompareResults(seqResult, parResult);
        double speedup = (double)seqTime / parTime;

        Console.WriteLine($"Размер данных: {dataSize:N0} элементов");
        Console.WriteLine($"Последовательная обработка: {seqTime} мс");
        Console.WriteLine($"Параллельная обработка ({threadCount} потоков): {parTime} мс");
        Console.WriteLine($"Ускорение: {speedup:F2}x");
        Console.WriteLine($"Результаты совпадают: {(resultsMatch ? "Да" : "Нет")}");
    }
}
