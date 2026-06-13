using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lab_13;

public sealed class WorkerClusterHostedService : IHostedService, IAsyncDisposable
{
    private const string Host = "127.0.0.1";
    private const int BasePort = 5100;
    private readonly WorkerRegistry registry;
    private readonly CompressionAlgorithmRegistry algorithms;
    private readonly ILoggerFactory loggerFactory;
    private readonly Dictionary<string, WorkerNodeServer> servers = new(
        StringComparer.OrdinalIgnoreCase
    );
    private readonly SemaphoreSlim gate = new(1, 1);
    private CancellationTokenSource? lifetimeCancellation;

    public WorkerClusterHostedService(
        WorkerRegistry registry,
        CompressionAlgorithmRegistry algorithms,
        ILoggerFactory loggerFactory
    )
    {
        this.registry = registry;
        this.algorithms = algorithms;
        this.loggerFactory = loggerFactory;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        for (var index = 1; index <= 4; index++)
        {
            var id = $"worker-{index}";
            await StartWorkerAsync(id, cancellationToken);
        }
    }

    public async Task<(bool Success, string Message)> StopWorkerAsync(
        string workerId,
        CancellationToken cancellationToken = default
    )
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!TryGetWorkerIndex(workerId, out _))
            {
                return (false, $"Воркер {workerId} не найден.");
            }

            if (!servers.Remove(workerId, out var server))
            {
                return (false, $"Воркер {workerId} уже остановлен.");
            }

            await server.DisposeAsync();
            registry.MarkHeartbeat(workerId, false);
            return (true, $"Воркер {workerId} остановлен.");
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<(bool Success, string Message)> StartWorkerAsync(
        string workerId,
        CancellationToken cancellationToken = default
    )
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!TryGetWorkerIndex(workerId, out var index))
            {
                return (false, $"Воркер {workerId} не найден.");
            }

            if (servers.ContainsKey(workerId))
            {
                return (false, $"Воркер {workerId} уже запущен.");
            }

            var port = BasePort + index;
            registry.AddOrUpdate(workerId, Host, port);

            var descriptor = new WorkerDescriptor(
                workerId,
                Host,
                port,
                true,
                DateTimeOffset.UtcNow,
                0,
                0,
                0
            );
            var server = new WorkerNodeServer(
                descriptor,
                algorithms,
                loggerFactory.CreateLogger($"WorkerNodeServer.{workerId}")
            );

            // При восстановлении создается новый TcpListener, потому что остановленный listener нельзя безопасно переиспользовать.
            server.Start(lifetimeCancellation?.Token ?? cancellationToken);
            servers[workerId] = server;
            registry.MarkHeartbeat(workerId, true);
            return (true, $"Воркер {workerId} запущен.");
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (lifetimeCancellation is not null)
        {
            await lifetimeCancellation.CancelAsync();
        }

        foreach (var server in servers.Values)
        {
            await server.DisposeAsync();
        }
        servers.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        if (lifetimeCancellation is not null)
        {
            await lifetimeCancellation.CancelAsync();
            lifetimeCancellation.Dispose();
        }

        foreach (var server in servers.Values)
        {
            await server.DisposeAsync();
        }
        servers.Clear();
        gate.Dispose();
    }

    private static bool TryGetWorkerIndex(string workerId, out int index)
    {
        index = 0;

        if (!workerId.StartsWith("worker-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var suffix = workerId["worker-".Length..];
        return int.TryParse(suffix, out index) && index is >= 1 and <= 4;
    }
}

public sealed class WorkerMonitorHostedService : BackgroundService
{
    private readonly WorkerRegistry registry;
    private readonly TcpCompressionClient client;
    private readonly ILogger<WorkerMonitorHostedService> logger;

    public WorkerMonitorHostedService(
        WorkerRegistry registry,
        TcpCompressionClient client,
        ILogger<WorkerMonitorHostedService> logger
    )
    {
        this.registry = registry;
        this.client = client;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var workers = registry.GetSnapshot();
            var checks = workers.Select(worker => CheckWorkerAsync(worker, stoppingToken));

            // Heartbeat выполняется параллельно через TPL, чтобы медленный узел не задерживал остальные проверки.
            await Task.WhenAll(checks);
        }
    }

    private async Task CheckWorkerAsync(
        WorkerDescriptor worker,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var isAlive = await client.SendHeartbeatAsync(worker, cancellationToken);
            registry.MarkHeartbeat(worker.Id, isAlive);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            registry.MarkHeartbeat(worker.Id, false);
            logger.LogWarning(ex, "Heartbeat не прошел для {WorkerId}.", worker.Id);
        }
    }
}
