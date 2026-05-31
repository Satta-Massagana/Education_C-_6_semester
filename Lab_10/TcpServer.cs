using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Lab10;

public sealed class TcpClientWrapper : IDisposable
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _linkedCts;
    private bool _disposed;

    public TcpClientWrapper(
        string clientId,
        System.Net.Sockets.TcpClient tcpClient,
        CancellationToken serverToken
    )
    {
        ClientId = clientId;
        TcpClient = tcpClient;
        Stream = tcpClient.GetStream();
        _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
    }

    public string ClientId { get; }
    public System.Net.Sockets.TcpClient TcpClient { get; }
    public NetworkStream Stream { get; }
    public CancellationToken Token => _linkedCts.Token;

    public async Task SendMessageAsync(NetworkMessage message, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await MessageProtocol
                .WriteMessageAsync(Stream, message, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void Cancel()
    {
        if (!_linkedCts.IsCancellationRequested)
            _linkedCts.Cancel();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Cancel();

        try
        {
            Stream.Dispose();
        }
        catch (IOException) { }

        try
        {
            TcpClient.Close();
            TcpClient.Dispose();
        }
        catch (IOException) { }

        _sendLock.Dispose();
        _linkedCts.Dispose();
    }
}

public sealed class TcpServer : IDisposable
{
    private readonly int _port;
    private readonly string _host;
    private readonly SemaphoreSlim _connectionSemaphore;
    private readonly object _clientsLock = new();
    private readonly ConcurrentDictionary<string, TcpClientWrapper> _connectedClients = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoopTask;
    private bool _disposed;

    public TcpServer(int port, string host = "127.0.0.1", int maxConnections = 100)
    {
        _port = port;
        _host = host;
        _connectionSemaphore = new SemaphoreSlim(maxConnections, maxConnections);
    }

    public int Port => _port;

    public Dictionary<string, TcpClientWrapper> ConnectedClients
    {
        get
        {
            lock (_clientsLock)
            {
                return _connectedClients.ToDictionary(
                    static pair => pair.Key,
                    static pair => pair.Value
                );
            }
        }
    }

    public event Action<string>? ClientConnected;
    public event Action<string>? ClientDisconnected;
    public event Action<string, NetworkMessage>? MessageReceived;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_listener is not null)
            throw new InvalidOperationException("Сервер уже запущен.");

        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Parse(_host), _port);
        _listener.Start();
        _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        if (_listener is null)
            return;

        _cts?.Cancel();

        try
        {
            _listener.Stop();
        }
        catch (SocketException) { }

        List<TcpClientWrapper> clients;
        lock (_clientsLock)
        {
            clients = _connectedClients.Values.ToList();
            _connectedClients.Clear();
        }

        foreach (var client in clients)
            client.Dispose();

        try
        {
            _acceptLoopTask?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException) { }

        _listener = null;
        _acceptLoopTask = null;
        _cts?.Dispose();
        _cts = null;
    }

    public async Task BroadcastMessageAsync(
        NetworkMessage message,
        CancellationToken cancellationToken = default
    )
    {
        List<TcpClientWrapper> clients;
        lock (_clientsLock)
        {
            clients = _connectedClients.Values.ToList();
        }

        foreach (var client in clients)
        {
            try
            {
                await client.SendMessageAsync(message, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
                when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                RemoveClient(client.ClientId);
            }
        }
    }

    public void BroadcastMessage(NetworkMessage message)
    {
        BroadcastMessageAsync(message).GetAwaiter().GetResult();
    }

    public async Task SendMessageToClientAsync(
        string clientId,
        NetworkMessage message,
        CancellationToken cancellationToken = default
    )
    {
        if (!_connectedClients.TryGetValue(clientId, out var client))
            throw new InvalidOperationException($"Клиент '{clientId}' не найден.");

        try
        {
            await client.SendMessageAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
            when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            RemoveClient(clientId);
            throw;
        }
    }

    public void SendMessageToClient(string clientId, NetworkMessage message)
    {
        SendMessageToClientAsync(clientId, message).GetAwaiter().GetResult();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is not null)
        {
            try
            {
                await _connectionSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                var tcpClient = await _listener
                    .AcceptTcpClientAsync(cancellationToken)
                    .ConfigureAwait(false);
                _ = HandleClientAsync(tcpClient, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
            }
            catch (Exception)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                _connectionSemaphore.Release();
            }
        }
    }

    private async Task HandleClientAsync(
        System.Net.Sockets.TcpClient tcpClient,
        CancellationToken serverToken
    )
    {
        var clientId = Guid.NewGuid().ToString("N");
        TcpClientWrapper? wrapper = null;

        try
        {
            tcpClient.NoDelay = true;
            wrapper = new TcpClientWrapper(clientId, tcpClient, serverToken);

            lock (_clientsLock)
            {
                _connectedClients[clientId] = wrapper;
            }

            ClientConnected?.Invoke(clientId);

            while (!wrapper.Token.IsCancellationRequested)
            {
                var packet = await MessageProtocol
                    .ReadMessageAsync(wrapper.Stream, wrapper.Token)
                    .ConfigureAwait(false);
                var message = MessageProtocol.Deserialize(packet);
                MessageReceived?.Invoke(clientId, message);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (InvalidDataException) { }
        catch (SocketException) { }
        finally
        {
            _connectionSemaphore.Release();
            RemoveClient(clientId, wrapper);
        }
    }

    private void RemoveClient(string clientId, TcpClientWrapper? wrapper = null)
    {
        lock (_clientsLock)
        {
            if (_connectedClients.TryRemove(clientId, out var existing))
            {
                existing.Dispose();
                ClientDisconnected?.Invoke(clientId);
                return;
            }
        }

        wrapper?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Stop();
        _connectionSemaphore.Dispose();
    }
}
