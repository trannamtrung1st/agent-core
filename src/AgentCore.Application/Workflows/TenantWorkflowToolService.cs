using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tasks;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Workflows;

namespace AgentCore.Application.Workflows;

public sealed class TenantWorkflowToolService(IIamStore security, ITenantWorkflowCatalogStore catalog, ITenantSessionStore sessions,
    ITenantRunStore runs, TenantRunConfigurationResolver configurations, TenantWorkflowCatalogService catalogs,
    TenantWorkflowExecutionService executions, IamAuthorizer iam, TimeProvider time)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async ValueTask<IReadOnlyList<string>> OfferedAsync(AgentRun run, CancellationToken ct)
    {
        if (run.Claim is null || run.Owner.TenantId is null) return [];
        var offered = new List<string>();
        foreach (var name in WorkflowToolContract.Names)
            try { await Require(new(run.Owner, run.SessionId, run.AgentRunId, run.Claim.Generation), name, null, ct); offered.Add(name); }
            catch (AccessDeniedException) { }
        return offered;
    }
    private async ValueTask<AgentCore.Domain.Security.TenantSecurityState> Require(TenantRuntimeFrame frame, string name, Guid? workflow, CancellationToken ct)
    {
        if (!WorkflowToolContract.IsTool(name) || frame.Owner.TenantId is not { } tenant) throw new AccessDeniedException("workflow_tool_unavailable");
        var state = await security.ReadTenantAsync(tenant, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        var session = await sessions.FindTenantSessionAsync(tenant, frame.SessionId, ct) ?? throw new AccessDeniedException("session_unavailable");
        var run = await runs.FindTenantRunAsync(tenant, frame.RunId, ct) ?? throw new AccessDeniedException("run_unavailable");
        var (agent, current) = await configurations.CurrentPolicyAsync(tenant, frame.Owner.AgentInstanceId, ct);
        await TaskAuthority.SourceAsync(state, new(Agent: frame, Tool: name), session, agent, run, current, iam, time.GetUtcNow(), WorkflowToolContract.Permission(name),
            name == "workflows.start", ct, workflow is { } id ? WorkflowCatalogAuthority.Scope(tenant, id) : null, discover: workflow is null);
        return state;
    }
    private static bool Eligible(WorkflowVersion version, Guid instance)
    {
        var graph = version.Graph.Read();
        return graph.AllowedOrigins.Contains(WorkflowInvocationKind.ClaimedAgent)
            && (!graph.Nodes.Any(n => n.Kind == WorkflowNodeKind.AgentTask) || graph.CoordinatorInstanceIds.Contains(instance));
    }
    public async ValueTask<string> ExecuteAsync(TenantRuntimeFrame frame, ModelToolCall call, int budget, CancellationToken ct)
    {
        try { return await ExecuteCoreAsync(frame, call, budget, ct); }
        catch (Exception error) when (error is JsonException or FormatException or KeyNotFoundException or OverflowException or InvalidOperationException)
        { throw new ArgumentException("Use the closed workflow tool schema with correctly typed values.", error); }
    }
    private async ValueTask<string> ExecuteCoreAsync(TenantRuntimeFrame frame, ModelToolCall call, int budget, CancellationToken ct)
    {
        if (!WorkflowToolContract.IsTool(call.Name) || Encoding.UTF8.GetByteCount(call.ArgumentsJson) > 20 * 1024) throw new ArgumentException("Use the bounded workflow tool schema.");
        if (call.Name == "workflows.start" && budget < 1024) throw new ArgumentException("Reserve at least 1024 output bytes for the start receipt before changing workflow facts.");
        using var document = JsonDocument.Parse(call.ArgumentsJson, new JsonDocumentOptions { MaxDepth = 16 }); var args = document.RootElement;
        using var schema = JsonDocument.Parse(WorkflowToolContract.Descriptors().Single(d => d.Name == call.Name).ModelDefinition.ParametersJson);
        TenantTaskToolService.Closed(args, schema.RootElement.GetProperty("properties").EnumerateObject().Select(p => p.Name), schema.RootElement.GetProperty("required").EnumerateArray().Select(p => p.GetString()!));
        var tenant = frame.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required");
        var workflow = args.TryGetProperty("workflowId", out var id) ? id.GetGuid() : (Guid?)null;
        var state = await Require(frame, call.Name, workflow, ct); object result;
        var page = args.TryGetProperty("page", out var p) ? p.GetInt32() : 0;
        var size = args.TryGetProperty("pageSize", out var s) ? s.GetInt32() : 5;
        if (page is < 0 or > 100_000 || size is < 1 or > 20) throw new ArgumentException("Use a bounded workflow page.");
        if (call.Name == "workflows.list")
        {
            var visible = new List<object>();
            // The catalog has a hard 200-definition tenant ceiling. Filter eligibility before counting or paging.
            for (var index = 0; index < 2; index++)
            {
                var batch = await catalog.ListWorkflowsAsync(tenant, null, index, 100, ct);
                foreach (var definition in batch.Items.Where(d => !d.Retired && d.PublishedVersion > 0))
                    try {
                        var version = await catalogs.EligibleAgentVersionAsync(tenant, definition.WorkflowId, definition.PublishedVersion, frame.Owner.ExecutorPrincipalId!.Value, ct);
                        if (Eligible(version, frame.Owner.AgentInstanceId)) visible.Add(new { definition.WorkflowId, definition.Name, definition.Description, version.Version, GraphHash = version.Graph.Hash,
                            InputCount = version.Graph.Read().Inputs.Count, RequiredCount = version.Graph.Read().Nodes.Count(n => n.Requirement != WorkflowRequirement.None) });
                    }
                    catch (AccessDeniedException) { }
                if (batch.Items.Count < 100) break;
            }
            result = new { items = visible.Skip(page * size).Take(size), total = visible.Count, page, pageSize = size, securityRevision = state.Tenant.Revision };
        }
        else if (call.Name == "workflows.status") result = await executions.ReadClaimedAsync(frame, args.GetProperty("executionId").GetGuid(), ct);
        else if (call.Name == "workflows.start")
        {
            var saved = await executions.StartClaimedAsync(frame, workflow!.Value, new(args.GetProperty("version").GetInt32(), args.GetProperty("inputs"),
                args.GetProperty("requestKey").GetString()!, state.Tenant.Revision), ct);
            result = new { saved.ExecutionId, saved.WorkflowId, saved.Version, saved.GraphHash, status = saved.Status.ToString(), saved.Revision };
            state = await security.ReadTenantAsync(tenant, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        }
        else
        {
            var version = await catalogs.EligibleAgentVersionAsync(tenant, workflow!.Value, args.GetProperty("version").GetInt32(), frame.Owner.ExecutorPrincipalId!.Value, ct);
            if (!Eligible(version, frame.Owner.AgentInstanceId)) throw new AccessDeniedException("workflow_invocation_unavailable");
            var graph = version.Graph.Read(); var section = args.TryGetProperty("section", out var selected) ? selected.GetString() : "overview";
            result = section switch {
                "steps" => new { items = (object)graph.Nodes.Skip(page * size).Take(size), total = graph.Nodes.Count, page, pageSize = size },
                "inputs" => new { items = (object)graph.Inputs.Skip(page * size).Take(size), total = graph.Inputs.Count, page, pageSize = size },
                "overview" => new { version.WorkflowId, version.Version, GraphHash = version.Graph.Hash, graph.AllowedOrigins, graph.CoordinatorInstanceIds,
                    graph.MaxTotalTaskSteps, graph.MaxTotalTaskSeconds, graph.SuccessGoal, InputCount = graph.Inputs.Count,
                    RequiredCount = graph.Nodes.Count(n => n.Requirement != WorkflowRequirement.None) },
                _ => throw new ArgumentException("Choose overview, steps or inputs.")
            };
        }
        var currentState = await Require(frame, call.Name, workflow, ct);
        if (currentState.Tenant.Revision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision");
        var output = JsonSerializer.Serialize(result, Json);
        if (Encoding.UTF8.GetByteCount(output) > budget) throw new ArgumentException("The saved result exceeds this output budget. Read a smaller page or section; do not repeat a committed start with a new key.");
        return output;
    }
}
