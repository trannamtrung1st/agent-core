using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_progress_policy_requires_actual_current_conversation_but_generic_execution_never_writes_a_notice(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var s = await Setup(f);
        var runtime = (ITenantRuntimeStore)f.Iam;
        var run = await runtime.ApplyTenantRuntimeCommandAsync(s.Run.Owner, s.Run.AgentRunId,
            new AgentRunCommand.Claim(s.Run.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var executor = new SessionToolExecutor(tenantAuthorization: new((ITenantRunStore)f.Iam, Operations(f), f.Clock));
        var frame = new ToolExecutionAdmission(false, TriggerKind.UserTurn, true, AgentInstanceId: run.AgentInstanceId, AgentRunId: run.AgentRunId,
            OwnedSessionId: run.SessionId, TenantId: s.Tenant, AgentRunGeneration: run.Claim!.Generation);
        var call = new ModelToolCall("progress", ToolCatalog.AppMessageSend, "{\"text\":\"Progress\"}");
        Assert.Equal(ToolPolicyDecision.Allow, await executor.EvaluateExecutionPolicyAsync(run.Admission.Configuration!.Definition, run.SessionId, call, default, frame, default));
        using (var result = JsonDocument.Parse((await executor.ExecuteAsync(run.Admission.Configuration.Definition, run.SessionId, call, 4096, admission: frame)).Text))
        {
            Assert.Equal("forbidden", result.RootElement.GetProperty("error").GetString());
            Assert.Equal("runtime_dispatch_required", result.RootElement.GetProperty("reasonCode").GetString());
        }
        foreach (var invalid in new[] { frame with { IntermediateMessagingAllowed = false }, frame with { Detached = true },
            frame with { TriggerKind = TriggerKind.ScheduledOccurrence }, frame with { SupportsTools = false },
            frame with { ConfigurationContinuationTarget = ToolCatalog.WorkspaceWrite }, frame with { AgentRunGeneration = Guid.NewGuid() },
            frame with { TenantId = null }, frame with { OwnedSessionId = Guid.NewGuid() } })
            Assert.Equal(ToolPolicyDecision.Deny, await executor.EvaluateExecutionPolicyAsync(run.Admission.Configuration.Definition, run.SessionId, call, default, invalid, default));
        Assert.Equal(run.Revision, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, run.AgentRunId))!.Revision);
        Assert.DoesNotContain((await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(s.Tenant, run.SessionId))!.Snapshot.Entries,
            entry => entry.Role == ConversationRole.ApplicationMessage);
    }
}
