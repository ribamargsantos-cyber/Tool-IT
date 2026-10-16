using System.Security.Cryptography;
using System.Text.Json;
using AgentToolit.Core.Config;
using AgentToolit.Core.Net;
using AgentToolit.Core.Transfers;
using Microsoft.Extensions.Logging;

namespace AgentToolit.Core.Execution.Handlers;

/// <summary>
/// Parte comum de file.send / file.fetch: falha fechada sem normalização Unicode, raízes locais, leitura e conferência
/// dos metadados (<c>transfer_id</c>, endpoint, direção, chunk) e tradução das respostas do Manager:
/// 409 → cancelled (a tarefa já não está em execução lá), outros 4xx → failed; rede/5xx/timeout de HttpClient sobem
/// (ManagerUnavailableException / OperationCanceledException): o runner os trata como falha TRANSITÓRIA, não reporta
/// resultado, e o Manager recoloca a tarefa quando a lease vence (ADR-023, L49); o .part é mantido e a retomada
/// continua dele. .part que ficar órfão é removido por <see cref="PartSweeper"/>. Cancelamento → cancelled.
/// Logs: só o id da transferência e o nome do arquivo (nunca o caminho completo vindo do Manager).
/// </summary>
public abstract class TransferHandlerBase(ITransferApi api, AgentSettings settings, TimeProvider time, AgentRuntime runtime, TransferRootRegistry rootRegistry, ILogger logger)
    : IContextTaskHandler
{
    /// <summary>Folga de progresso: envia ao mudar o percentual ou, sem mudança, a cada 15 s.</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(15);

    public abstract string Type { get; }

    protected abstract string Direction { get; }

    protected ITransferApi Api => api;

    protected ILogger Logger => logger;

    protected TimeProvider Time => time;

    protected AgentSettings Settings => settings;

    public Task<TaskOutcome> ExecuteAsync(JsonElement parameters, CancellationToken ct) => ExecuteAsync(parameters, TaskContext.None, ct);

    public async Task<TaskOutcome> ExecuteAsync(JsonElement parameters, TaskContext context, CancellationToken ct)
    {
        if (!PathGuard.NormalizationAvailable)
        {
            return TaskOutcome.Rejected(PathGuard.Message(PathGuard.NormalizationUnavailable));
        }

        var roots = RootsFor(rootRegistry);
        if (roots.Count == 0)
        {
            return TaskOutcome.Rejected("Transferências desligadas neste computador (nenhuma pasta permitida ou segura configurada).");
        }

        var id = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("transfer_id", out var raw)
            && raw.ValueKind == JsonValueKind.String ? raw.GetString() : null;
        if (!TaskSchemaValidator.IsCanonicalUuid(id))
        {
            return TaskOutcome.Rejected("invalid transfer_id");
        }

        var me = runtime.State?.EndpointId;
        if (string.IsNullOrEmpty(me))
        {
            return TaskOutcome.Rejected("Agente não registrado.");
        }

        try
        {
            var info = await api.GetAsync(id!, ct);
            if (!string.Equals(info.TransferId, id, StringComparison.Ordinal))
            {
                logger.LogWarning("Transferência {Id}: o Manager devolveu metadados de outra transferência.", id);

                return TaskOutcome.Rejected("Metadados de outra transferência.");
            }
            if (!string.Equals(info.EndpointId, me, StringComparison.Ordinal))
            {
                logger.LogWarning("Transferência {Id}: pertence a outro computador; recusada.", id);

                return TaskOutcome.Rejected("A transferência pertence a outro computador.");
            }
            if (!string.Equals(info.Direction, Direction, StringComparison.Ordinal))
            {
                return TaskOutcome.Rejected($"Direção da transferência não confere com a tarefa ({Type}).");
            }
            if (info.ChunkSize <= 0 || info.ChunkSize > TransferProtocol.MaxChunkBytes)
            {
                return TaskOutcome.Rejected("Tamanho de chunk inválido nos metadados.");
            }

            return await RunAsync(id!, info, roots, new ProgressReporter(context, time), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogInformation("Transferência {Id} cancelada.", id);

            return TaskOutcome.Cancelled("Transferência cancelada.");
        }
        catch (ManagerRejectedException ex) when (ex.Status == 409)
        {
            logger.LogInformation("Transferência {Id}: o Manager encerrou a transferência ({Code}).", id, ex.Code);

            return TaskOutcome.Cancelled($"A transferência não está mais em andamento no Manager ({ex.Code}).");
        }
        catch (ManagerRejectedException ex) when (ex.Status == 404)
        {
            return TaskOutcome.Failed("Transferência não encontrada no Manager.");
        }
        catch (ManagerRejectedException ex)
        {
            return TaskOutcome.Failed($"O Manager recusou a operação ({ex.Status} {ex.Code}).");
        }
    }

    /// <summary>Raízes seguras desta direção, do registro fixo calculado na subida.</summary>
    protected abstract IReadOnlyList<string> RootsFor(TransferRootRegistry registry);

    /// <summary>A transferência propriamente dita (metadados já conferidos).</summary>
    protected abstract Task<TaskOutcome> RunAsync(string id, TransferInfo info, IReadOnlyList<string> roots, ProgressReporter progress, CancellationToken ct);

    /// <summary>Recusa/falha de <see cref="PathGuard.OpenChecked"/>: ataque/corrida → rejected (Warning); em uso → failed.</summary>
    protected TaskOutcome OpenFailure(string id, string name, string? code, string what)
    {
        if (TransferPaths.IsSuspicious(code) || code == PathGuard.NormalizationUnavailable)
        {
            logger.LogWarning("Transferência {Id}: abertura de {Name} recusada ({Code}).", id, name, code);

            return TaskOutcome.Rejected(PathGuard.Message(code!));
        }

        return code switch
        {
            "not_found" => TaskOutcome.Failed($"{what}: não existe."),
            "exists" => TaskOutcome.Failed($"{what}: já existe (outra execução em andamento?)."),
            "open_failed" => TaskOutcome.Failed($"{what}: arquivo em uso por outro programa ou sem acesso."),
            _ => TaskOutcome.Rejected(PathGuard.Message(code ?? "open_failed")),
        };
    }

    protected static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Número de chunks de <paramref name="size"/> bytes (0 para arquivo vazio), ou null se não cabe em int.</summary>
    protected static int? ChunkCount(long size, int chunkSize)
    {
        var count = size == 0 ? 0 : (size + chunkSize - 1) / chunkSize;

        return count <= int.MaxValue ? (int)count : null;
    }

    /// <summary>Progresso 0–99 por bytes; ao mudar o percentual ou a cada 15 s. Falha nunca interrompe (exceto cancelamento).</summary>
    protected sealed class ProgressReporter(TaskContext context, TimeProvider time)
    {
        private int _last = -1;
        private DateTimeOffset _lastAt;

        public async Task ReportAsync(long done, long total, CancellationToken ct)
        {
            var percent = total <= 0 ? 0 : (int)Math.Clamp(done * 100 / total, 0, 99);
            var now = time.GetUtcNow();
            if (percent == _last && now - _lastAt < ProgressInterval)
            {
                return;
            }

            _last = percent;
            _lastAt = now;
            try
            {
                await context.ReportProgress(percent, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // progresso nunca é necessário para a correção
            }
        }
    }
}
