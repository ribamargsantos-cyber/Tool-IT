using System.Text;
using AgentToolit.Core.Net;
using AgentToolit.Core.Protocol;
using Microsoft.Extensions.Logging;

namespace AgentToolit.Core.Execution;

/// <summary>
/// Resultado pronto para envio + se o host deve ser parado para reinício depois do envio.
/// <paramref name="Transient"/>: falha transitória — o resultado NÃO deve ser enviado (a tentativa é abandonada para
/// o Manager recolocar a tarefa na fila quando a lease vencer; ADR-023).
/// </summary>
public sealed record TaskRun(TaskResultRequest Result, bool RestartAgent, bool Transient = false);

/// <summary>
/// Executa uma tarefa por vez: valida tipo/parâmetros contra o catálogo local (fora dele → rejected sem executar),
/// aplica o timeout do tipo, aceita cancelamento via <see cref="TaskCoordinator"/> e limita a saída a 16 KiB.
/// Handler <see cref="IContextTaskHandler"/> recebe um <see cref="TaskContext"/> cujo progresso vai para
/// <paramref name="progress"/> (sem sink: progresso descartado). Falha no envio do progresso nunca derruba a tarefa;
/// 409 (tentativa/lease já não vale) cancela o handler e a tarefa termina como cancelled.
/// </summary>
public sealed class TaskRunner(
    IEnumerable<ITaskHandler> handlers, TaskCoordinator coordinator, TimeProvider time, ILogger<TaskRunner> logger,
    ITaskProgressSink? progress = null)
{
    public const int MaxOutputBytes = 16384;
    private const int MaxErrorChars = 1024;

    private readonly Dictionary<string, ITaskHandler> _handlers = handlers
        .Where(h => LocalTaskCatalog.IsKnown(h.Type))   // handler de tipo fora do catálogo nunca é alcançável
        .ToDictionary(h => h.Type, StringComparer.Ordinal);

    private readonly SemaphoreSlim _one = new(1, 1);

    public async Task<TaskResultRequest> RunAsync(TaskAssignment a, CancellationToken ct) => (await ExecuteAsync(a, ct)).Result;

    public async Task<TaskRun> ExecuteAsync(TaskAssignment a, CancellationToken ct)
    {
        await _one.WaitAsync(ct);   // concorrência 1
        try
        {
            var startedAt = time.GetUtcNow();
            var outcome = await RunOutcomeAsync(a, ct);
            var finishedAt = time.GetUtcNow();

            var (stdout, cutOut) = TruncateUtf8(outcome.Stdout, MaxOutputBytes);
            var (stderr, cutErr) = TruncateUtf8(outcome.Stderr, MaxOutputBytes);
            var error = outcome.Error is { Length: > MaxErrorChars } e ? e[..MaxErrorChars] : outcome.Error;

            var result = new TaskResultRequest(
                a.AttemptId, outcome.Status, outcome.ExitCode, stdout, stderr, cutOut || cutErr,
                startedAt, finishedAt, Math.Max(0, (long)(finishedAt - startedAt).TotalMilliseconds), error);

            logger.LogInformation("Tarefa {TaskId} ({Type}) terminou: {Status}{Error}", a.TaskId, a.Type, outcome.Status,
                error is null ? "" : " - " + error);

            return new TaskRun(result, outcome.RestartAgent && outcome.Status == TaskResultStatus.Completed,
                outcome.Status == TaskResultStatus.Transient);
        }
        finally
        {
            _one.Release();
        }
    }

    private async Task<TaskOutcome> RunOutcomeAsync(TaskAssignment a, CancellationToken ct)
    {
        var type = a.Type;
        if (!LocalTaskCatalog.IsKnown(type))
        {
            logger.LogWarning("Tarefa {TaskId} recusada: tipo {Type} fora do catálogo local.", a.TaskId, type);

            return TaskOutcome.Rejected("unknown task type");
        }

        var (ok, error) = TaskSchemaValidator.Validate(type, a.Params);
        if (!ok)
        {
            logger.LogWarning("Tarefa {TaskId} ({Type}) recusada: {Error}.", a.TaskId, type, error);

            return TaskOutcome.Rejected(error ?? "invalid params");
        }

        if (!_handlers.TryGetValue(type, out var handler))
        {
            return TaskOutcome.Rejected("task type not supported by this agent");
        }

        using var cancel = new CancellationTokenSource();
        using var lost = new CancellationTokenSource();
        using var timeout = new CancellationTokenSource(LocalTaskCatalog.EffectiveTimeout(type, a.TimeoutSeconds), time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cancel.Token, timeout.Token, lost.Token);
        using var registration = coordinator.Register(a.TaskId, cancel);

        try
        {
            // Task.Run + WaitAsync: um handler preso em API síncrona não segura o runner além do timeout.
            var work = handler is IContextTaskHandler contextual
                ? Task.Run(() => contextual.ExecuteAsync(a.Params, new TaskContext(a.TaskId, a.AttemptId, Reporter(a, lost)), linked.Token), CancellationToken.None)
                : Task.Run(() => handler.ExecuteAsync(a.Params, linked.Token), CancellationToken.None);
            var outcome = await work.WaitAsync(linked.Token);

            return outcome ?? TaskOutcome.Failed("handler returned no outcome");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // host parando: nada a enviar agora (a lease vence e o Manager decide o retry)
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return TaskOutcome.Cancelled("cancelled by manager");
        }
        catch (OperationCanceledException) when (lost.IsCancellationRequested)
        {
            return TaskOutcome.Cancelled("task no longer running on manager");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return TaskOutcome.Failed("timeout");
        }
        catch (ManagerUnavailableException ex)
        {
            // Rede/5xx/429: o que o handler já gravou (.part) é mantido; não reporta failed (seria final no Manager).
            logger.LogWarning("Tarefa {TaskId} ({Type}) interrompida por falha transitória: {Reason}", a.TaskId, a.Type, ex.Message);

            return TaskOutcome.TransientFailure(ex.Message);
        }
        catch (OperationCanceledException ex) when (a.Type is "file.send" or "file.fetch")
        {
            // Nenhum token foi cancelado: é o timeout de uma chamada HttpClient ao Manager (transitório).
            logger.LogWarning("Tarefa {TaskId} ({Type}) interrompida por timeout de rede: {Reason}", a.TaskId, a.Type, ex.Message);

            return TaskOutcome.TransientFailure("network timeout");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Tarefa {TaskId} ({Type}) falhou.", a.TaskId, type);

            return TaskOutcome.Failed(ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>
    /// Envio de progresso da tentativa: percentual limitado a 0–99; erros engolidos (registrados); 409 (tentativa
    /// encerrada/lease perdida no Manager) cancela <paramref name="lost"/> para o handler parar de gravar.
    /// </summary>
    private Func<int, CancellationToken, Task> Reporter(TaskAssignment a, CancellationTokenSource lost) => async (percent, token) =>
    {
        if (progress is null || lost.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await progress.ReportAsync(a.TaskId, a.AttemptId, Math.Clamp(percent, 0, 99), token);
        }
        catch (ManagerRejectedException ex) when (ex.Status == 409)
        {
            logger.LogWarning("Tarefa {TaskId} ({Type}): o Manager não reconhece mais esta tentativa ({Code}); interrompendo.", a.TaskId, a.Type, ex.Code);
            try
            {
                lost.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // a tarefa já terminou
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // o handler já está sendo cancelado
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Tarefa {TaskId} ({Type}): falha ao enviar progresso (ignorada).", a.TaskId, a.Type);
        }
    };

    /// <summary>Corta em no máximo <paramref name="maxBytes"/> bytes UTF-8 sem partir um caractere.</summary>
    public static (string? Text, bool Truncated) TruncateUtf8(string? text, int maxBytes)
    {
        if (text is null)
        {
            return (null, false);
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= maxBytes)
        {
            return (text, false);
        }

        var cut = maxBytes;
        while (cut > 0 && (bytes[cut] & 0xC0) == 0x80)
        {
            cut--;   // bytes[cut] é continuação (10xxxxxx): recua até o início do caractere
        }

        return (Encoding.UTF8.GetString(bytes, 0, cut), true);
    }
}
