using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
namespace AgentCore.Application.Security;
public static class TenantManagementMutationContinuationProjection
{
    public static AgentRunCheckpoint Complete(AgentRunCheckpoint checkpoint, ModelToolCall call, TenantApprovalRequest request, TenantApprovalOutcome outcome, TenantOperationReceipt? receipt)
    {
        var value = request.Operation.ManagementMutation!;
        var body = JsonSerializer.SerializeToNode(new {
            approvalStatus = JsonNamingPolicy.CamelCase.ConvertName(request.Status.ToString()), outcome = JsonNamingPolicy.CamelCase.ConvertName(outcome.Outcome.ToString()),
            reasonCode = outcome.ReasonCode, requestId = request.RequestId, operationId = request.Operation.OperationId, applied = receipt?.Outcome == TenantOperationOutcome.Completed,
            skill = receipt?.Outcome == TenantOperationOutcome.Completed && value.Skill is { } skill ? TenantManagementSkillRules.SkillMetadata(skill.Result) : null,
            instanceSkill = receipt?.Outcome == TenantOperationOutcome.Completed && value.Skill is { } local ? TenantManagementSkillRules.SkillMetadata(local.Result, true) : null,
            definitionSkill = receipt?.Outcome == TenantOperationOutcome.Completed && value.Skill is { } inherited ? TenantManagementSkillRules.DefinitionMetadata(inherited.Result) : null,
            resource = receipt?.Outcome == TenantOperationOutcome.Completed && value.Resource is { } resource ? TenantManagementResourceRules.Metadata(resource.Result) : null,
            deletedKey = receipt?.Outcome == TenantOperationOutcome.Completed ? value.Skill?.Result.Deleted == true ? value.Skill.Result.Key : value.Resource?.Result.Deleted == true ? value.Resource.Result.Key : null : null,
            instanceRevision = receipt?.InstanceRevision, tool = value.Harness?.ToolName ?? value.ToolName,
            changesApplyTo = value.ToolName == "management.freeze" ? "immediateRevocation" : "nextRun",
            revocationsApplyTo = value.ToolName is "management.freeze" or "management.policy.update" ? "immediate" : null,
            replayAllowed = false, currentRunConfigurationUnchanged = true })!;
        if (value.Harness is { } harness)
        {
            var saved = receipt?.Outcome == TenantOperationOutcome.Completed;
            body["saved"] = saved; body["changed"] = saved;
            body["verification"] = saved ? "partiallyVerified" : null;
            body["assessmentId"] = saved ? harness.Assessment.OperationId : null;
        }
        return AgentRunToolCallCheckpoint.CompleteTenantAuthorization(checkpoint, call, body.ToJsonString());
    }
}
