using System.Text.Json;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Tests;

public sealed class TenantWaitContractTests
{
    [Theory] [InlineData(ExecutionOrigin.UserMessage, AgentRunOutputContract.ConversationResponse)]
    [InlineData(ExecutionOrigin.ApprovalContinuation, AgentRunOutputContract.ConversationResponse)]
    [InlineData(ExecutionOrigin.DelegatedTask, AgentRunOutputContract.BackgroundOutcome)]
    [InlineData(ExecutionOrigin.ApprovalContinuation, AgentRunOutputContract.BackgroundOutcome)]
    public async Task Tenant_wait_requires_exact_owned_human_derived_execution_and_explicit_capability(ExecutionOrigin origin, AgentRunOutputContract output)
    {
        var definition = await CapabilityProjectionTests.Definition(ToolCatalog.ExecutionWait);
        var session = Guid.NewGuid(); var activation = Guid.NewGuid();
        TrustedExecutionContext Execution(ExecutionOrigin value) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), session, Guid.NewGuid(), activation,
            value == ExecutionOrigin.UserMessage ? activation : Guid.NewGuid(), value == ExecutionOrigin.UserMessage ? null : Guid.NewGuid(), value == ExecutionOrigin.UserMessage ? null : Guid.NewGuid(), value);
        var context = CapabilityProjectionTests.Context(definition) with { AgentInstanceId = Guid.NewGuid(), OwnedSessionId = session,
            OutputContract = output, DetachedExecution = origin != ExecutionOrigin.UserMessage, LoadedCapabilityIds = [ToolCatalog.ExecutionWait],
            ExecutionContext = Execution(origin), RunTaskIntent = output == AgentRunOutputContract.BackgroundOutcome
                ? new("Exact delegated task", AgentRunTaskSource.DelegatedTask, Guid.NewGuid().ToString("D")) : null };
        Assert.Contains(ToolProjectionService.Project(definition, context, ToolConfigurationGates.AllowAll), t => t.Name == ToolCatalog.ExecutionWait);
        foreach (var invalid in new[] { context with { OwnedSessionId = Guid.NewGuid() }, context with { ModelSupportsTools = false },
            context with { OutputContract = AgentRunOutputContract.CompletionReport }, context with { ExecutionContext = Execution(ExecutionOrigin.CompletionReport) } })
            Assert.DoesNotContain(ToolProjectionService.Project(definition, invalid, ToolConfigurationGates.AllowAll), t => t.Name == ToolCatalog.ExecutionWait);
        var noCapability = await CapabilityProjectionTests.Definition(ToolCatalog.WorkspaceRead);
        Assert.DoesNotContain(ToolProjectionService.Project(noCapability, context, ToolConfigurationGates.AllowAll), t => t.Name == ToolCatalog.ExecutionWait);
    }

    [Theory] [InlineData("{\"mode\":\"duration\",\"seconds\":0}")]
    [InlineData("{\"mode\":\"duration\",\"seconds\":301}")]
    [InlineData("{\"mode\":\"duration\",\"seconds\":1,\"seconds\":2}")]
    [InlineData("{\"mode\":\"duration\",\"seconds\":1,\"tenantId\":\"forged\"}")]
    [InlineData("{\"mode\":\"duration\",\"seconds\":1,\"until\":\"any\"}")]
    [InlineData("{\"mode\":\"unsupported\"}")]
    [InlineData("{\"mode\":\"background\",\"backgroundSessionIds\":[],\"until\":\"all\",\"timeoutSeconds\":1}")]
    [InlineData("{\"mode\":\"background\",\"backgroundSessionIds\":[\"00000000-0000-0000-0000-000000000000\"],\"until\":\"all\",\"timeoutSeconds\":1}")]
    public void Closed_wait_contract_rejects_ambiguous_authority_unbounded_or_empty_conditions(string args)
    {
        using var json = JsonDocument.Parse(args);
        Assert.Throws<ArgumentException>(() => ExecutionWaitContract.Parse("wait", json.RootElement, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Wait_contract_preserves_finite_fractional_deadline_and_rejects_duplicate_targets()
    {
        var id = Guid.NewGuid(); using var duration = JsonDocument.Parse("""{"mode":"duration","seconds":0.125}""");
        var wait = ExecutionWaitContract.Parse("wait", duration.RootElement, DateTimeOffset.UnixEpoch);
        Assert.Equal(TimeSpan.FromMilliseconds(125), wait.DeadlineUtc - wait.StartedAtUtc);
        using var duplicate = JsonDocument.Parse(JsonSerializer.Serialize(new { mode = "tasks", taskIds = new[] { id, id }, until = "any", timeoutSeconds = 1 }));
        Assert.Throws<ArgumentException>(() => ExecutionWaitContract.Parse("wait", duplicate.RootElement, DateTimeOffset.UnixEpoch));
    }
}
