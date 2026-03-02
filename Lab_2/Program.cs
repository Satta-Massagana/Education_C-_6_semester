using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

class Program
{
    static void Main()
    {
        const int dataSize = 10_000_000;
        var random = new Random(42);
        var data = new decimal[dataSize];

        for (int i = 0; i < dataSize; i++)
        {
            data[i] = (decimal)(1.0 + random.NextDouble() * 999.0);
        }

        var processor = new TaskProcessor();

        // Последовательная обработка
        var swSeq = Stopwatch.StartNew();
        var seqResult = SequentialProcess(data);
        swSeq.Stop();

        // ThreadPool
        AsyncLogger.LogAsync("Начало ThreadPool обработки").Wait();
        var swTp = Stopwatch.StartNew();
        var tpResult = processor.ProcessDataWithThreadPool(data);
        swTp.Stop();

        // TAP
        AsyncLogger.LogAsync("Начало TAP обработки").Wait();
        var swTap = Stopwatch.StartNew();
        var tapResult = processor.ProcessDataAsync(data).Result;
        swTap.Stop();

        // APM
        AsyncLogger.LogAsync("Начало APM обработки").Wait();
        var swApm = Stopwatch.StartNew();
        var apmResult = processor.ProcessDataWithAPM(data);
        swApm.Stop();

        var matchTp = seqResult.SequenceEqual(tpResult);
        var matchTap = seqResult.SequenceEqual(tapResult);
        var matchApm = seqResult.SequenceEqual(apmResult);
        var allMatch = matchTp && matchTap && matchApm;

        Console.WriteLine($"Размер данных: {dataSize:N0} элементов");
        Console.WriteLine($"\nПоследовательная: {swSeq.ElapsedMilliseconds} мс");
        Console.WriteLine($"ThreadPool: {swTp.ElapsedMilliseconds} мс");
        Console.WriteLine($"TAP: {swTap.ElapsedMilliseconds} мс");
        Console.WriteLine($"APM: {swApm.ElapsedMilliseconds} мс");
        Console.WriteLine(
            $"\nУскорение ThreadPool: {swSeq.ElapsedMilliseconds / (double)swTp.ElapsedMilliseconds:F2}x"
        );
        Console.WriteLine(
            $"Ускорение TAP: {swSeq.ElapsedMilliseconds / (double)swTap.ElapsedMilliseconds:F2}x"
        );
        Console.WriteLine(
            $"Ускорение APM: {swSeq.ElapsedMilliseconds / (double)swApm.ElapsedMilliseconds:F2}x"
        );
        Console.WriteLine($"Результаты совпадают: {(allMatch ? "Да" : "Нет")}\n");

        AsyncLogger.LogAsync("Обработка завершена").Wait();
    }

    static decimal[] SequentialProcess(decimal[] data)
    {
        var result = new decimal[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            result[i] = data[i] * 2m + 1m;
        }
        return result;
    }
}
