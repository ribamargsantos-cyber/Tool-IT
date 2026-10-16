namespace AgentToolit.Core.Execution;

/// <summary>Estados finais aceitos pelo Manager em POST /tasks/{id}/result.</summary>
public static class TaskResultStatus
{
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Rejected = "rejected";
    public const string Cancelled = "cancelled";

    /// <summary>
    /// SÓ interno do Agent (nunca enviado ao Manager): falha transitória (rede/Manager fora). O resultado é descartado,
    /// a lease vence e <c>tasks:expire</c> recoloca a tarefa idempotente na fila (ADR-023, opção A; L49).
    /// </summary>
    public const string Transient = "transient";
}

/// <summary>
/// O que um handler produziu. <see cref="RestartAgent"/> só é marcado por <c>agent.restart</c>: o poller envia o
/// resultado e só então pede a parada do host (a recuperação do serviço o reinicia).
/// </summary>
public sealed record TaskOutcome(string Status, int? ExitCode, string? Stdout, string? Stderr, string? Error)
{
    public bool RestartAgent { get; init; }

    public static TaskOutcome Completed(string? stdout) => new(TaskResultStatus.Completed, 0, stdout, null, null);

    public static TaskOutcome Failed(string error) => new(TaskResultStatus.Failed, null, null, null, error);

    public static TaskOutcome Rejected(string error) => new(TaskResultStatus.Rejected, null, null, null, error);

    public static TaskOutcome TransientFailure(string error) => new(TaskResultStatus.Transient, null, null, null, error);

    public static TaskOutcome Cancelled(string? error = null) => new(TaskResultStatus.Cancelled, null, null, null, error);
}
