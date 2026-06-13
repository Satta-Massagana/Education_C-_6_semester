using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Lab_13;

public static class TcpProtocol
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task WriteFrameAsync<THeader>(
        NetworkStream stream,
        THeader header,
        byte[] payload,
        CancellationToken cancellationToken,
        Func<long, long, ValueTask>? payloadProgressCallback = null
    )
    {
        var json = JsonSerializer.Serialize(header, JsonOptions) + "\n";
        var headerBytes = Encoding.UTF8.GetBytes(json);

        // Протокол состоит из JSON-заголовка и бинарной полезной нагрузки фиксированной длины.
        await stream.WriteAsync(headerBytes, cancellationToken);
        if (payload.Length > 0)
        {
            const int chunkSize = 1024 * 1024;
            long sent = 0;

            while (sent < payload.LongLength)
            {
                var length = (int)Math.Min(chunkSize, payload.LongLength - sent);
                await stream.WriteAsync(payload.AsMemory((int)sent, length), cancellationToken);
                sent += length;

                if (payloadProgressCallback is not null)
                {
                    await payloadProgressCallback(sent, payload.LongLength);
                }
            }
        }

        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<THeader> ReadHeaderAsync<THeader>(
        NetworkStream stream,
        CancellationToken cancellationToken
    )
    {
        var buffer = new List<byte>(512);
        var oneByte = new byte[1];

        while (true)
        {
            var read = await stream.ReadAsync(oneByte, cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("TCP соединение потеряно");
            }

            if (oneByte[0] == (byte)'\n')
            {
                break;
            }

            buffer.Add(oneByte[0]);
        }

        var json = Encoding.UTF8.GetString(buffer.ToArray());
        return JsonSerializer.Deserialize<THeader>(json, JsonOptions)
            ?? throw new InvalidOperationException(
                "Не удалось разобрать JSON-заголовок TCP сообщения."
            );
    }

    public static async Task<byte[]> ReadPayloadAsync(
        NetworkStream stream,
        long payloadLength,
        CancellationToken cancellationToken
    )
    {
        if (payloadLength < 0 || payloadLength > int.MaxValue)
        {
            throw new InvalidDataException("Некорректный размер полезной нагрузки.");
        }

        var payload = new byte[payloadLength];
        var offset = 0;

        while (offset < payload.Length)
        {
            var read = await stream.ReadAsync(payload.AsMemory(offset), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("TCP соединение потеряно");
            }

            offset += read;
        }

        return payload;
    }
}
