using System.Text;

namespace Lab10;

public class NetworkMessage
{
    public Guid MessageId { get; set; } = Guid.NewGuid();
    public string MessageType { get; set; } = string.Empty;
    public byte[] Payload { get; set; } = Array.Empty<byte>();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string SenderId { get; set; } = string.Empty;
}

public static class MessageProtocol
{
    public const int MaxMessageSize = 10 * 1024 * 1024;

    public static byte[] Serialize(NetworkMessage message)
    {
        if (!Validate(message))
            throw new ArgumentException("Сообщение не прошло валидацию.", nameof(message));

        using var bodyStream = new MemoryStream();
        using (var writer = new BinaryWriter(bodyStream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(message.MessageId.ToByteArray());
            writer.Write(message.MessageType);
            writer.Write(message.Payload.Length);
            writer.Write(message.Payload);
            writer.Write(message.Timestamp.ToBinary());
            writer.Write(message.SenderId);
        }

        var body = bodyStream.ToArray();
        using var packetStream = new MemoryStream();
        using (var writer = new BinaryWriter(packetStream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(body.Length);
            writer.Write(body);
        }

        return packetStream.ToArray();
    }

    public static int GetSerializedSize(NetworkMessage message) => Serialize(message).Length;

    public static NetworkMessage Deserialize(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        using var stream = new MemoryStream(data);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        var bodyLength = reader.ReadInt32();
        if (bodyLength <= 0 || bodyLength > MaxMessageSize)
            throw new InvalidDataException($"Недопустимая длина тела сообщения: {bodyLength}.");

        var body = reader.ReadBytes(bodyLength);
        if (body.Length != bodyLength)
            throw new InvalidDataException("Неполные данные тела сообщения.");

        using var bodyStream = new MemoryStream(body);
        using var bodyReader = new BinaryReader(bodyStream, Encoding.UTF8, leaveOpen: true);

        var message = new NetworkMessage
        {
            MessageId = new Guid(bodyReader.ReadBytes(16)),
            MessageType = bodyReader.ReadString(),
            Payload = bodyReader.ReadBytes(bodyReader.ReadInt32()),
            Timestamp = DateTime.FromBinary(bodyReader.ReadInt64()),
            SenderId = bodyReader.ReadString(),
        };

        if (!Validate(message))
            throw new InvalidDataException("Десериализованное сообщение не прошло валидацию.");

        return message;
    }

    public static bool Validate(NetworkMessage? message)
    {
        if (message is null)
            return false;

        if (message.MessageId == Guid.Empty)
            return false;

        if (string.IsNullOrWhiteSpace(message.MessageType))
            return false;

        if (string.IsNullOrWhiteSpace(message.SenderId))
            return false;

        if (message.Payload is null)
            return false;

        var estimatedSize =
            sizeof(int)
            + 16
            + message.MessageType.Length * 4
            + sizeof(int)
            + message.Payload.Length
            + sizeof(long)
            + message.SenderId.Length * 4;

        return estimatedSize <= MaxMessageSize;
    }

    public static async Task<byte[]> ReadMessageAsync(
        Stream stream,
        CancellationToken cancellationToken
    )
    {
        var lengthBuffer = new byte[sizeof(int)];
        await ReadExactAsync(stream, lengthBuffer, cancellationToken).ConfigureAwait(false);

        var bodyLength = BitConverter.ToInt32(lengthBuffer, 0);
        if (bodyLength <= 0 || bodyLength > MaxMessageSize)
            throw new InvalidDataException($"Недопустимая длина сообщения: {bodyLength}.");

        var packet = new byte[sizeof(int) + bodyLength];
        Buffer.BlockCopy(lengthBuffer, 0, packet, 0, sizeof(int));

        await ReadExactAsync(stream, packet, sizeof(int), bodyLength, cancellationToken)
            .ConfigureAwait(false);
        return packet;
    }

    public static async Task WriteMessageAsync(
        Stream stream,
        NetworkMessage message,
        CancellationToken cancellationToken
    )
    {
        var data = Serialize(message);
        await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReadExactAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken
    )
    {
        await ReadExactAsync(stream, buffer, 0, buffer.Length, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task ReadExactAsync(
        Stream stream,
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    )
    {
        var totalRead = 0;
        while (totalRead < count)
        {
            var read = await stream
                .ReadAsync(
                    buffer.AsMemory(offset + totalRead, count - totalRead),
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (read == 0)
                throw new IOException("Соединение закрыто до получения полного сообщения.");

            totalRead += read;
        }
    }
}
