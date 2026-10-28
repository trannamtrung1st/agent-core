using System.Text.Json;
using AgentCore.Domain.Tasks;

namespace AgentCore.Domain.Tests;

public sealed class WorkflowTaskSourceTests
{
    private static TaskSource Source() => new(Guid.NewGuid(), null, Guid.NewGuid(), Guid.NewGuid(), TaskActorKind.WorkflowContinuation,
        null, null, null, null, Guid.NewGuid(), null, 1,
        new(Guid.NewGuid(), Guid.NewGuid(), 1, new('a', 64), "prepare", 1, Guid.NewGuid()));
    [Fact]
    public void Workflow_source_has_exact_process_witness_and_no_forged_session_human_or_run()
    {
        var source = Source(); source.Validate(); Assert.Null(source.SessionId); Assert.Null(source.RunId); Assert.Null(source.HumanCommandId);
        Assert.Throws<InvalidOperationException>(() => source.RequireSessionId());
        Assert.Equal(source, JsonSerializer.Deserialize<TaskSource>(JsonSerializer.Serialize(source)));
        Assert.Equal($"workflow:{source.Workflow!.ExecutionId:D}:{source.Workflow.OperationId:D}", source.OperationScope);
        Assert.Throws<ArgumentException>(() => (source with { SessionId = Guid.NewGuid() }).Validate());
        Assert.Throws<ArgumentException>(() => (source with { RunId = Guid.NewGuid() }).Validate());
        Assert.Throws<ArgumentException>(() => (source with { HumanCommandId = Guid.NewGuid() }).Validate());
        Assert.Throws<ArgumentException>(() => (source with { Workflow = null }).Validate());
        Assert.Throws<ArgumentException>(() => (source with { ActorKind = TaskActorKind.Human }).Validate());
        Assert.Throws<ArgumentException>(() => (source with { ActorKind = TaskActorKind.Agent }).Validate());
        Assert.Throws<ArgumentException>(() => (source with { Workflow = source.Workflow with { Attempt = 2 } }).Validate());
    }
    [Fact]
    public void Existing_task_sources_keep_their_historical_json_shape_and_require_real_sessions()
    {
        var source = new TaskSource(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), TaskActorKind.Agent,
            Guid.NewGuid(), Guid.NewGuid(), null, null, Guid.NewGuid(), null, 1);
        source.Validate(); Assert.DoesNotContain("Workflow", JsonSerializer.Serialize(source));
        Assert.Throws<ArgumentException>(() => (source with { SessionId = null }).Validate());
        Assert.Throws<ArgumentException>(() => (source with { Workflow = Source().Workflow }).Validate());
    }
}
