using System.Net.Sockets;

namespace Lab10;

public sealed class TcpClient : IDisposable
{
    private readonly string _serverAddress;
    private readonly int _port;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private System.Net.Sockets.TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private bool _disposed;
    private bool _isConnected;

    public TcpClient(string serverAddress, int port)
    {
        _serverAddress = serverAddress;
        _port = port;
    }

    public string ClientId { get; private set; } = Guid.NewGuid().ToString("N");

    public bool EnableAutoReconnect { get; set; } = true;

    public int MaxReconnectAttempts { get; set; } = 3;

    public int ConnectionTimeoutMs { get; set; } = 5000;

    public bool IsConnected => _isConnected && _tcpClient?.Connected == true;

    public event Action? Connected;
    public event Action? Disconnected;
    public event Action<NetworkMessage>? MessageReceived;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsConnected)
            return;

        var attempts = 0;
        var maxAttempts = EnableAutoReconnect ? MaxReconnectAttempts : 1;

        while (attempts < maxAttempts)
        {
            attempts++;

            try
            {
                await InternalConnectAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is SocketException or TimeoutException or IOException)
            {
                CleanupConnection();

                if (attempts >= maxAttempts)
                    throw new IOException(
                        $"Не удалось подключиться к {_serverAddress}:{_port} после {attempts} попыток.",
                        ex
                    );

                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempts), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    public void Connect()
    {
        ConnectAsync().GetAwaiter().GetResult();
    }

    public void Disconnect()
    {
        CleanupConnection();
        Disconnected?.Invoke();
    }

    public async Task SendMessageAsync(
        NetworkMessage message,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsConnected || _stream is null)
            throw new InvalidOperationException("Клиент не подключен к серверу.");

        if (!MessageProtocol.Validate(message))
            throw new ArgumentException("Сообщение не прошло валидацию.", nameof(message));

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await MessageProtocol
                .WriteMessageAsync(_stream, message, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            HandleConnectionLost();
            throw;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void SendMessage(NetworkMessage message)
    {
        SendMessageAsync(message).GetAwaiter().GetResult();
    }

    private async Task InternalConnectAsync(CancellationToken cancellationToken)
    {
        _tcpClient = new System.Net.Sockets.TcpClient { NoDelay = true };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ConnectionTimeoutMs);

        await _tcpClient
            .ConnectAsync(_serverAddress, _port, timeoutCts.Token)
            .ConfigureAwait(false);

        _stream = _tcpClient.GetStream();
        _cts = new CancellationTokenSource();
        _isConnected = true;

        _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));

        var hello = new NetworkMessage
        {
            MessageType = "HELLO",
            SenderId = ClientId,
            Payload = Array.Empty<byte>(),
        };

        await MessageProtocol.WriteMessageAsync(_stream, hello, _cts.Token).ConfigureAwait(false);
        Connected?.Invoke();
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _stream is not null)
            {
                var packet = await MessageProtocol
                    .ReadMessageAsync(_stream, cancellationToken)
                    .ConfigureAwait(false);
                var message = MessageProtocol.Deserialize(packet);
                MessageReceived?.Invoke(message);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException)
        {
            HandleConnectionLost();
        }
        catch (SocketException)
        {
            HandleConnectionLost();
        }
        catch (InvalidDataException)
        {
            HandleConnectionLost();
        }
    }

    private void HandleConnectionLost()
    {
        if (!_isConnected)
            return;

        CleanupConnection();
        Disconnected?.Invoke();

        if (EnableAutoReconnect && !_disposed)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await ConnectAsync().ConfigureAwait(false);
                }
                catch (IOException) { }
            });
        }
    }

    private void CleanupConnection()
    {
        _isConnected = false;
        _cts?.Cancel();

        try
        {
            _receiveTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException) { }

        try
        {
            _stream?.Dispose();
        }
        catch (IOException) { }

        try
        {
            _tcpClient?.Close();
            _tcpClient?.Dispose();
        }
        catch (IOException) { }

        _stream = null;
        _tcpClient = null;
        _receiveTask = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        EnableAutoReconnect = false;
        CleanupConnection();
        _sendLock.Dispose();
    }
}
