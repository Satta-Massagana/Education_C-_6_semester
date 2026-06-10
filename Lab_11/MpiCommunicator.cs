using System.Diagnostics;

namespace Lab_11;

// Коммуникатор связывает код приложения с сетевым узлом и предоставляет MPI-подобные операции.
public sealed class MpiCommunicator
{
    // Inbox хранит сообщения, которые уже пришли по TCP, но еще не были получены нужным Receive.
    private readonly object _inboxLock = new();
    private readonly List<MpiMessage> _inbox = new();

    // SemaphoreSlim будит Receive, когда read-loop узла добавил новое сообщение во входящую очередь.
    private readonly SemaphoreSlim _messageArrived = new(0);
    private MpiNode? _node;

    public int NodeId { get; }
    public int TotalNodes { get; }

    // В MPI rank — это номер процесса внутри коммуникатора. rank равен NodeId.
    public int Rank => NodeId;

    // Таймаут защищает от бесконечного ожидания при ошибках сети или неправильной последовательности операций.
    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public MpiCommunicator(int nodeId, int totalNodes)
    {
        if (totalNodes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(totalNodes),
                "Количество узлов должно быть положительным."
            );
        }

        if (nodeId < 0 || nodeId >= totalNodes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nodeId),
                "Идентификатор узла должен попадать в диапазон ranks."
            );
        }

        NodeId = nodeId;
        TotalNodes = totalNodes;
    }

    // Привязывает коммуникатор к конкретному TCP-узлу.
    public MpiCommunicator AttachNode(MpiNode node)
    {
        if (node.NodeId != NodeId)
        {
            throw new ArgumentException(
                "Идентификатор узла должен совпадать с rank коммуникатора.",
                nameof(node)
            );
        }

        if (_node is not null)
        {
            _node.MessageReceived -= OnMessageReceived;
        }

        _node = node;
        // Все входящие сообщения узла попадают во внутреннюю очередь коммуникатора.
        _node.MessageReceived += OnMessageReceived;
        return this;
    }

    // Отправляет сообщение конкретному rank.
    public void Send(int destination, object? message)
    {
        SendInternal(destination, message, MpiMessageTypes.Send, MpiTags.Default);
    }

    // Принимает сообщение от конкретного rank. Используется тег по умолчанию.
    public object? Receive(int source)
    {
        return ReceiveInternal(source, MpiMessageTypes.Send, MpiTags.Default);
    }

    // Версия Receive с явным тегом. Она показывает, как теги помогают фильтровать похожие сообщения.
    public object? Receive(int source, int tag)
    {
        return ReceiveInternal(source, MpiMessageTypes.Send, tag);
    }

    // Комбинированная операция: сначала отправляем, затем ждем ответ от другого узла.
    public object? SendReceive(int destination, object? sendMsg, int source)
    {
        Send(destination, sendMsg);
        return Receive(source);
    }

    public object? Broadcast(object? message, int root)
    {
        return CollectiveOperations.ImplementBroadcast(this, message, root);
    }

    public object?[] Gather(object? localData, int root)
    {
        return CollectiveOperations.ImplementGather(this, localData, root);
    }

    public object? Scatter(object?[] data, int root)
    {
        return CollectiveOperations.ImplementScatter(this, data, root);
    }

    public object?[] AllGather(object? localData)
    {
        return CollectiveOperations.ImplementAllGather(this, localData);
    }

    public object? Reduce(object localValue, Func<object, object, object> operation, int root)
    {
        return CollectiveOperations.ImplementReduce(this, localValue, operation, root);
    }

    public object? AllReduce(object localValue, Func<object, object, object> operation)
    {
        // AllReduce = Reduce к root + Broadcast результата всем узлам.
        var reduced = Reduce(localValue, operation, root: 0);
        return Broadcast(reduced, root: 0);
    }

    internal void SendCollective(int destination, object? payload, string messageType, int tag)
    {
        SendInternal(destination, payload, messageType, tag);
    }

    internal object? ReceiveCollective(int source, string messageType, int tag)
    {
        return ReceiveInternal(source, messageType, tag);
    }

    internal MpiMessage ReceiveMessage(int source, string? messageType, int tag)
    {
        var timeoutWatch = Stopwatch.StartNew();

        while (true)
        {
            lock (_inboxLock)
            {
                // Ищем первое сообщение, подходящее по отправителю, типу и тегу.
                var index = _inbox.FindIndex(message =>
                    (source < 0 || message.SourceRank == source)
                    && (messageType is null || message.MessageType == messageType)
                    && (tag == MpiTags.AnyTag || message.Tag == tag)
                );

                if (index >= 0)
                {
                    // Сообщение удаляется из очереди, чтобы следующий Receive не получил его повторно.
                    var message = _inbox[index];
                    _inbox.RemoveAt(index);
                    return message;
                }
            }

            var remaining = OperationTimeout - timeoutWatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException(
                    $"Узел {Rank} не дождался сообщения: source={source}, type={messageType ?? "*"}, tag={tag}."
                );
            }

            // Если подходящего сообщения нет, ждем сигнал от OnMessageReceived или истечения timeout.
            if (!_messageArrived.Wait(remaining))
            {
                throw new TimeoutException(
                    $"Узел {Rank} не дождался сообщения за {OperationTimeout.TotalSeconds:0.##} сек."
                );
            }
        }
    }

    private void SendInternal(int destination, object? payload, string messageType, int tag)
    {
        ValidateRank(destination);

        var node =
            _node ?? throw new InvalidOperationException("Коммуникатор не привязан к MpiNode.");
        // MpiMessage сериализует payload в byte[], поэтому сетевой слой не зависит от типа данных.
        var message = new MpiMessage(messageType, Rank, destination, tag, payload);

        node.SendMessageAsync(message).GetAwaiter().GetResult();
    }

    private object? ReceiveInternal(int source, string messageType, int tag)
    {
        ValidateRank(source);
        // ReceiveMessage возвращает конверт, а DeserializePayload достает исходный объект из Payload.
        return ReceiveMessage(source, messageType, tag).DeserializePayload();
    }

    private void ValidateRank(int rank)
    {
        if (rank < 0 || rank >= TotalNodes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rank),
                $"Rank {rank} находится вне диапазона 0..{TotalNodes - 1}."
            );
        }
    }

    private void OnMessageReceived(MpiMessage message)
    {
        lock (_inboxLock)
        {
            // Сообщения складываются в очередь: конкретный Receive сам отфильтрует нужное по source/type/tag.
            _inbox.Add(message);
        }

        // Будим ожидающие Receive.
        _messageArrived.Release();
    }
}
