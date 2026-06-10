using System.Text.Json;

namespace Lab_11;

// Текстовые типы сообщений
public static class MpiMessageTypes
{
    public const string Send = "SEND";
    public const string Broadcast = "BROADCAST";
    public const string Gather = "GATHER";
    public const string Scatter = "SCATTER";
    public const string Reduce = "REDUCE";
    public const string Barrier = "BARRIER";
}

// Теги нужны для фильтрации сообщений одного типа и разделения разных операций.
public static class MpiTags
{
    public const int AnyTag = -1;
    public const int Default = 0;
    public const int Broadcast = 100;
    public const int Gather = 200;
    public const int Scatter = 300;
    public const int Reduce = 400;
    public const int BarrierArrival = 500;
    public const int BarrierRelease = 501;
}

// Основная структура MPI-подобного сообщения, которое передается по TCP.
// Payload хранится как byte[], чтобы транспортный уровень не зависел от конкретного типа данных.
public class MpiMessage
{
    public Guid MessageId { get; set; } = Guid.NewGuid();
    public string MessageType { get; set; } = MpiMessageTypes.Send;
    public int SourceRank { get; set; }
    public int DestinationRank { get; set; }
    public int Tag { get; set; } = MpiTags.Default;
    public byte[] Payload { get; set; } = Array.Empty<byte>();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    // Имя CLR-типа полезной нагрузки. Благодаря этому Receive возвращает не JsonElement, а исходный тип.
    public string? PayloadType { get; set; }

    public MpiMessage() { }

    public MpiMessage(
        string messageType,
        int sourceRank,
        int destinationRank,
        int tag,
        object? payload
    )
    {
        // Любой объект сначала превращается в SerializedValue: там лежит тип и JSON-байты.
        var serialized = SerializedValue.FromObject(payload);

        MessageType = messageType;
        SourceRank = sourceRank;
        DestinationRank = destinationRank;
        Tag = tag;
        Payload = serialized.Data;
        PayloadType = serialized.TypeName;
        Timestamp = DateTime.UtcNow;
    }

    public object? DeserializePayload()
    {
        // При получении сообщения восстанавливаем payload по сохраненному CLR-типу.
        return SerializedValue.FromRaw(PayloadType, Payload).ToObject();
    }
}

// Сообщение "точка-точка": один отправитель и один получатель.
public class PointToPointMessage : MpiMessage
{
    public PointToPointMessage() { }

    public PointToPointMessage(int sourceRank, int destinationRank, int tag, object? payload)
        : base(MpiMessageTypes.Send, sourceRank, destinationRank, tag, payload) { }
}

// Сообщение коллективной операции. OperationId оставлен для трассировки и отладки.
public class CollectiveMessage : MpiMessage
{
    public Guid OperationId { get; set; } = Guid.NewGuid();
    public int RootRank { get; set; }

    public CollectiveMessage() { }

    public CollectiveMessage(
        string messageType,
        int sourceRank,
        int destinationRank,
        int tag,
        int rootRank,
        object? payload
    )
        : base(messageType, sourceRank, destinationRank, tag, payload)
    {
        RootRank = rootRank;
    }
}

// Служебное сообщение для синхронизации узлов в барьере.
public class BarrierMessage : CollectiveMessage
{
    public BarrierMessage() { }

    public BarrierMessage(
        int sourceRank,
        int destinationRank,
        int tag,
        int rootRank,
        object? payload
    )
        : base(MpiMessageTypes.Barrier, sourceRank, destinationRank, tag, rootRank, payload) { }
}

// Универсальная сериализованная оболочка для вложенных значений в коллективных операциях.
public sealed class SerializedValue
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string? TypeName { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();

    public static SerializedValue FromObject(object? value)
    {
        if (value is null)
        {
            return new SerializedValue();
        }

        var type = value.GetType();

        // Сохраняем AssemblyQualifiedName, чтобы на другой стороне восстановить конкретный тип объекта.
        return new SerializedValue
        {
            TypeName = type.AssemblyQualifiedName,
            Data = JsonSerializer.SerializeToUtf8Bytes(value, type, JsonOptions),
        };
    }

    public static SerializedValue FromRaw(string? typeName, byte[] data)
    {
        return new SerializedValue { TypeName = typeName, Data = data };
    }

    public object? ToObject()
    {
        if (string.IsNullOrWhiteSpace(TypeName) || Data.Length == 0)
        {
            return null;
        }

        var type = Type.GetType(TypeName);
        // Если тип найден, десериализуем строго в него; если нет — возвращаем обычный object из JSON.
        return type is null
            ? JsonSerializer.Deserialize<object>(Data, JsonOptions)
            : JsonSerializer.Deserialize(Data, type, JsonOptions);
    }
}

// Значение с указанием ранга-владельца. Используется в tree-gather и кольцевом AllGather.
public sealed class RankedValue
{
    public int OwnerRank { get; set; }
    public SerializedValue Value { get; set; } = new();

    public RankedValue() { }

    public RankedValue(int ownerRank, object? value)
    {
        // OwnerRank нужен root-узлу, чтобы разложить собранные значения в правильном порядке ranks.
        OwnerRank = ownerRank;
        Value = SerializedValue.FromObject(value);
    }
}

// Пакет Scatter: узел получает только те значения, которые нужны ему и его потомкам в дереве.
public sealed class ScatterPackage
{
    public Dictionary<int, SerializedValue> Values { get; set; } = new();
}
