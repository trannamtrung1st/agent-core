using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

internal static class AgentRunTestDriver
{
    // Dispatch is an ACK. Journeys explicitly wait for the admitted executions to settle.
    internal static async Task<int> ExecuteRunsAsync(this IServiceProvider services, int limit = 100)
    {
        var store = services.GetRequiredService<IAgentRunStore>();
        var time = services.GetRequiredService<TimeProvider>();
        var initial = await store.ListRunnableAsync(time.GetUtcNow(), limit);
        var dispatched = await services.GetRequiredService<AgentRunCoordinator>().ExecuteRunnableAsync(limit);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        foreach (var run in initial)
        {
            while (true)
            {
                var current = await store.GetAsync(run.Owner, run.AgentRunId, timeout.Token);
                if (current is null || current.Status is not (AgentRunStatus.Running or AgentRunStatus.Queued)) break;
                try { await Task.Delay(10, timeout.Token); }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException($"AgentRun did not settle: status={current.Status}, revision={current.Revision}, attempt={current.AttemptCount}, generation={current.Claim?.Generation}, dispatched={dispatched}.");
                }
            }
        }
        return dispatched;
    }

    internal static async Task<IReadOnlyList<AgentRun>> AutomationRunsAsync(this IServiceProvider services,
        AgentRunOwner owner, Guid automationId, int limit = 100)
    {
        var result = new List<AgentRun>();
        foreach (var run in await services.GetRequiredService<IAgentRunStore>().ListAsync(owner, limit))
            if ((await services.SessionAsync(run)).Origin.AutomationId == automationId) result.Add(run);
        return result;
    }

    internal static async Task<AgentRun> CompleteSourceAsync(this IServiceProvider services, Guid instanceId,
        string toolResult = "{\"ok\":true}", string summary = "Source succeeded")
    {
        var session = await services.GetRequiredService<AgentCore.Application.Sessions.SessionManager>().CreateForInstanceAsync(instanceId, SessionMode.Text);
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var input = new ConversationEntry(Guid.NewGuid(), 1, Guid.NewGuid(), ConversationRole.User, "Inspect current state", null,
            EntryStatus.Completed, SessionMode.Text, 0, 21, now);
        session = session with { Revision = session.Revision + 1, Entries = [input], LastEntrySequence = 1, UpdatedAt = now };
        var store = services.GetRequiredService<IAgentRunStore>();
        var run = (await store.AdmitAsync(session, session.Revision - 1, AgentRunTestFixtures.Run(session, now))).Run;
        run = await store.ApplyAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Claim(run.Revision, now, Guid.NewGuid(), now.AddMinutes(3)));
        var payload = AgentRunToolCallCheckpoint.Write([new(ModelRole.Tool, toolResult, ToolCallId: "read", Name: "http.request")]);
        run = await store.ApplyAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Checkpoint(run.Revision, now, run.Claim!.Generation, new(payload, 1, 0, 180000), null));
        var response = new ConversationEntry(Guid.NewGuid(), 2, null, ConversationRole.Assistant, summary, run.ResponseId,
            EntryStatus.Completed, SessionMode.Text, 0, summary.Length, now);
        var completedAt = now.AddMilliseconds(1);
        var completed = session with { Revision = session.Revision + 1, Entries = [input, response], LastEntrySequence = 2 };
        return await store.CommitOutcomeAsync(completed, session.Revision, run.Owner, run.AgentRunId,
            new AgentRunCommand.Complete(run.Revision, completedAt, run.Claim!.Generation, summary, AgentRunOutcomeKind.Response, response.EntryId), null);
    }

    internal static async Task<SessionSnapshot> SessionAsync(this IServiceProvider services, AgentRun run) =>
        (await services.GetRequiredService<IMemoryStore>().LoadAsync(run.SessionId))!;
}
