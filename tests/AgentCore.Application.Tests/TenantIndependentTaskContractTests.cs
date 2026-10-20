using AgentCore.Application.Tools;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Tests;

public sealed class TenantIndependentTaskContractTests
{
    [Theory]
    [InlineData(ExecutionOrigin.ImmediateBackground, true)] [InlineData(ExecutionOrigin.ApprovalContinuation, true)]
    [InlineData(ExecutionOrigin.ImmediateBackground, false)] [InlineData(ExecutionOrigin.ApprovalContinuation, false)]
    public async Task No_human_child_controls_require_a_frozen_child_task_and_owned_trusted_parent(ExecutionOrigin origin, bool task)
    {
        var names = new[] { ToolCatalog.BackgroundStart, ToolCatalog.BackgroundList, ToolCatalog.BackgroundInspect,
            ToolCatalog.BackgroundTake, ToolCatalog.BackgroundAcknowledge, ToolCatalog.ExecutionWait };
        var definition = await CapabilityProjectionTests.Definition(names); var session = Guid.NewGuid(); var parent = Guid.NewGuid();
        var context = CapabilityProjectionTests.Context(definition) with { AgentInstanceId = Guid.NewGuid(), OwnedSessionId = session,
            OutputContract = AgentRunOutputContract.BackgroundOutcome, DetachedExecution = true, LoadedCapabilityIds = names, HasBackgroundClaim = true,
            ExecutionContext = new(Guid.NewGuid(), Guid.NewGuid(), null, session, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), parent, Guid.NewGuid(), origin),
            RunTaskIntent = task ? new("Exact child task", AgentRunTaskSource.AgentRun, parent.ToString("D")) : null };
        var tools = ToolProjectionService.Project(definition, context, ToolConfigurationGates.AllowAll);
        foreach (var name in names) Assert.Equal(task, tools.Any(t => t.Name == name));
        if (task)
        {
            var schema = Assert.Single(tools, t => t.Name == ToolCatalog.RequestAuthorization).ParametersJson;
            Assert.Contains("start_background_work", schema); Assert.Contains("inspect_background_completion", schema);
            Assert.DoesNotContain("update_persona", schema);
        }
        var foreign = ToolProjectionService.Project(definition, context with { OwnedSessionId = Guid.NewGuid() }, ToolConfigurationGates.AllowAll);
        foreach (var name in names) Assert.DoesNotContain(foreign, t => t.Name == name);
    }

    [Theory]
    [InlineData(ExecutionOrigin.ScheduledOccurrence, false, true, true)]
    [InlineData(ExecutionOrigin.ManualInvocation, true, true, true)]
    [InlineData(ExecutionOrigin.ApprovalContinuation, false, true, true)]
    [InlineData(ExecutionOrigin.ApprovalContinuation, true, true, true)]
    [InlineData(ExecutionOrigin.ManualInvocation, false, true, false)]
    [InlineData(ExecutionOrigin.ScheduledOccurrence, false, false, false)]
    [InlineData(ExecutionOrigin.ApprovalContinuation, false, false, false)]
    [InlineData(ExecutionOrigin.ApplicationEvent, false, true, false)]
    [InlineData(ExecutionOrigin.CoreEvent, false, true, false)]
    [InlineData(ExecutionOrigin.Initiative, false, true, false)]
    public async Task Independent_discovery_requires_a_converted_origin_and_frozen_Automation_task_without_chat_child_capability(ExecutionOrigin origin, bool human, bool automation, bool expected)
    {
        var definition = await CapabilityProjectionTests.Definition(ToolCatalog.WorkspaceRead, ToolCatalog.WorkspaceWrite, ToolCatalog.ArtifactsCreateFromWorkspace, ToolCatalog.BackgroundInspect, ToolCatalog.ExecutionWait);
        var session = Guid.NewGuid(); var activation = Guid.NewGuid(); var continuation = origin == ExecutionOrigin.ApprovalContinuation;
        var context = CapabilityProjectionTests.Context(definition) with {
            AgentInstanceId = Guid.NewGuid(), OwnedSessionId = session, ModelSupportsTools = true, DetachedExecution = true, OutputContract = AgentRunOutputContract.BackgroundOutcome,
            LoadedCapabilityIds = [ToolCatalog.WorkspaceRead, ToolCatalog.WorkspaceWrite, ToolCatalog.ArtifactsCreateFromWorkspace, ToolCatalog.ExecutionWait, ToolCatalog.BackgroundInspect],
            ExecutionContext = new(Guid.NewGuid(), Guid.NewGuid(), human ? Guid.NewGuid() : null, session, Guid.NewGuid(), activation,
                continuation ? Guid.NewGuid() : activation, continuation ? Guid.NewGuid() : null, continuation ? Guid.NewGuid() : null, origin),
            RunTaskIntent = new("The exact frozen task", automation ? AgentRunTaskSource.Automation : AgentRunTaskSource.AgentRun, Guid.NewGuid().ToString("D") + ":1") };
        Assert.Equal(expected, TenantTaskExecutionContract.Allows(context));
        var tools = ToolProjectionService.Project(definition, context, ToolConfigurationGates.AllowAll);
        Assert.Equal(expected, tools.Any(t => t.Name == ToolCatalog.WorkspaceRead));
        Assert.Equal(expected, tools.Any(t => t.Name == ToolCatalog.WorkspaceWrite));
        Assert.Equal(expected, tools.Any(t => t.Name == ToolCatalog.ExecutionWait));
        Assert.DoesNotContain(tools, t => t.Name is ToolCatalog.BackgroundStart or ToolCatalog.BrowserFillCredential);
        if (expected)
        {
            Assert.Contains(tools, t => t.Name == ToolCatalog.ExecutionWait);
            Assert.Contains(tools, t => t.Name == ToolCatalog.BackgroundInspect);
            Assert.Contains("inspect_background_completion", Assert.Single(tools, t => t.Name == ToolCatalog.RequestAuthorization).ParametersJson);
        }
    }
    [Theory] [InlineData(false, 32, 0, 0, 0, 0, null)] [InlineData(true, 32, 0, 0, 0, 0, "approval_rate_limited")]
    [InlineData(false, 64, 128, 0, 0, 0, "approval_rate_limited")] [InlineData(false, 64, 0, 32, 0, 0, "approval_rate_limited")]
    [InlineData(false, 64, 0, 0, 8, 0, "approval_rate_limited")] [InlineData(false, 64, 0, 0, 0, 32, "approval_root_limit")]
    public void Independent_tasks_have_no_fictitious_human_quota_but_keep_tenant_executor_operation_and_root_limits(bool human, int requester, int tenant, int executor, int operation, int root, string? reason)
    {
        if (reason is null) TenantApprovalLimits.Require(tenant, executor, requester, operation, root, human);
        else Assert.Equal(reason, Assert.Throws<SecurityConflictException>(() => TenantApprovalLimits.Require(tenant, executor, requester, operation, root, human)).ReasonCode);
    }

}
