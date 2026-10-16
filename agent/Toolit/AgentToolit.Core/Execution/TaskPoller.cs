using AgentToolit.Core.Config;
using AgentToolit.Core.Net;
using AgentToolit.Core.Protocol;
using AgentToolit.Core.Services;
using Microsoft.Extensions.Logging;

namespace AgentToolit.Core.Execution;

/// <summary>Pede ao host que pare com código de saída ≠ 0 (a recuperação do serviço o reinicia).</summary>
public interface IAgentRestarter
{
    void RequestRestart();
}

/// <summary>
/// Um ciclo de poll de tarefas (GET /tasks/next?wait=0). Devolve quanto esperar: <see cref="AgentSettings.TaskPollSeconds"/>
/// sem tarefa, zero depois de executar uma (pode haver outra na fila), backoff em falha de rede.
/// Uma tarefa por vez; resultado não entregue por falha de rede é reenviado no próximo ciclo antes de pedir outra.
/// </summary>
public sealed class TaskPoller(
    ManagerClient client, TaskRunner runner, AgentRuntime runtime, AgentSettings settings, ILogger<TaskPoller> logger,
    IAgentRestarter? restarter = null, Backoff? backoff = null)
{
    private readonly Backoff _backoff = backoff ?? new Backoff();
    private PendingResult? _pending;

    private sealed record PendingResult(string TaskId, TaskResultRequest Result, bool RestartAgent);

    public async Task<TimeSpan> TickAsync(CancellationToken ct)
    {
        if (!runtime.IsEnrolled)
        {
            return HeartbeatService.IdleWhenNotEnrolled;
        }

        if (runtime.IsRevoked)
        {
            _pending = null;

            return HeartbeatService.IdleWhenRevoked;
        }

        try
        {
            if (_pending is { } pending)
            {
                await DeliverAsync(pending, ct);
                _backoff.Reset();

                return TimeSpan.Zero;
            }

            var task = await client.NextTaskAsync(ct);
            _backoff.Reset();
            if (task is null)
            {
                return TimeSpan.FromSeconds(settings.TaskPollSeconds);
            }

            if (string.IsNullOrWhiteSpace(task.TaskId) || string.IsNullOrWhiteSpace(task.AttemptId))
            {
                logger.LogError("Tarefa recebida sem task_id/attempt_id; ignorada.");

                return TimeSpan.FromSeconds(settings.TaskPollSeconds);
            }

            logger.LogInformation("Tarefa {TaskId} recebida: {Type} (risco {Risk}).", task.TaskId, task.Type, task.Risk);
            var run = await runner.ExecuteAsync(task, ct);

            if (run.Transient)
            {
                // ADR-023 (A): não reporta; a lease vence, o Manager recoloca e a mesma tarefa retoma do .part.
                logger.LogWarning("Tarefa {TaskId}: falha transitória; tentativa abandonada para o Manager recolocar na fila.", task.TaskId);

                return _backoff.Next();
            }

            // Guardado antes do envio: se a rede falhar, o próximo ciclo reenvia em vez de executar de novo.
            _pending = new PendingResult(task.TaskId, run.Result, run.RestartAgent);
            await DeliverAsync(_pending, ct);

            return TimeSpan.Zero;
        }
        catch (AgentRevokedException)
        {
            runtime.MarkRevoked();
            _pending = null;
            logger.LogWarning("O Manager respondeu que esta instalação foi revogada; tarefas suspensas.");

            return HeartbeatService.IdleWhenRevoked;
        }
        catch (ManagerUnavailableException ex)
        {
            var delay = ex.RetryAfter ?? _backoff.Next();
            logger.LogWarning("Manager indisponível para tarefas ({Reason}); nova tentativa em {Seconds:F0}s.", ex.Message, delay.TotalSeconds);
            RestartIfPending();

            return delay;
        }
        catch (ManagerAuthException)
        {
            var delay = _backoff.Next();
            logger.LogWarning("Manager recusou a autenticação do poll de tarefas; nova tentativa em {Seconds:F0}s.", delay.TotalSeconds);
            RestartIfPending();

            return delay;
        }
        catch (ManagerRejectedException ex)
        {
            if (_pending is { } dropped)
            {
                // Resultado recusado de forma definitiva (ex.: 422/404): reenviar o mesmo conteúdo não resolve.
                logger.LogError("Resultado da tarefa {TaskId} recusado e descartado: {Error}", dropped.TaskId, ex.Message);
                Finish(dropped);

                return TimeSpan.FromSeconds(settings.TaskPollSeconds);
            }

            logger.LogError("Poll de tarefas rejeitado: {Error}", ex.Message);

            return TimeSpan.FromSeconds(Math.Max(settings.TaskPollSeconds, 60));
        }
    }

    /// <summary>Envia o resultado; 409 task.conflict (attempt não vigente) descarta. Falha de rede propaga e mantém pendente.</summary>
    private async Task DeliverAsync(PendingResult pending, CancellationToken ct)
    {
        var accepted = await client.SendResultAsync(pending.TaskId, pending.Result, ct);
        if (!accepted)
        {
            logger.LogWarning("Resultado da tarefa {TaskId} descartado: tentativa não é mais a vigente (409).", pending.TaskId);
        }

        Finish(pending);
    }

    /// <summary>
    /// agent.restart cujo resultado não pôde ser entregue: reinicia mesmo assim (o resultado se perde e a lease vence no
    /// Manager) em vez de adiar o reinício indefinidamente enquanto o Manager estiver fora.
    /// </summary>
    private void RestartIfPending()
    {
        if (_pending is { RestartAgent: true } pending)
        {
            logger.LogWarning("Resultado do reinício da tarefa {TaskId} não entregue; reiniciando mesmo assim.", pending.TaskId);
            Finish(pending);
        }
    }

    private void Finish(PendingResult pending)
    {
        _pending = null;
        if (pending.RestartAgent)
        {
            // Só depois do envio (aceito ou descartado): o resultado não se perde com a parada.
            logger.LogWarning("Reinício do Agent solicitado pela tarefa {TaskId}; parando o serviço.", pending.TaskId);
            restarter?.RequestRestart();
        }
    }
}
