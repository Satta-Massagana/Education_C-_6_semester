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
    private readonly List<WorkerNodeServer> servers = new();
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

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        for (var index = 1; index <= 4; index++)
        {
            var id = $"worker-{index}";
            var port = BasePort + index;
            registry.AddOrUpdate(id, Host, port);

            var descriptor = new WorkerDescriptor(
                id,
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
                loggerFactory.CreateLogger($"WorkerNodeServer.{id}")
            );

            server.Start(lifetimeCancellation.Token);
            servers.Add(server);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (lifetimeCancellation is not null)
        {
            await lifetimeCancellation.CancelAsync();
        }

        foreach (var server in servers)
        {
            await server.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (lifetimeCancellation is not null)
        {
            await lifetimeCancellation.CancelAsync();
            lifetimeCancellation.Dispose();
        }

        foreach (var server in servers)
        {
            await server.DisposeAsync();
        }
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
