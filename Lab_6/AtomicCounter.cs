namespace Lab_6;

public sealed class AtomicCounter
{
    private long _value;

    public AtomicCounter(long initialValue)
    {
        _value = initialValue;
    }

    public long Value => Interlocked.Read(ref _value);

    public long Increment() => Interlocked.Increment(ref _value);

    public long Decrement() => Interlocked.Decrement(ref _value);

    public long Add(long value) => Interlocked.Add(ref _value, value);

    public long Exchange(long newValue) => Interlocked.Exchange(ref _value, newValue);

    public long CompareExchange(long newValue, long comparand) =>
        Interlocked.CompareExchange(ref _value, newValue, comparand);

    public long Reset() => Interlocked.Exchange(ref _value, 0);

    public long GetAndIncrement() => Interlocked.Increment(ref _value) - 1;

    public long GetAndDecrement() => Interlocked.Decrement(ref _value) + 1;
}
