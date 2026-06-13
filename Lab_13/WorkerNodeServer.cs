using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Lab_13;

public sealed class WorkerNodeServer : IAsyncDisposable
{
    private readonly WorkerDescriptor descriptor;
    private readonly CompressionAlgorithmRegistry algorithms;
    private readonly ILogger logger;
    private readonly TcpListener listener;
    private CancellationTokenSource? lifetimeCancellation;
    private Task? acceptLoopTask;

    public WorkerNodeServer(
        WorkerDescriptor descriptor,
        CompressionAlgorithmRegistry algorithms,
        ILogger logger
    )
    {
        this.descriptor = descriptor;
        this.algorithms = algorithms;
        this.logger = logger;
        listener = new TcpListener(IPAddress.Parse(descriptor.Host), descriptor.Port);
    }

    public void Start(CancellationToken cancellationToken)
    {
        lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        listener.Start();

        // Цикл приема соединений работает в TPL-задаче, без ручного создания Thread.
        acceptLoopTask = Task.Run(
            () => AcceptLoopAsync(lifetimeCancellation.Token),
            CancellationToken.None
        );
        logger.LogInformation(
            "{WorkerId} запущен на {Host}:{Port}.",
            descriptor.Id,
            descriptor.Host,
            descriptor.Port
        );
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "{WorkerId}: ошибка приема TCP соединения.", descriptor.Id);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var _ = client;

        try
        {
            await using var stream = client.GetStream();
            var request = await TcpProtocol.ReadHeaderAsync<TcpRequestEnvelope>(
                stream,
                cancellationToken
            );

            if (request.Command.Equals("heartbeat", StringComparison.OrdinalIgnoreCase))
            {
                await WriteHeartbeatAsync(stream, request, cancellationToken);
                return;
            }

            if (!request.Command.Equals("compress", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Неизвестная команда {request.Command}.");
            }

            var payload = await TcpProtocol.ReadPayloadAsync(
                stream,
                request.PayloadLength,
                cancellationToken
            );
            var result = await CompressPayloadAsync(request, payload, stream, cancellationToken);
            await TcpProtocol.WriteFrameAsync(
                stream,
                result.Header,
                result.Payload,
                cancellationToken
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{WorkerId}: ошибка обработки TCP клиента.", descriptor.Id);
        }
    }

    private async Task WriteHeartbeatAsync(
        NetworkStream stream,
        TcpRequestEnvelope request,
        CancellationToken cancellationToken
    )
    {
        var response = new TcpResponseEnvelope(
            "heartbeat",
            true,
            descriptor.Id,
            request.JobId,
            request.PartIndex,
            0,
            0,
            0,
            0,
            null
        );

        await TcpProtocol.WriteFrameAsync(stream, response, Array.Empty<byte>(), cancellationToken);
    }

    private async Task<(TcpResponseEnvelope Header, byte[] Payload)> CompressPayloadAsync(
        TcpRequestEnvelope request,
        byte[] payload,
        NetworkStream stream,
        CancellationToken cancellationToken
    )
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var type = Enum.Parse<CompressionAlgorithmType>(request.Algorithm);
            var compressed = await algorithms
                .Get(type)
                .CompressAsync(
                    payload,
                    request.FileName,
                    cancellationToken,
                    processedBytes =>
                        WriteCompressionProgressAsync(
                            stream,
                            request,
                            payload.LongLength,
                            processedBytes,
                            stopwatch.ElapsedMilliseconds,
                            cancellationToken
                        )
                );
            stopwatch.Stop();

            var response = new TcpResponseEnvelope(
                "compress",
                true,
                descriptor.Id,
                request.JobId,
                request.PartIndex,
                compressed.Length,
                payload.Length,
                compressed.Length,
                stopwatch.ElapsedMilliseconds,
                null
            );

            return (response, compressed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            // Ошибка возвращается мастеру как часть протокола, чтобы он мог переназначить задачу.
            var response = new TcpResponseEnvelope(
                "compress",
                false,
                descriptor.Id,
                request.JobId,
                request.PartIndex,
                0,
                payload.Length,
                0,
                stopwatch.ElapsedMilliseconds,
                ex.Message
            );

            return (response, Array.Empty<byte>());
        }
    }

    private async ValueTask WriteCompressionProgressAsync(
        NetworkStream stream,
        TcpRequestEnvelope request,
        long totalBytes,
        long processedBytes,
        long elapsedMilliseconds,
        CancellationToken cancellationToken
    )
    {
        var response = new TcpResponseEnvelope(
            "progress",
            true,
            descriptor.Id,
            request.JobId,
            request.PartIndex,
            0,
            totalBytes,
            processedBytes,
            elapsedMilliseconds,
            null
        );

        await TcpProtocol.WriteFrameAsync(stream, response, Array.Empty<byte>(), cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (lifetimeCancellation is not null)
        {
            await lifetimeCancellation.CancelAsync();
            lifetimeCancellation.Dispose();
        }

        listener.Stop();

        if (acceptLoopTask is not null)
        {
            try
            {
                await acceptLoopTask;
            }
            catch (OperationCanceledException) { }
        }
    }
}
