using System.Text.Json;
using AgentToolit.Core.Protocol;

namespace AgentToolit.Core.Config;

/// <summary>Configuração persistente (config.json). Sem segredos: token de enrollment NUNCA é gravado.</summary>
public sealed class AgentSettings
{
    public string ServerUrl { get; set; } = "";
    public int HeartbeatSeconds { get; set; } = 60;
    public int MetricsSeconds { get; set; } = 60;
    public int InventoryHours { get; set; } = 24;
    public int RequestTimeoutSeconds { get; set; } = 30;
    public int MetricsBatchSize { get; set; } = 120;
    public int QueueMaxItems { get; set; } = 5000;
    public long QueueMaxBytes { get; set; } = 20L * 1024 * 1024;
    public int LogRetentionDays { get; set; } = 14;

    private int _taskPollSeconds = 10;

    /// <summary>Intervalo do poll curto de tarefas (GET /tasks/next?wait=0). Sempre dentro de [Min, Max].</summary>
    public int TaskPollSeconds
    {
        get => _taskPollSeconds;
        set => _taskPollSeconds = Math.Clamp(value, MinTaskPollSeconds, MaxTaskPollSeconds);
    }

    /// <summary>
    /// Veto local do Agent para transferências: só dentro destas pastas (envio e recuperação). A política do Manager
    /// não chega ao Agent; o que vale aqui é esta lista. Entradas são conferidas no uso com PathGuard.ValidateDirectory
    /// (inválidas são ignoradas e registradas pelo chamador). Nulo no config.json = lista vazia (transferência desligada).
    /// </summary>
    public List<string> TransferRoots
    {
        get => _transferRoots;
        set => _transferRoots = value ?? [];
    }

    /// <summary>Pastas aceitas SÓ para recuperação (fetch), além de <see cref="TransferRoots"/>: ex. logs.</summary>
    public List<string> TransferFetchOnlyRoots
    {
        get => _transferFetchOnlyRoots;
        set => _transferFetchOnlyRoots = value ?? [];
    }

    /// <summary>Idade mínima (horas) para um <c>.toolit-{id}.part</c> órfão ser apagado pela varredura (ADR-023).</summary>
    public int TransferPartRetentionHours
    {
        get => _transferPartRetentionHours;
        set => _transferPartRetentionHours = Math.Clamp(value, 1, 24 * 30);
    }

    private int _transferPartRetentionHours = 24;

    private List<string> _transferRoots = [ProgramDataToolit("Inbox"), ProgramDataToolit("Outbox")];

    private List<string> _transferFetchOnlyRoots = [ProgramDataToolit("Logs")];

    private long _transferBytesPerSecond = DefaultTransferBytesPerSecond;

    /// <summary>Banda máxima das transferências (bytes/s). Sempre dentro de [Min, Max].</summary>
    public long TransferBytesPerSecond
    {
        get => _transferBytesPerSecond;
        set => _transferBytesPerSecond = Math.Clamp(value, MinTransferBytesPerSecond, MaxTransferBytesPerSecond);
    }

    public const long DefaultTransferBytesPerSecond = 4L * 1024 * 1024;
    public const long MinTransferBytesPerSecond = 64L * 1024;
    public const long MaxTransferBytesPerSecond = 64L * 1024 * 1024;

    /// <summary>%ProgramData%\Toolit\{name}.</summary>
    private static string ProgramDataToolit(string name) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Toolit", name);

    /// <summary>Limites mínimos: o Manager não consegue levar o Agent a consultas abusivas.</summary>
    public const int MinHeartbeatSeconds = 15;
    public const int MinMetricsSeconds = 15;
    public const int MinInventoryHours = 1;
    public const int MinTaskPollSeconds = 5;
    public const int MaxTaskPollSeconds = 300;

    public void ApplyServerConfig(ServerConfig server)
    {
        HeartbeatSeconds = Math.Max(MinHeartbeatSeconds, server.HeartbeatSeconds);
        MetricsSeconds = Math.Max(MinMetricsSeconds, server.MetricsSeconds);
        InventoryHours = Math.Max(MinInventoryHours, server.InventoryHours);
    }
}

public sealed class AgentSettingsStore(AgentPaths paths)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public AgentSettings Load()
    {
        if (!File.Exists(paths.ConfigFile))
        {
            return new AgentSettings();
        }

        return JsonSerializer.Deserialize<AgentSettings>(File.ReadAllText(paths.ConfigFile), Options) ?? new AgentSettings();
    }

    public void Save(AgentSettings settings) => AtomicFile.WriteAllText(paths.ConfigFile, JsonSerializer.Serialize(settings, Options));
}

/// <summary>Identidade pública do registro (state.json): sem segredos.</summary>
public sealed class AgentState
{
    public string AgentId { get; set; } = "";
    public string EndpointId { get; set; } = "";
    public DateTimeOffset EnrolledAt { get; set; }

    /// <summary>Servidor respondeu 403 agent.revoked: o Agent fica ocioso até um novo enrollment.</summary>
    public bool Revoked { get; set; }
}

public sealed class AgentStateStore(AgentPaths paths)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public AgentState? Load() =>
        File.Exists(paths.StateFile) ? JsonSerializer.Deserialize<AgentState>(File.ReadAllText(paths.StateFile), Options) : null;

    public void Save(AgentState state) => AtomicFile.WriteAllText(paths.StateFile, JsonSerializer.Serialize(state, Options));
}

/// <summary>Escrita atômica (arquivo temporário + troca): queda de energia não deixa JSON truncado.</summary>
internal static class AtomicFile
{
    public static void WriteAllText(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}
