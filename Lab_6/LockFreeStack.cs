namespace Lab_6;

public sealed class LockFreeStack<T>
{
    public sealed class Node
    {
        public required T Value { get; init; }
        public Node? Next { get; set; }
    }

    private Node? _head;

    private static Node? ReadHead(ref Node? head) =>
        Interlocked.CompareExchange(ref head, null, null);

    public void Push(T item)
    {
        var node = new Node { Value = item };
        while (true)
        {
            var observed = ReadHead(ref _head);
            node.Next = observed;
            if (Interlocked.CompareExchange(ref _head, node, observed) == observed)
                return;
        }
    }

    public bool TryPop(out T item)
    {
        while (true)
        {
            var observed = ReadHead(ref _head);
            if (observed is null)
            {
                item = default!;
                return false;
            }

            var next = observed.Next;
            if (Interlocked.CompareExchange(ref _head, next, observed) == observed)
            {
                item = observed.Value;
                return true;
            }
        }
    }

    public bool TryPeek(out T item)
    {
        var observed = ReadHead(ref _head);
        if (observed is null)
        {
            item = default!;
            return false;
        }

        item = observed.Value;
        return true;
    }

    public bool IsEmpty() => ReadHead(ref _head) is null;

    public void Clear()
    {
        while (true)
        {
            var observed = ReadHead(ref _head);
            if (observed is null)
                return;

            if (Interlocked.CompareExchange(ref _head, null, observed) == observed)
                return;
        }
    }
}
