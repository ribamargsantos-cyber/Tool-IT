using AgentToolit;
using AgentToolit.Collectors;
using AgentToolit.Collectors.Inventory;
using AgentToolit.Collectors.Metrics;
using AgentToolit.Collectors.Wmi;
using AgentToolit.Core.Config;
using AgentToolit.Core.Execution;
using AgentToolit.Core.Execution.Handlers;
using AgentToolit.Core.Execution.Windows;
using AgentToolit.Core.Host;
using AgentToolit.Core.Identity;
using AgentToolit.Core.Logging;
using AgentToolit.Core.Net;
using AgentToolit.Core.Queue;
using AgentToolit.Core.Services;
using AgentToolit.Core.Transfers;

// Comandos administrativos (instalador/operador). Sem argumentos: executa como serviço do Windows.
Console.OutputEncoding = new System.Text.UTF8Encoding(false);

switch (args.FirstOrDefault()?.ToLowerInvariant())
{
    case "enroll":
        return await Commands.EnrollAsync(args[1..]);
    case "status":
        return Commands.Status();
    case "service":
        return await ServiceCommands.RunAsync(args[1..]);
    case "rotate-key":
        return await Commands.RotateKeyAsync();
    case "--version":
        Console.WriteLine(Commands.Version);

        return 0;
}


var paths = Commands.Paths();
paths.EnsureCreated(restrictAcl: Environment.GetEnvironmentVariable("TOOLIT_AGENT_DATA") is null); // ACL só se elevado/SYSTEM

var settings = new AgentSettingsStore(paths).Load();

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Services.AddWindowsService(options => options.ServiceName = "ToolitAgent");

builder.Logging.ClearProviders();
builder.Logging.AddProvider(new FileLoggerProvider(paths.LogDirectory, settings.LogRetentionDays));
if (Environment.UserInteractive)
{
    builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
}

builder.Logging.AddEventLog(o => o.SourceName = "ToolitAgent");
builder.Logging.AddFilter<Microsoft.Extensions.Logging.EventLog.EventLogLoggerProvider>(level => level >= LogLevel.Warning);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(new AgentStateStore(paths));
builder.Services.AddSingleton<AgentRuntime>();
builder.Services.AddSingleton(_ => new OutboundQueue(paths.QueueFile, settings.QueueMaxItems, settings.QueueMaxBytes));

// Identidade: sem chave local o serviço sobe, mas fica ocioso até o enrollment (e avisa no log).
builder.Services.AddSingleton(sp =>
    AgentIdentity.TryLoad(paths) ?? AgentIdentity.Generate());

builder.Services.AddSingleton(sp =>
{
    if (!ServerUrlPolicy.TryValidate(settings.ServerUrl, out var uri, out var error))
    {
        sp.GetRequiredService<ILogger<AgentRuntime>>().LogWarning("Servidor não configurado ou inválido ({Error}); execute 'enroll'.", error);
        uri = new Uri("https://invalid.local/");
    }

    var http = new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(settings.RequestTimeoutSeconds) };

    return new ManagerClient(http, sp.GetRequiredService<AgentIdentity>(), sp.GetRequiredService<AgentRuntime>(),
        sp.GetRequiredService<TimeProvider>(), Commands.Version);
});

builder.Services.AddSingleton<IWmiQuery, WmiQuery>();
builder.Services.AddSingleton<ISystemProbe, WindowsSystemProbe>();
builder.Services.AddSingleton<ISystemProbeMemory, WindowsMemoryProbe>();
builder.Services.AddSingleton<IHostFacts, WindowsHostFacts>();
builder.Services.AddSingleton<IUninstallSource, RegistryUninstallSource>();
builder.Services.AddSingleton<ISectionCollector, SystemCollector>();
builder.Services.AddSingleton<ISectionCollector, HardwareCollector>();
builder.Services.AddSingleton<ISectionCollector, SoftwareCollector>();
builder.Services.AddSingleton<ISectionCollector, NetworkCollector>();
builder.Services.AddSingleton<ISectionCollector, PeripheralCollector>();
builder.Services.AddSingleton<InventoryCollector>();
builder.Services.AddSingleton<MetricsCollector>();
builder.Services.AddSingleton<CollectionService>();

// Tarefas remotas: só os tipos do catálogo local, sobre APIs .NET (nunca cmd/PowerShell).
builder.Services.AddSingleton<IProcessApi, WindowsProcessApi>();
builder.Services.AddSingleton<IServiceApi, WindowsServiceApi>();
builder.Services.AddSingleton<IProtectedProcessSource, SecurityCenterAntivirusSource>();
builder.Services.AddSingleton(sp => new ProtectedProcessList(sp.GetRequiredService<IProtectedProcessSource>()));
builder.Services.AddSingleton<IInventoryRefresher, CollectionInventoryRefresher>();
builder.Services.AddSingleton<IAgentRestarter>(sp =>
    new AgentRestarter(sp.GetRequiredService<IHostApplicationLifetime>(), sp.GetRequiredService<ILogger<AgentRestarter>>()));
builder.Services.AddSingleton<ITaskHandler, SystemInfoHandler>();
builder.Services.AddSingleton<ITaskHandler, ProcessListHandler>();
builder.Services.AddSingleton<ITaskHandler, ServiceListHandler>();
builder.Services.AddSingleton<ITaskHandler, ProcessKillHandler>();
builder.Services.AddSingleton<ITaskHandler, InventoryRefreshHandler>();
builder.Services.AddSingleton<ITaskHandler, AgentRestartHandler>();
// Transferência de arquivos: metadados e chunks pelas rotas assinadas; caminhos só pelo PathGuard.
builder.Services.AddSingleton<ITransferApi>(sp => sp.GetRequiredService<ManagerClient>());
builder.Services.AddSingleton<IDiskSpace, SystemDiskSpace>();
// Raízes seguras calculadas UMA vez (ACL e conferência pelo handle); os handlers usam só este registro.
builder.Services.AddSingleton(sp => TransferRootRegistry.Build(
    sp.GetRequiredService<AgentSettings>(), sp.GetRequiredService<ILoggerFactory>().CreateLogger("AgentToolit.Transfers")));
builder.Services.AddSingleton<ITaskHandler, FileSendHandler>();
builder.Services.AddSingleton<ITaskHandler, FileFetchHandler>();
builder.Services.AddSingleton<ITaskProgressSink, ManagerProgressSink>();
foreach (var type in new[] { "service.start", "service.stop", "service.restart" })
{
    builder.Services.AddSingleton<ITaskHandler>(sp => new ServiceControlHandler(type, sp.GetRequiredService<IServiceApi>()));
}

builder.Services.AddSingleton(sp => new TaskCoordinator(sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<TaskRunner>();
builder.Services.AddSingleton(sp => new TaskPoller(
    sp.GetRequiredService<ManagerClient>(), sp.GetRequiredService<TaskRunner>(), sp.GetRequiredService<AgentRuntime>(),
    sp.GetRequiredService<AgentSettings>(), sp.GetRequiredService<ILogger<TaskPoller>>(), sp.GetRequiredService<IAgentRestarter>()));

builder.Services.AddSingleton(sp => new HeartbeatService(
    sp.GetRequiredService<ManagerClient>(), sp.GetRequiredService<AgentRuntime>(), sp.GetRequiredService<AgentSettings>(),
    sp.GetRequiredService<IHostFacts>(), sp.GetRequiredService<OutboundQueue>(), sp.GetRequiredService<ILogger<HeartbeatService>>(),
    tasks: sp.GetRequiredService<TaskCoordinator>()));
builder.Services.AddSingleton<SenderService>();

builder.Services.AddHostedService<HeartbeatWorker>();
builder.Services.AddHostedService<MetricsWorker>();
builder.Services.AddHostedService<InventoryWorker>();
builder.Services.AddHostedService<SenderWorker>();
builder.Services.AddHostedService<TaskWorker>();
builder.Services.AddSingleton<PartSweeper>();
builder.Services.AddHostedService<PartSweepWorker>();

var host = builder.Build();
host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AgentToolit")
    .LogInformation("Toolit Agent {Version} iniciado (registrado={Enrolled}).", Commands.Version, host.Services.GetRequiredService<AgentRuntime>().IsEnrolled);
if (!PathGuard.NormalizationAvailable)
{
    host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AgentToolit")
        .LogWarning("Normalização Unicode indisponível neste sistema: transferências de arquivo serão recusadas.");
}

// Raízes de transferência: restringe a pasta-mãe e calcula o registro de raízes seguras (ACL só SYSTEM/Administradores,
// aplicada e conferida pelo handle quando elevado). Nunca derruba o serviço.
{
    var transferLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AgentToolit.Transfers");
    try
    {
        TransferRootRegistry.RestrictParent(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Toolit"), transferLogger);
        var registry = host.Services.GetRequiredService<TransferRootRegistry>();   // dispara o Build (log das raízes)
        transferLogger.LogInformation("Transferências: {Send} raiz(es) de envio, {Fetch} de recuperação ativas.",
            registry.SendRoots.Count, registry.FetchRoots.Count);
    }
    catch (Exception ex)
    {
        transferLogger.LogError(ex, "Falha ao preparar as pastas de transferência.");
    }
}

await host.RunAsync();

return 0;
