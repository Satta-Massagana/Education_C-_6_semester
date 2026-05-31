using System.Diagnostics;
using System.Net.Sockets;

namespace Lab10;

public readonly record struct BenchmarkResult(
    int ClientCount,
    int SentMessages,
    int ReceivedMessages,
    long SentBytes,
    long ReceivedBytes,
    long TransmittedBytes,
    TimeSpan Elapsed
)
{
    private const int IpHeaderSize = 20;
    private const int TcpHeaderSize = 20;
    private const int TcpMaxSegmentPayload = 1460;

    public double SentMegabytes => SentBytes / (1024.0 * 1024.0);
    public double ReceivedMegabytes => ReceivedBytes / (1024.0 * 1024.0);
    public double TransmittedMegabytes => TransmittedBytes / (1024.0 * 1024.0);
    public long TcpIpOverheadBytes
    {
        get
        {
            var segments = (TransmittedBytes + TcpMaxSegmentPayload - 1) / TcpMaxSegmentPayload;
            return segments * (IpHeaderSize + TcpHeaderSize);
        }
    }

    public long TransmittedWithTcpIpBytes => TransmittedBytes + TcpIpOverheadBytes;
    public double TransmittedWithTcpIpMegabytes => TransmittedWithTcpIpBytes / (1024.0 * 1024.0);
    public double SpeedMbitPerSec =>
        SentBytes * 8.0 / Math.Max(Elapsed.TotalSeconds, 0.001) / (1024.0 * 1024.0);
}

public sealed class NetworkBenchmark : IDisposable
{
    private readonly string _host;
    private readonly int _port;

    public NetworkBenchmark(string host = "127.0.0.1", int port = 8888)
    {
        _host = host;
        _port = port;
    }

    public async Task<BenchmarkResult> BenchmarkSingleClientAsync(
        int messageCount,
        int messageSize,
        CancellationToken cancellationToken = default
    )
    {
        using var server = CreateServer();
        server.Start();
        var port = GetServerPort(server);
        await WaitForServerAsync(port, cancellationToken).ConfigureAwait(false);

        using var client = CreateClient(port);
        var received = 0;
        client.MessageReceived += _ => Interlocked.Increment(ref received);

        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        var payload = CreatePayload(messageSize);
        var packetSize = GetTransmittedPacketSize(client.ClientId, payload);

        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < messageCount; i++)
        {
            await client
                .SendMessageAsync(CreateDataMessage(client.ClientId, payload), cancellationToken)
                .ConfigureAwait(false);
        }

        await WaitForCountAsync(
                () => received,
                messageCount,
                TimeSpan.FromSeconds(30),
                cancellationToken
            )
            .ConfigureAwait(false);
        stopwatch.Stop();

        return new BenchmarkResult(
            ClientCount: 1,
            SentMessages: messageCount,
            ReceivedMessages: received,
            SentBytes: (long)messageCount * messageSize,
            ReceivedBytes: (long)received * messageSize,
            TransmittedBytes: (long)messageCount * packetSize,
            Elapsed: stopwatch.Elapsed
        );
    }

    public BenchmarkResult BenchmarkSingleClient(int messageCount, int messageSize)
    {
        return BenchmarkSingleClientAsync(messageCount, messageSize).GetAwaiter().GetResult();
    }

    public async Task<BenchmarkResult> BenchmarkMultipleClientsAsync(
        int clientCount,
        int messageCount,
        int messageSize,
        CancellationToken cancellationToken = default
    )
    {
        using var server = CreateServer();
        server.Start();
        var port = GetServerPort(server);
        await WaitForServerAsync(port, cancellationToken).ConfigureAwait(false);

        var clients = new List<TcpClient>();
        var received = 0;

        try
        {
            for (var i = 0; i < clientCount; i++)
            {
                var client = CreateClient(port);
                client.MessageReceived += _ => Interlocked.Increment(ref received);
                await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
                clients.Add(client);
            }

            var payload = CreatePayload(messageSize);
            var totalSent = clientCount * messageCount;
            var stopwatch = Stopwatch.StartNew();

            var sendTasks = clients.Select(async client =>
            {
                for (var i = 0; i < messageCount; i++)
                {
                    await client
                        .SendMessageAsync(
                            CreateDataMessage(client.ClientId, payload),
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                }
            });

            await Task.WhenAll(sendTasks).ConfigureAwait(false);
            await WaitForCountAsync(
                    () => received,
                    totalSent,
                    TimeSpan.FromSeconds(60),
                    cancellationToken
                )
                .ConfigureAwait(false);
            stopwatch.Stop();

            var transmittedBytes = clients.Sum(client =>
                (long)messageCount * GetTransmittedPacketSize(client.ClientId, payload)
            );

            return new BenchmarkResult(
                ClientCount: clientCount,
                SentMessages: totalSent,
                ReceivedMessages: received,
                SentBytes: (long)totalSent * messageSize,
                ReceivedBytes: (long)received * messageSize,
                TransmittedBytes: transmittedBytes,
                Elapsed: stopwatch.Elapsed
            );
        }
        finally
        {
            foreach (var client in clients)
                client.Dispose();
        }
    }

    public BenchmarkResult BenchmarkMultipleClients(
        int clientCount,
        int messageCount,
        int messageSize
    )
    {
        return BenchmarkMultipleClientsAsync(clientCount, messageCount, messageSize)
            .GetAwaiter()
            .GetResult();
    }

    public async Task<BenchmarkResult> BenchmarkThroughputAsync(
        int durationSeconds,
        int messageSize,
        CancellationToken cancellationToken = default
    )
    {
        using var server = CreateServer();
        server.Start();
        var port = GetServerPort(server);
        await WaitForServerAsync(port, cancellationToken).ConfigureAwait(false);

        using var client = CreateClient(port);
        long receivedBytes = 0;
        client.MessageReceived += message =>
            Interlocked.Add(ref receivedBytes, message.Payload.Length);

        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);

        var payload = CreatePayload(messageSize);
        var packetSize = GetTransmittedPacketSize(client.ClientId, payload);
        long sentBytes = 0;
        long transmittedBytes = 0;
        var sentMessages = 0;
        var stopwatch = Stopwatch.StartNew();
        var endTime = stopwatch.Elapsed + TimeSpan.FromSeconds(durationSeconds);

        while (stopwatch.Elapsed < endTime && !cancellationToken.IsCancellationRequested)
        {
            await client
                .SendMessageAsync(CreateDataMessage(client.ClientId, payload), cancellationToken)
                .ConfigureAwait(false);
            sentBytes += messageSize;
            transmittedBytes += packetSize;
            sentMessages++;
        }

        stopwatch.Stop();
        await Task.Delay(300, cancellationToken).ConfigureAwait(false);

        var receivedMessages = messageSize == 0 ? 0 : (int)(receivedBytes / messageSize);

        return new BenchmarkResult(
            ClientCount: 1,
            SentMessages: sentMessages,
            ReceivedMessages: receivedMessages,
            SentBytes: sentBytes,
            ReceivedBytes: receivedBytes,
            TransmittedBytes: transmittedBytes,
            Elapsed: stopwatch.Elapsed
        );
    }

    public BenchmarkResult BenchmarkThroughput(int durationSeconds, int messageSize)
    {
        return BenchmarkThroughputAsync(durationSeconds, messageSize).GetAwaiter().GetResult();
    }

    public async Task<(double AverageMs, double MinMs, double MaxMs)> CompareLatencyAsync(
        int iterations,
        CancellationToken cancellationToken = default
    )
    {
        using var server = CreateServer();
        server.Start();
        var port = GetServerPort(server);
        await WaitForServerAsync(port, cancellationToken).ConfigureAwait(false);

        using var client = CreateClient(port);
        var latencies = new List<double>(iterations);
        var waitHandle = new ManualResetEventSlim(false);

        client.MessageReceived += message =>
        {
            if (message.MessageType == "PING" && message.Payload.Length >= sizeof(long))
            {
                var sentTicks = BitConverter.ToInt64(message.Payload, 0);
                var latency = (
                    DateTime.UtcNow - new DateTime(sentTicks, DateTimeKind.Utc)
                ).TotalMilliseconds;
                lock (latencies)
                {
                    latencies.Add(latency);
                }

                waitHandle.Set();
            }
        };

        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < iterations; i++)
        {
            waitHandle.Reset();
            var payload = BitConverter.GetBytes(DateTime.UtcNow.Ticks);
            await client
                .SendMessageAsync(
                    new NetworkMessage
                    {
                        MessageType = "PING",
                        SenderId = client.ClientId,
                        Payload = payload,
                    },
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (!waitHandle.Wait(TimeSpan.FromSeconds(5), cancellationToken))
                throw new TimeoutException("Истекло время ожидания ответа для измерения задержки.");
        }

        return (latencies.Average(), latencies.Min(), latencies.Max());
    }

    public (double AverageMs, double MinMs, double MaxMs) CompareLatency(int iterations)
    {
        return CompareLatencyAsync(iterations).GetAwaiter().GetResult();
    }

    private TcpServer CreateServer()
    {
        var port = GetFreePort();
        var server = new TcpServer(port, _host);
        server.MessageReceived += (connectionId, message) =>
        {
            if (message.MessageType is "DATA" or "PING")
            {
                var echo = new NetworkMessage
                {
                    MessageType = message.MessageType,
                    SenderId = "server",
                    Payload = message.Payload,
                };

                try
                {
                    server.SendMessageToClient(connectionId, echo);
                }
                catch (InvalidOperationException) { }
            }
        };

        return server;
    }

    private TcpClient CreateClient(int port)
    {
        return new TcpClient(_host, port)
        {
            EnableAutoReconnect = false,
            ConnectionTimeoutMs = 5000,
        };
    }

    private static byte[] CreatePayload(int messageSize)
    {
        var payload = new byte[messageSize];
        Random.Shared.NextBytes(payload);
        return payload;
    }

    private static NetworkMessage CreateDataMessage(string senderId, byte[] payload)
    {
        return new NetworkMessage
        {
            MessageType = "DATA",
            SenderId = senderId,
            Payload = payload,
        };
    }

    private static int GetTransmittedPacketSize(string senderId, byte[] payload)
    {
        return MessageProtocol.GetSerializedSize(CreateDataMessage(senderId, payload));
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static int GetServerPort(TcpServer server) => server.Port;

    private static async Task WaitForServerAsync(int port, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                using var probe = new System.Net.Sockets.TcpClient();
                await probe
                    .ConnectAsync("127.0.0.1", port, cancellationToken)
                    .ConfigureAwait(false);
                probe.Close();
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new TimeoutException($"Сервер на порту {port} не готов к подключению.");
    }

    private static async Task WaitForCountAsync(
        Func<int> getCount,
        int expected,
        TimeSpan timeout,
        CancellationToken cancellationToken
    )
    {
        var stopwatch = Stopwatch.StartNew();
        while (getCount() < expected && stopwatch.Elapsed < timeout)
        {
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }

        if (getCount() < expected)
            throw new TimeoutException($"Получено {getCount()} из {expected} сообщений.");
    }

    public void Dispose() { }
}
