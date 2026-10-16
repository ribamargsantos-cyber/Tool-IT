using System.Net;
using System.Text.Json;
using AgentToolit.Core.Config;
using AgentToolit.Core.Execution;
using AgentToolit.Core.Net;
using AgentToolit.Core.Services;
using AgentToolit.Core.Transfers;
using AgentToolit.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentToolit.Tests;

/// <summary>
/// ADR-023 (L49): falha transitória não é reportada (a lease vence e o Manager recoloca; o .part permanece e a retomada
/// continua dele); falha definitiva continua failed; .part órfão antigo é varrido pelo handle.
/// </summary>
public class TransferResumeTests
{
    private const string TaskId = "6f1c1d0e-8b7a-4c55-9a3e-2d4f5a6b7c80";
    private const string AttemptId = "0b9e6a52-3c1d-4e7f-8a21-9d5c4b3a2f10";
    private const string FileParams = "{\"transfer_id\":\"" + FileHandlersTests.TransferId + "\"}";

    private sealed class ThrowingHandler(Exception error) : ITaskHandler
    {
        public string Type => "file.send";

        public int Calls;

        public Task<TaskOutcome> ExecuteAsync(JsonElement parameters, CancellationToken ct)
        {
            Calls++;

            throw error;
        }
    }

    private static string NextJson() =>
        "{\"task_id\":\"" + TaskId + "\",\"attempt_id\":\"" + AttemptId + "\",\"type\":\"file.send\",\"params\":" + FileParams +
        ",\"timeout_seconds\":900,\"lease_expires_at\":\"2026-10-15T12:01:30+00:00\",\"risk\":\"medium\"}";

    [Fact]
    public async Task Runner_marks_manager_unavailable_as_transient_without_a_failed_result()
    {
        var handler = new ThrowingHandler(new ManagerUnavailableException("rede"));
        var runner = new TaskRunner([handler], new TaskCoordinator(), TimeProvider.System, NullLogger<TaskRunner>.Instance);

        var run = await runner.ExecuteAsync(TaskExecutionTests.Assignment("file.send", FileParams), default);

        Assert.True(run.Transient);
        Assert.Equal(TaskResultStatus.Transient, run.Result.Status);
    }

    [Fact]
    public async Task Runner_marks_network_timeout_of_a_transfer_as_transient()
    {
        var handler = new ThrowingHandler(new TaskCanceledException("HttpClient timeout"));
        var runner = new TaskRunner([handler], new TaskCoordinator(), TimeProvider.System, NullLogger<TaskRunner>.Instance);

        var run = await runner.ExecuteAsync(TaskExecutionTests.Assignment("file.send", FileParams), default);

        Assert.True(run.Transient);
    }

    [Fact]
    public async Task Runner_keeps_permanent_errors_as_failed()
    {
        var handler = new ThrowingHandler(new InvalidOperationException("defeito"));
        var runner = new TaskRunner([handler], new TaskCoordinator(), TimeProvider.System, NullLogger<TaskRunner>.Instance);

        var run = await runner.ExecuteAsync(TaskExecutionTests.Assignment("file.send", FileParams), default);

        Assert.False(run.Transient);
        Assert.Equal(TaskResultStatus.Failed, run.Result.Status);
    }

    [Fact]
    public async Task Poller_does_not_post_a_result_for_a_transient_failure_and_backs_off()
    {
        using var dir = new TempDir();
        var (client, runtime, _, http) = Enrolled.Create(dir, r =>
            r.Method == "GET" && r.Path.EndsWith("/tasks/next")
                ? StubHandler.Json(HttpStatusCode.OK, NextJson())
                : StubHandler.Error(HttpStatusCode.NotFound, "request.not_found"));
        var handler = new ThrowingHandler(new ManagerUnavailableException("rede"));
        var runner = new TaskRunner([handler], new TaskCoordinator(), TimeProvider.System, NullLogger<TaskRunner>.Instance);
        var poller = new TaskPoller(client, runner, runtime, new AgentSettings { TaskPollSeconds = 10 }, NullLogger<TaskPoller>.Instance,
            null, new Backoff(random: new Random(1)));

        var delay = await poller.TickAsync(default);

        Assert.True(delay > TimeSpan.Zero);
        Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain(http.Requests, r => r.Method == "POST" && r.Path.EndsWith("/result"));
    }

    [Fact]
    public async Task Send_resumes_from_the_part_left_by_a_network_failure_and_completes_with_the_right_hash()
    {
        using var f = new FileHandlersTests.Fixture();
        var content = "0123456789"u8.ToArray();
        f.SetupSend(Path.Combine(f.Root, "a.txt"), content);
        f.Api.ChunkError = n => n == 2 ? new ManagerUnavailableException("rede") : null;

        await Assert.ThrowsAsync<ManagerUnavailableException>(() => f.RunSend());
        Assert.True(File.Exists(FileHandlersTests.Fixture.PartPath(f.Root)));

        f.Api.ChunkError = null;
        f.Api.ChunkRequests.Clear();
        var outcome = await f.RunSend();

        Assert.Equal(TaskResultStatus.Completed, outcome.Status);
        Assert.Equal([2], f.Api.ChunkRequests);   // só o que faltava
        Assert.Equal(content, File.ReadAllBytes(Path.Combine(f.Root, "a.txt")));
        Assert.False(File.Exists(FileHandlersTests.Fixture.PartPath(f.Root)));
    }

    [Fact]
    public async Task Send_hash_mismatch_stays_failed_and_removes_the_part()
    {
        using var f = new FileHandlersTests.Fixture();
        f.SetupSend(Path.Combine(f.Root, "a.txt"), "0123456789"u8.ToArray(), sha: new string('a', 64));

        var outcome = await f.RunSend();

        Assert.Equal(TaskResultStatus.Failed, outcome.Status);
        Assert.False(File.Exists(FileHandlersTests.Fixture.PartPath(f.Root)));
    }

    [Fact]
    public void Sweeper_removes_only_old_part_files_with_the_strict_name()
    {
        using var f = new FileHandlersTests.Fixture();
        var old = FileHandlersTests.Fixture.PartPath(f.Root);
        var fresh = Path.Combine(f.Root, ".toolit-11111111-2222-4333-8444-555555555555.part");
        var otherName = Path.Combine(f.Root, ".toolit-naoeuuid.part");
        var final = Path.Combine(f.Root, "relatorio.pdf");
        foreach (var p in new[] { old, fresh, otherName, final })
        {
            File.WriteAllText(p, "x");
        }
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-48));
        File.SetLastWriteTimeUtc(otherName, DateTime.UtcNow.AddHours(-48));
        File.SetLastWriteTimeUtc(final, DateTime.UtcNow.AddHours(-48));
        var sweeper = new PartSweeper(f.Registry(), f.Settings, TimeProvider.System, NullLogger<PartSweeper>.Instance);

        var removed = sweeper.Sweep();

        Assert.Equal(1, removed);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));       // recente: pode estar em andamento
        Assert.True(File.Exists(otherName));   // fora do padrão estrito
        Assert.True(File.Exists(final));       // arquivo final nunca é tocado
    }

    [Fact]
    public void Sweeper_skips_a_part_held_open_by_a_running_transfer()
    {
        using var f = new FileHandlersTests.Fixture();
        var old = FileHandlersTests.Fixture.PartPath(f.Root);
        File.WriteAllText(old, "x");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-48));
        using var held = new FileStream(old, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var sweeper = new PartSweeper(f.Registry(), f.Settings, TimeProvider.System, NullLogger<PartSweeper>.Instance);

        Assert.Equal(0, sweeper.Sweep());
        Assert.True(File.Exists(old));
    }
}
