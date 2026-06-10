using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Lab_11;

// Узел кластера: слушает TCP-порт, хранит подключения к соседним узлам и передает входящие сообщения наверх.
public sealed class MpiNode
{
    private readonly TcpListener _listener;

    // ConnectedNodes читается и изменяется из разных фоновых задач, поэтому доступ защищается lock-ом.
    private readonly object _connectionsLock = new();
    private CancellationTokenSource? _stopTokenSource;

    public int NodeId { get; }
    public int Port { get; }

    // Список активных соединений с другими узлами. Ключ — rank удаленного узла.
    public Dictionary<int, TcpClientWrapper> ConnectedNodes { get; } = new();

    public int ConnectionCount
    {
        get
        {
            lock (_connectionsLock)
            {
                return ConnectedNodes.Count;
            }
        }
    }

    // Событие вызывается, когда сообщение дошло до текущего узла.
    public event Action<MpiMessage>? MessageReceived;

    public MpiNode(int nodeId, int port)
    {
        NodeId = nodeId;
        Port = port;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Server.SetSocketOption(
            SocketOptionLevel.Socket,
            SocketOptionName.ReuseAddress,
            true
        );
    }

    // Запускает прослушивание входящих TCP-подключений.
    public void Start()
    {
        if (_stopTokenSource is not null)
        {
            return;
        }

        _stopTokenSource = new CancellationTokenSource();
        _listener.Start();

        // Цикл принятия соединений работает в фоне, чтобы основной поток мог запускать тесты.
        _ = Task.Run(() => AcceptLoopAsync(_stopTokenSource.Token));
    }

    // Останавливает узел и закрывает все TCP-соединения.
    public void Stop()
    {
        _stopTokenSource?.Cancel();
        _stopTokenSource = null;

        try
        {
            _listener.Stop();
        }
        catch (SocketException)
        {
            // Узел уже остановлен, дополнительных действий не требуется.
        }

        lock (_connectionsLock)
        {
            foreach (var wrapper in ConnectedNodes.Values)
            {
                wrapper.Dispose();
            }

            ConnectedNodes.Clear();
        }
    }

    // Подключается к удаленному узлу. При временной недоступности порта делает несколько попыток.
    public void ConnectToNode(int remoteNodeId, string remoteAddress, int remotePort)
    {
        ConnectToNodeAsync(remoteNodeId, remoteAddress, remotePort).GetAwaiter().GetResult();
    }

    // Обрабатывает входящее сообщение: локальные сообщения отдает коммуникатору, остальные маршрутизирует.
    public void ProcessMessage(MpiMessage message)
    {
        if (message.DestinationRank == NodeId)
        {
            // Сообщение дошло до адресата: дальше его обработает MpiCommunicator этого узла.
            MessageReceived?.Invoke(message);
            return;
        }

        // Если адресат другой, пытаемся переслать сообщение по известному TCP-соединению.
        RouteMessage(message);
    }

    // Пересылает сообщение дальше, если текущий узел не является конечным получателем.
    public void RouteMessage(MpiMessage message)
    {
        TcpClientWrapper wrapper;

        lock (_connectionsLock)
        {
            if (!ConnectedNodes.TryGetValue(message.DestinationRank, out wrapper!))
            {
                throw new InvalidOperationException(
                    $"Узел {NodeId} не знает маршрут к узлу {message.DestinationRank}."
                );
            }
        }

        wrapper.SendAsync(message).GetAwaiter().GetResult();
    }

    public Task SendMessageAsync(MpiMessage message, CancellationToken cancellationToken = default)
    {
        TcpClientWrapper wrapper;

        lock (_connectionsLock)
        {
            if (!ConnectedNodes.TryGetValue(message.DestinationRank, out wrapper!))
            {
                throw new InvalidOperationException(
                    $"Нет подключения от узла {NodeId} к узлу {message.DestinationRank}."
                );
            }
        }

        // Фактическая запись в NetworkStream выполняется внутри TcpClientWrapper.
        return wrapper.SendAsync(message, cancellationToken);
    }

    private async Task ConnectToNodeAsync(int remoteNodeId, string remoteAddress, int remotePort)
    {
        lock (_connectionsLock)
        {
            if (ConnectedNodes.ContainsKey(remoteNodeId))
            {
                return;
            }
        }

        Exception? lastError = null;

        for (var attempt = 1; attempt <= 20; attempt++)
        {
            var client = new TcpClient();

            try
            {
                // Каждая попытка подключения ограничена таймаутом, чтобы ошибка узла не зависла навсегда.
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await client.ConnectAsync(remoteAddress, remotePort, timeout.Token);
                // Сразу после подключения отправляем свой NodeId, чтобы принимающая сторона знала, кто подключился.
                await TcpClientWrapper.WriteNodeIdAsync(client.GetStream(), NodeId, timeout.Token);

                var wrapper = new TcpClientWrapper(remoteNodeId, client);
                RegisterConnection(remoteNodeId, wrapper);
                // На каждом соединении запускается отдельный read-loop для приема сообщений.
                wrapper.StartReadLoop(
                    message => Task.Run(() => ProcessMessage(message)),
                    _stopTokenSource?.Token ?? CancellationToken.None
                );
                return;
            }
            catch (Exception ex)
                when (ex is SocketException or OperationCanceledException or IOException)
            {
                lastError = ex;
                client.Dispose();
                // Узел мог еще не успеть открыть порт, поэтому перед следующей попыткой немного ждем.
                await Task.Delay(100);
            }
        }

        throw new InvalidOperationException(
            $"Не удалось подключить узел {NodeId} к узлу {remoteNodeId} на {remoteAddress}:{remotePort}.",
            lastError
        );
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                // Первые 4 байта в новом соединении — идентификатор удаленного узла.
                var remoteNodeId = await TcpClientWrapper.ReadNodeIdAsync(
                    client.GetStream(),
                    cancellationToken
                );
                var wrapper = new TcpClientWrapper(remoteNodeId, client);

                RegisterConnection(remoteNodeId, wrapper);
                // После регистрации начинаем читать сообщения из этого соединения.
                wrapper.StartReadLoop(
                    message => Task.Run(() => ProcessMessage(message)),
                    cancellationToken
                );
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
                Console.WriteLine($"[node {NodeId}] Ошибка приема подключения: {ex.Message}");
            }
        }
    }

    private void RegisterConnection(int remoteNodeId, TcpClientWrapper wrapper)
    {
        lock (_connectionsLock)
        {
            if (ConnectedNodes.Remove(remoteNodeId, out var oldWrapper))
            {
                // Если соединение с этим узлом уже было, заменяем его новым и закрываем старое.
                oldWrapper.Dispose();
            }

            ConnectedNodes[remoteNodeId] = wrapper;
        }
    }
}

// Небольшая обертка над TcpClient: реализует length-prefix протокол и защищает запись lock-ом.
public sealed class TcpClientWrapper : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public int RemoteNodeId { get; }

    public TcpClientWrapper(int remoteNodeId, TcpClient client)
    {
        RemoteNodeId = remoteNodeId;
        _client = client;
        _stream = client.GetStream();
    }

    public async Task SendAsync(MpiMessage message, CancellationToken cancellationToken = default)
    {
        // Сообщение сериализуется в JSON, а byte[] Payload внутри JSON кодируется стандартным base64.
        var json = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        // Перед JSON отправляем 4 байта длины, чтобы получатель понимал границы сообщений в TCP-потоке.
        var length = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(json.Length));

        // На одно TCP-соединение могут писать разные задачи, поэтому запись делаем последовательно.
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _stream.WriteAsync(length, cancellationToken);
            await _stream.WriteAsync(json, cancellationToken);
            await _stream.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void StartReadLoop(Func<MpiMessage, Task> onMessage, CancellationToken cancellationToken)
    {
        // Цикл чтения живет в фоне, пока соединение не закрыто или узел не остановлен.
        _ = Task.Run(
            async () =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        var lengthBuffer = new byte[4];
                        // Сначала читаем длину JSON-сообщения.
                        if (!await ReadExactlyAsync(_stream, lengthBuffer, cancellationToken))
                        {
                            break;
                        }

                        var length = IPAddress.NetworkToHostOrder(
                            BitConverter.ToInt32(lengthBuffer)
                        );
                        if (length <= 0)
                        {
                            throw new InvalidDataException(
                                "Получена некорректная длина сообщения."
                            );
                        }

                        var payloadBuffer = new byte[length];
                        // Затем дочитываем ровно length байт тела сообщения.
                        if (!await ReadExactlyAsync(_stream, payloadBuffer, cancellationToken))
                        {
                            break;
                        }

                        var message = JsonSerializer.Deserialize<MpiMessage>(
                            payloadBuffer,
                            JsonOptions
                        );
                        if (message is not null)
                        {
                            // Передаем сообщение наверх: MpiNode решит, локальное оно или его нужно маршрутизировать.
                            await onMessage(message);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (IOException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Ошибка чтения от узла {RemoteNodeId}: {ex.Message}");
                        break;
                    }
                }
            },
            cancellationToken
        );
    }

    public static async Task WriteNodeIdAsync(
        NetworkStream stream,
        int nodeId,
        CancellationToken cancellationToken
    )
    {
        // Handshake соединения: отправляем NodeId в network byte order.
        var bytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(nodeId));
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<int> ReadNodeIdAsync(
        NetworkStream stream,
        CancellationToken cancellationToken
    )
    {
        var buffer = new byte[4];
        // Принимающая сторона читает NodeId до запуска обычного read-loop сообщений.
        if (!await ReadExactlyAsync(stream, buffer, cancellationToken))
        {
            throw new IOException("Соединение закрыто до получения идентификатора узла.");
        }

        return IPAddress.NetworkToHostOrder(BitConverter.ToInt32(buffer));
    }

    private static async Task<bool> ReadExactlyAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken
    )
    {
        var offset = 0;

        // NetworkStream может вернуть меньше байт, чем запросили, поэтому читаем в цикле до заполнения буфера.
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(offset, buffer.Length - offset),
                cancellationToken
            );
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    public void Dispose()
    {
        _writeLock.Dispose();
        _stream.Dispose();
        _client.Dispose();
    }
}
