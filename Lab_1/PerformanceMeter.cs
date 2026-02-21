using System;
using System.Diagnostics;
using System.Linq;

public static class PerformanceMeter
{
    public static (long timeMs, decimal[] result) MeasureExecutionTime(
        Func<decimal[]> action,
        string operationName
    )
    {
        // Замер времени выполнения
        var stopwatch = Stopwatch.StartNew();
        decimal[] result = action();
        stopwatch.Stop();
        long timeMs = stopwatch.ElapsedMilliseconds;
        return (timeMs, result);
    }

    public static bool CompareResults(
        decimal[] result1,
        decimal[] result2,
        decimal tolerance = 0.0001m
    )
    {
        if (result1.Length != result2.Length)
            return false;
        return result1
            // Проверка одинаковости двух массивов через склеивание
            .Zip(result2, (r1, r2) => Math.Abs(r1 - r2) <= tolerance)
            .All(diffOk => diffOk);
    }
}
