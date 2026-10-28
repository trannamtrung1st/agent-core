using System.Collections.Immutable;
using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tasks;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Workflows;

public static class WorkflowReadReferences
{
    // A source Run can inspect the process it just started, including its worker. Rechecking that worker
    // reaches the same source Run. Bound this read-reference cycle by actual tenant/Run identity only;
    // incumbent source/claim/IAM checks still execute on every owning boundary.
    private static readonly AsyncLocal<ImmutableHashSet<(Guid Tenant, Guid Run)>?> Reading = new();
    public static async ValueTask RequireAsync(AgentRun run, TenantSecurityState state, IamAuthorizer iam, ITenantRunStore runs, CancellationToken ct)
    {
        if (run.Status != AgentRunStatus.Running || run.Claim is null || !AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var messages)) return;
        var key = (state.Tenant.TenantId, run.AgentRunId); var prior = Reading.Value;
        if (prior?.Contains(key) == true) return;
        Reading.Value = (prior ?? ImmutableHashSet<(Guid, Guid)>.Empty).Add(key);
        try
        {
            var savedMessages = messages!;
            var replies = savedMessages.Where(m => m.Role == ModelRole.Tool && m.Name is { } name && WorkflowToolContract.IsTool(name)).ToArray();
            if (replies.Length == 0) return;
            if (runs is not ITenantWorkflowCatalogStore catalog || runs is not ITenantWorkflowExecutionStore processes || runs is not ITenantSessionStore sessions)
                throw new AccessDeniedException("workflow_source_unavailable");
            var calls = savedMessages.SelectMany(m => m.ToolCalls ?? []).GroupBy(c => c.Id, StringComparer.Ordinal).ToArray();
            if (calls.Any(g => g.Select(c => (c.Name, c.ArgumentsJson)).Distinct().Count() != 1)) throw new AccessDeniedException("workflow_source_ambiguous");
            var byId = calls.ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var actor = new TaskActor(Agent: new(run.Owner, run.SessionId, run.AgentRunId, run.Claim.Generation), Tool: "workflows.status");
            async ValueTask Read(string permission, AccessScope scope)
            {
                await TaskAuthority.RequireAsync(state, actor.PrincipalId, permission, scope, iam, ct);
                if (await TenantSandboxReadOperationRules.NeedsReviewAsync(state, run.Admission.ExecutionContext?.OriginalInitiatorPrincipalId, permission, scope, iam, ct))
                    throw new AccessDeniedException("workflow_source_unavailable");
            }
            async ValueTask Version(Guid workflow, int number, string? hash)
            {
                await Read("workflows.read", WorkflowCatalogAuthority.Scope(state.Tenant.TenantId, workflow));
                var saved = await catalog.FindWorkflowVersionAsync(state.Tenant.TenantId, workflow, number, ct);
                if (saved is null || hash is not null && saved.Graph.Hash != hash) throw new AccessDeniedException("workflow_source_unavailable");
            }
            foreach (var reply in replies)
            {
                if (!byId.TryGetValue(reply.ToolCallId ?? "", out var call) || call.Name != reply.Name) continue;
                using var document = JsonDocument.Parse(reply.Text); var data = document.RootElement;
                if (data.ValueKind != JsonValueKind.Object || data.TryGetProperty("error", out _) || data.TryGetProperty("code", out _)) continue;
                using var arguments = JsonDocument.Parse(call.ArgumentsJson); var args = arguments.RootElement;
                if (call.Name == "workflows.list" && data.TryGetProperty("items", out var items))
                    foreach (var item in items.EnumerateArray()) await Version(item.GetProperty("workflowId").GetGuid(), item.GetProperty("version").GetInt32(), item.GetProperty("graphHash").GetString());
                if (call.Name == "workflows.inspect")
                    await Version(args.GetProperty("workflowId").GetGuid(), args.GetProperty("version").GetInt32(), data.TryGetProperty("graphHash", out var hash) ? hash.GetString() : null);
                if (call.Name is not ("workflows.status" or "workflows.start") || !data.TryGetProperty("executionId", out var executionId)) continue;
                var execution = await processes.FindWorkflowExecutionAsync(state.Tenant.TenantId, executionId.GetGuid(), ct) ?? throw new AccessDeniedException("workflow_source_unavailable");
                await Version(execution.WorkflowId, execution.Version, data.GetProperty("graphHash").GetString());
                if (call.Name == "workflows.start")
                {
                    if (execution.Source.RunId != run.AgentRunId || execution.Source.PrincipalId != actor.PrincipalId
                        || execution.RequestKey != args.GetProperty("requestKey").GetString()) throw new AccessDeniedException("workflow_source_unavailable");
                    continue;
                }
                if (!await WorkflowReadRules.CanReadAsync(state, actor.PrincipalId, execution, iam, sessions, runs, ct)) throw new AccessDeniedException("workflow_source_unavailable");
                await Read("workflows.monitor", WorkflowCatalogAuthority.Scope(execution.TenantId, execution.WorkflowId));
                if (!data.TryGetProperty("steps", out var steps)) continue;
                var operations = await processes.ReadWorkflowStepsAsync(execution.TenantId, execution.ExecutionId, ct);
                foreach (var step in steps.EnumerateArray())
                {
                    if (step.TryGetProperty("evaluationAttemptId", out var evaluationId) && evaluationId.ValueKind == JsonValueKind.String)
                    {
                        var operation = operations.SingleOrDefault(o => o.NodeId == step.GetProperty("nodeId").GetString());
                        if (operation?.EvaluationAttemptId != evaluationId.GetGuid() || runs is not ITenantEvaluationStore evaluations)
                            throw new AccessDeniedException("workflow_source_unavailable");
                        var attempt = await evaluations.FindAttemptAsync(execution.TenantId, evaluationId.GetGuid(), ct) ?? throw new AccessDeniedException("workflow_source_unavailable");
                        var subject = await runs.FindTenantRunAsync(execution.TenantId, attempt.SubjectId, ct) ?? throw new AccessDeniedException("workflow_source_unavailable");
                        await Read("evaluations.read", AccessScope.ForResource(execution.TenantId, "evaluation-suite", attempt.SuiteId.ToString("D")));
                        await Read("agent-runs.read", TenantRunRules.Scope(execution.TenantId, subject.AgentRunId));
                        await Read("sessions.read", TenantSessionRules.Scope(execution.TenantId, subject.SessionId));
                    }
                    if (!step.TryGetProperty("taskId", out var taskId) || taskId.ValueKind == JsonValueKind.Null) continue;
                    var task = runs is ITenantTaskStore tasks ? await tasks.FindTaskAsync(execution.TenantId, taskId.GetGuid(), ct) : null;
                    if (task?.Source.Workflow?.ExecutionId != execution.ExecutionId || task.Source.Workflow.NodeId != step.GetProperty("nodeId").GetString()
                        || !await TaskAuthority.CanReadAsync(state, actor, task, iam, ct, run)) throw new AccessDeniedException("workflow_source_unavailable");
                    if (step.TryGetProperty("workerRunId", out var workerId) && workerId.ValueKind == JsonValueKind.String)
                    {
                        var worker = await runs.FindTenantRunAsync(execution.TenantId, workerId.GetGuid(), ct);
                        if (worker is null || worker.SessionId != task.WorkerSessionId) throw new AccessDeniedException("workflow_source_unavailable");
                        await Read("agent-runs.read", TenantRunRules.Scope(execution.TenantId, worker.AgentRunId));
                        await Read("sessions.read", TenantSessionRules.Scope(execution.TenantId, worker.SessionId));
                    }
                }
            }
        }
        finally { Reading.Value = prior; }
    }
}
