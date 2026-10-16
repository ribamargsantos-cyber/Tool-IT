using AgentToolit.Collectors;
using AgentToolit.Core.Config;
using AgentToolit.Core.Execution;
using AgentToolit.Core.Services;
using AgentToolit.Core.Transfers;

namespace AgentToolit;

/// <summary>Cascas finas de BackgroundService: toda a lógica está em serviços testáveis.</summary>
internal static class Loop
{
    /// <param name="wait">Espera entre ciclos; padrão <c>Task.Delay</c>. O worker de tarefas usa a do coordenador (acorda no heartbeat).</param>
    public static async Task RunAsync(Func<CancellationToken, Task<TimeSpan>> tick, TimeProvider time, ILogger logger, string name, CancellationToken ct,
        Func<TimeSpan, CancellationToken, Task>? wait = null)
    {
        while (!ct.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                delay = await tick(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Nenhum erro inesperado derruba o serviço: registra e tenta de novo.
                logger.LogError(ex, "Falha inesperada em {Worker}; nova tentativa em 60s.", name);
                delay = TimeSpan.FromSeconds(60);
            }

            try
            {
                await (wait is null ? Task.Delay(delay, time, ct) : wait(delay, ct));
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

public sealed class HeartbeatWorker(HeartbeatService service, TimeProvider time, ILogger<HeartbeatWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Loop.RunAsync(service.TickAsync, time, logger, "heartbeat", stoppingToken);
}

/// <summary>Poll de tarefas: uma por vez; entre ciclos espera o intervalo ou o aviso do heartbeat (tasks_available).</summary>
public sealed class TaskWorker(TaskPoller poller, TaskCoordinator coordinator, TimeProvider time, ILogger<TaskWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Loop.RunAsync(poller.TickAsync, time, logger, "tasks", stoppingToken, coordinator.WaitAsync);
}

/// <summary>Varredura de .part órfão (ADR-023): na subida e a cada 6 h.</summary>
public sealed class PartSweepWorker(PartSweeper sweeper, TimeProvider time, ILogger<PartSweepWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Loop.RunAsync(ct =>
        {
            sweeper.Sweep();

            return Task.FromResult(TimeSpan.FromHours(6));
        }, time, logger, "part-sweep", stoppingToken);
}

public sealed class SenderWorker(SenderService service, TimeProvider time, ILogger<SenderWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Loop.RunAsync(service.TickAsync, time, logger, "sender", stoppingToken);
}

public sealed class MetricsWorker(CollectionService collection, AgentSettings settings, TimeProvider time, ILogger<MetricsWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Loop.RunAsync(async ct =>
        {
            await collection.CollectMetricsAsync(ct);

            return TimeSpan.FromSeconds(settings.MetricsSeconds);
        }, time, logger, "metrics", stoppingToken);
}

public sealed class InventoryWorker(CollectionService collection, AgentSettings settings, TimeProvider time, ILogger<InventoryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Pequena espera inicial: o boot do Windows ainda está concorrendo por disco/CPU (limite de carga no start).
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), time, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await Loop.RunAsync(async ct =>
        {
            await collection.CollectInventoryAsync(ct);

            return TimeSpan.FromHours(settings.InventoryHours);
        }, time, logger, "inventory", stoppingToken);
    }
}
