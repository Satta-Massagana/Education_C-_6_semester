using System.Diagnostics;
using System.Net.Sockets;

namespace Lab_13;

public sealed class TcpCompressionClient
{
    public async Task<bool> SendHeartbeatAsync(
        WorkerDescriptor worker,
        CancellationToken cancellationToken
    )
    {
        using var client = new TcpClient();
        await client.ConnectAsync(worker.Host, worker.Port, cancellationToken);
        await using var stream = client.GetStream();

        var request = new TcpRequestEnvelope(
            "heartbeat",
            worker.Id,
            "heartbeat",
            0,
            0,
            CompressionAlgorithmType.GZip.ToString(),
            string.Empty,
            0
        );

        await TcpProtocol.WriteFrameAsync(stream, request, Array.Empty<byte>(), cancellationToken);
        var response = await TcpProtocol.ReadHeaderAsync<TcpResponseEnvelope>(
            stream,
            cancellationToken
        );
        return response.Success && response.Command == "heartbeat";
    }

    public async Task<WorkerCompressionResult> CompressAsync(
        CompressionWorkItem item,
        CancellationToken cancellationToken
    )
    {
        var networkStopwatch = Stopwatch.StartNew();
        using var client = new TcpClient();

        await client.ConnectAsync(item.Worker.Host, item.Worker.Port, cancellationToken);
        await using var stream = client.GetStream();

        var request = new TcpRequestEnvelope(
            "compress",
            item.Worker.Id,
            item.JobId,
            item.PartIndex,
            item.TotalParts,
            item.Algorithm.ToString(),
            item.FileName,
            item.Payload.LongLength
        );

        // Мастер отправляет задание воркеру по TCP и асинхронно ожидает ответ.
        await TcpProtocol.WriteFrameAsync(stream, request, item.Payload, cancellationToken);
        var header = await TcpProtocol.ReadHeaderAsync<TcpResponseEnvelope>(
            stream,
            cancellationToken
        );
        var payload = await TcpProtocol.ReadPayloadAsync(
            stream,
            header.PayloadLength,
            cancellationToken
        );
        networkStopwatch.Stop();

        return new WorkerCompressionResult(
            header.Success,
            header.WorkerId,
            header.PartIndex,
            item.TotalParts,
            payload,
            header.OriginalBytes,
            header.CompressedBytes,
            header.WorkerElapsedMilliseconds,
            networkStopwatch.ElapsedMilliseconds,
            header.ErrorMessage
        );
    }
}
