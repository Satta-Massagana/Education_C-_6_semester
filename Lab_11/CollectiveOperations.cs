namespace Lab_11;

// Реализация коллективных операций. Алгоритмы используют дерево и кольцо, а не простую отправку всем подряд.
public static class CollectiveOperations
{
    public static object? ImplementBroadcast(MpiCommunicator comm, object? message, int root)
    {
        ValidateRoot(comm, root);

        object? currentMessage = message;
        // Дерево строится в логических индексах от root, поэтому root может быть не равен 0.
        var parent = GetParent(comm.Rank, root, comm.TotalNodes);

        if (comm.Rank != root)
        {
            // Некорневой узел сначала получает сообщение от родителя в бинарном дереве.
            currentMessage = comm.ReceiveCollective(
                parent,
                MpiMessageTypes.Broadcast,
                MpiTags.Broadcast
            );
        }

        // После получения узел пересылает сообщение своим детям. Так root отправляет не всем, а максимум двум узлам.
        foreach (var child in GetChildren(comm.Rank, root, comm.TotalNodes))
        {
            comm.SendCollective(
                child,
                currentMessage,
                MpiMessageTypes.Broadcast,
                MpiTags.Broadcast
            );
        }

        return currentMessage;
    }

    public static object?[] ImplementGather(MpiCommunicator comm, object? localData, int root)
    {
        ValidateRoot(comm, root);

        // Tree-gather: каждый узел начинает с собственного значения.
        var gatheredValues = new List<RankedValue> { new(comm.Rank, localData) };

        // Затем узел принимает уже агрегированные списки от своих дочерних узлов.
        foreach (var child in GetChildren(comm.Rank, root, comm.TotalNodes))
        {
            var childValues =
                (List<RankedValue>)(
                    comm.ReceiveCollective(child, MpiMessageTypes.Gather, MpiTags.Gather)
                    ?? throw new InvalidOperationException(
                        "Gather получил пустой пакет от дочернего узла."
                    )
                );

            gatheredValues.AddRange(childValues);
        }

        if (comm.Rank != root)
        {
            var parent = GetParent(comm.Rank, root, comm.TotalNodes);
            // Узел отправляет родителю один агрегированный пакет: свои данные плюс данные всего поддерева.
            comm.SendCollective(parent, gatheredValues, MpiMessageTypes.Gather, MpiTags.Gather);
            return Array.Empty<object?>();
        }

        var result = new object?[comm.TotalNodes];
        // Root раскладывает собранные значения по индексам ranks, чтобы результат был в стабильном порядке.
        foreach (var value in gatheredValues)
        {
            result[value.OwnerRank] = value.Value.ToObject();
        }

        return result;
    }

    public static object? ImplementScatter(MpiCommunicator comm, object?[] data, int root)
    {
        ValidateRoot(comm, root);

        ScatterPackage package;

        if (comm.Rank == root)
        {
            // Root упаковывает все значения, но дальше каждому поддереву отправит только его часть.
            if (data.Length != comm.TotalNodes)
            {
                throw new ArgumentException(
                    "Для Scatter массив должен содержать значение для каждого узла.",
                    nameof(data)
                );
            }

            package = new ScatterPackage();
            for (var rank = 0; rank < data.Length; rank++)
            {
                package.Values[rank] = SerializedValue.FromObject(data[rank]);
            }
        }
        else
        {
            var parent = GetParent(comm.Rank, root, comm.TotalNodes);
            package = (ScatterPackage)(
                comm.ReceiveCollective(parent, MpiMessageTypes.Scatter, MpiTags.Scatter)
                ?? throw new InvalidOperationException("Scatter получил пустой пакет.")
            );
        }

        foreach (var child in GetChildren(comm.Rank, root, comm.TotalNodes))
        {
            var childPackage = new ScatterPackage();

            // Для каждого ребенка выбираем ranks, которые находятся в его поддереве.
            foreach (var rank in GetSubtreeRanks(child, root, comm.TotalNodes))
            {
                if (package.Values.TryGetValue(rank, out var value))
                {
                    childPackage.Values[rank] = value;
                }
            }

            if (childPackage.Values.Count > 0)
            {
                comm.SendCollective(child, childPackage, MpiMessageTypes.Scatter, MpiTags.Scatter);
            }
        }

        return package.Values.TryGetValue(comm.Rank, out var ownValue) ? ownValue.ToObject() : null;
    }

    public static object? ImplementReduce(
        MpiCommunicator comm,
        object localValue,
        Func<object, object, object> op,
        int root
    )
    {
        ValidateRoot(comm, root);

        object accumulator = localValue;

        // Дети отправляют агрегированные значения родителю, а родитель применяет операцию.
        foreach (var child in GetChildren(comm.Rank, root, comm.TotalNodes))
        {
            var childValue =
                comm.ReceiveCollective(child, MpiMessageTypes.Reduce, MpiTags.Reduce)
                ?? throw new InvalidOperationException(
                    "Reduce получил пустое значение от дочернего узла."
                );
            // В отличие от Gather, Reduce не хранит все данные, а сворачивает их в одно значение.
            accumulator = op(accumulator, childValue);
        }

        if (comm.Rank != root)
        {
            var parent = GetParent(comm.Rank, root, comm.TotalNodes);
            // Некорневой узел отправляет родителю уже посчитанный промежуточный результат.
            comm.SendCollective(parent, accumulator, MpiMessageTypes.Reduce, MpiTags.Reduce);
            return null;
        }

        return accumulator;
    }

    public static void ImplementBarrier(MpiCommunicator comm)
    {
        const int root = 0;

        // Arrival-фаза: все участники сообщают root, что дошли до барьера.
        if (comm.Rank == root)
        {
            // На фазе arrival root ждет служебное сообщение от каждого участника.
            for (var rank = 0; rank < comm.TotalNodes; rank++)
            {
                if (rank != root)
                {
                    comm.ReceiveCollective(rank, MpiMessageTypes.Barrier, MpiTags.BarrierArrival);
                }
            }
        }
        else
        {
            comm.SendCollective(root, "READY", MpiMessageTypes.Barrier, MpiTags.BarrierArrival);
        }

        var parent = GetParent(comm.Rank, root, comm.TotalNodes);

        // Release-фаза: root разрешает выход из барьера, а сигнал GO расходится по дереву.
        if (comm.Rank != root)
        {
            // На фазе release узел выходит из барьера только после сигнала от родителя.
            comm.ReceiveCollective(parent, MpiMessageTypes.Barrier, MpiTags.BarrierRelease);
        }

        foreach (var child in GetChildren(comm.Rank, root, comm.TotalNodes))
        {
            comm.SendCollective(child, "GO", MpiMessageTypes.Barrier, MpiTags.BarrierRelease);
        }
    }

    public static object?[] ImplementAllGather(MpiCommunicator comm, object? localData)
    {
        // AllGather оставлен кольцевым: в конце каждый узел получает данные всех остальных узлов.
        return RingAllGather(comm, localData, rootOnly: false, root: 0);
    }

    private static object?[] RingAllGather(
        MpiCommunicator comm,
        object? localData,
        bool rootOnly,
        int root
    )
    {
        var result = new object?[comm.TotalNodes];
        result[comm.Rank] = localData;

        var next = (comm.Rank + 1) % comm.TotalNodes;
        var previous = (comm.Rank - 1 + comm.TotalNodes) % comm.TotalNodes;
        var currentChunk = new RankedValue(comm.Rank, localData);

        for (var step = 0; step < comm.TotalNodes - 1; step++)
        {
            // В кольце каждый узел отправляет ровно один фрагмент за шаг и получает один фрагмент от соседа.
            comm.SendCollective(next, currentChunk, MpiMessageTypes.Gather, MpiTags.Gather);

            var receivedChunk = (RankedValue)(
                comm.ReceiveCollective(previous, MpiMessageTypes.Gather, MpiTags.Gather)
                ?? throw new InvalidOperationException("Gather получил пустой фрагмент.")
            );

            result[receivedChunk.OwnerRank] = receivedChunk.Value.ToObject();
            currentChunk = receivedChunk;
        }

        return !rootOnly || comm.Rank == root ? result : Array.Empty<object?>();
    }

    private static void ValidateRoot(MpiCommunicator comm, int root)
    {
        if (root < 0 || root >= comm.TotalNodes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(root),
                "Root должен быть существующим rank."
            );
        }
    }

    private static int GetParent(int rank, int root, int totalNodes)
    {
        if (rank == root)
        {
            return root;
        }

        // В бинарном дереве родитель логического индекса i находится в (i - 1) / 2.
        var logicalIndex = ToLogicalIndex(rank, root, totalNodes);
        var parentLogicalIndex = (logicalIndex - 1) / 2;
        return ToRank(parentLogicalIndex, root, totalNodes);
    }

    private static IEnumerable<int> GetChildren(int rank, int root, int totalNodes)
    {
        // Для бинарного дерева дети логического индекса i находятся в 2*i+1 и 2*i+2.
        var logicalIndex = ToLogicalIndex(rank, root, totalNodes);
        var left = logicalIndex * 2 + 1;
        var right = logicalIndex * 2 + 2;

        if (left < totalNodes)
        {
            yield return ToRank(left, root, totalNodes);
        }

        if (right < totalNodes)
        {
            yield return ToRank(right, root, totalNodes);
        }
    }

    private static IEnumerable<int> GetSubtreeRanks(int subtreeRootRank, int root, int totalNodes)
    {
        // Обход поддерева нужен Scatter, чтобы отправлять ребенку только данные его ветки.
        var subtreeRootLogical = ToLogicalIndex(subtreeRootRank, root, totalNodes);
        var stack = new Stack<int>();
        stack.Push(subtreeRootLogical);

        while (stack.Count > 0)
        {
            var logical = stack.Pop();
            if (logical >= totalNodes)
            {
                continue;
            }

            yield return ToRank(logical, root, totalNodes);

            // Сначала кладем правого, потом левого, чтобы обход был стабильным слева направо.
            stack.Push(logical * 2 + 2);
            stack.Push(logical * 2 + 1);
        }
    }

    private static int ToLogicalIndex(int rank, int root, int totalNodes)
    {
        // Перенумеровываем ranks так, чтобы root стал логическим индексом 0.
        return (rank - root + totalNodes) % totalNodes;
    }

    private static int ToRank(int logicalIndex, int root, int totalNodes)
    {
        // Возвращаемся из логического индекса дерева к реальному rank узла.
        return (logicalIndex + root) % totalNodes;
    }
}
