namespace Lab_6;

public sealed class StatisticsTracker
{
    private long _totalRequests;
    private long _successfulRequests;
    private long _failedRequests;
    private long _totalProcessingTime;

    public long TotalRequests => Interlocked.Read(ref _totalRequests);

    public long SuccessfulRequests => Interlocked.Read(ref _successfulRequests);

    public long FailedRequests => Interlocked.Read(ref _failedRequests);

    public long TotalProcessingTime => Interlocked.Read(ref _totalProcessingTime);

    public void RecordRequest(bool success, long processingTime)
    {
        Interlocked.Increment(ref _totalRequests);
        if (success)
            Interlocked.Increment(ref _successfulRequests);
        else
            Interlocked.Increment(ref _failedRequests);

        Interlocked.Add(ref _totalProcessingTime, processingTime);
    }

    public double GetSuccessRate()
    {
        var total = Interlocked.Read(ref _totalRequests);
        if (total == 0)
            return 0;

        var ok = Interlocked.Read(ref _successfulRequests);
        return ok * 100.0 / total;
    }

    public double GetAverageProcessingTime()
    {
        var total = Interlocked.Read(ref _totalRequests);
        if (total == 0)
            return 0;

        var time = Interlocked.Read(ref _totalProcessingTime);
        return (double)time / total;
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _totalRequests, 0);
        Interlocked.Exchange(ref _successfulRequests, 0);
        Interlocked.Exchange(ref _failedRequests, 0);
        Interlocked.Exchange(ref _totalProcessingTime, 0);
    }
}
