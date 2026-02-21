using System;
using System.Threading;

public static class DataProcessor
{
    public static decimal[] ProcessDataSequential(decimal[] data)
    {
        var result = new decimal[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            result[i] =
                (decimal)Math.Sqrt((double)data[i]) * (decimal)Math.Log10((double)(data[i] + 1));
        }
        return result;
    }

    public static decimal[] ProcessDataParallel(decimal[] data, int threadCount)
    {
        if (threadCount < 1)
            threadCount = 1;

        // Пустой массив для вычеслений
        var result = new decimal[data.Length];

        // Массив с количеством потоков
        var threads = new Thread[threadCount];

        // Разделение массива по кускам между потоками
        var threadRanges = new (int start, int end)[threadCount];
        int chunkSize = data.Length / threadCount; // Например 10М / 4 потока = 2.5М на один поток

        // Создать каждый поток, и перебрать их части массива
        for (int t = 0; t < threadCount; t++)
        {
            int startIdx = t * chunkSize;
            // Считаем конец куска массива для потока через тернарный оператор - условие ? значение_если_true : значение_если_false
            int endIdx = (t == threadCount - 1) ? data.Length : startIdx + chunkSize;
            threadRanges[t] = (startIdx, endIdx);

            int threadIndex = t;

            // Создать поток, и перебрать его часть массива. Через лямбда функцию (чтобы не писать лишнюю функцию)
            threads[t] = new Thread(() =>
            {
                int localStart = threadRanges[threadIndex].start;
                int localEnd = threadRanges[threadIndex].end;

                try
                {
                    for (int i = localStart; i < localEnd; i++)
                    {
                        result[i] =
                            (decimal)Math.Sqrt((double)data[i])
                            * (decimal)Math.Log10((double)(data[i] + 1));
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Поток {threadIndex} ошибка: {ex.Message}");
                    throw;
                }
            });
            // Запустить все потоки
            threads[t].Start();
        }

        // Ждём завершения всех потоков
        for (int t = 0; t < threadCount; t++)
        {
            threads[t].Join();
        }

        return result;
    }
}
