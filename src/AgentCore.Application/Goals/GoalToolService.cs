using System.Text.Json;
using System.Text.Json.Serialization;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Goals;
namespace AgentCore.Application.Goals;
public sealed class GoalToolService(IIamStore security, ITenantSessionGoalStore goals, ITenantSessionStore sessions,
    ITenantAgentStore agents, ITenantSpaceStore spaces, TenantRunConfigurationResolver configuration, TenantOperationAuthorizer operations, TimeProvider time, IIdGenerator ids)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public async ValueTask<IReadOnlyList<string>> OfferedAsync(AgentRun run, CancellationToken ct)
    {
        if (run.Claim is null) return []; var result = new List<string>();
        foreach (var name in GoalToolContract.Names.Where(name => AgentCore.Application.Agents.RolePermissions.AllowsTool(run.Admission.Configuration!.Definition, name)))
            try { await Require(new(run.Owner, run.SessionId, run.AgentRunId, run.Claim.Generation), name, null, false, ct); if (name is "goals.progress" or "goals.verify" && run.Admission.GoalWork is null) continue; result.Add(name); }
            catch (AccessDeniedException) { }
            catch (SecurityConflictException) { }
        return result;
    }
    public async ValueTask<string> ExecuteAsync(TenantRuntimeFrame frame, ModelToolCall call, int budget, CancellationToken ct)
    {
        if (!GoalToolContract.IsTool(call.Name) || System.Text.Encoding.UTF8.GetByteCount(call.ArgumentsJson) > 64_000) throw new ArgumentException("Invalid Goal tool arguments.");
        using var document = JsonDocument.Parse(call.ArgumentsJson); var args = document.RootElement;
        using var schema = JsonDocument.Parse(GoalToolContract.Descriptors().Single(d => d.Name == call.Name).ModelDefinition.ParametersJson);
        var allowed = schema.RootElement.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet();
        if (args.ValueKind != JsonValueKind.Object || args.EnumerateObject().Any(p => !allowed.Contains(p.Name)) || args.EnumerateObject().Select(p => p.Name).Distinct().Count() != args.EnumerateObject().Count()) throw new ArgumentException("Use the closed Goal arguments.");
        var goalId = args.TryGetProperty("goalId", out var value) ? value.GetGuid() : (Guid?)null;
        var state = await Require(frame, call.Name, goalId, call.Name != "goals.read", ct);
        object result;
        if (call.Name == "goals.read")
        {
            if (goalId is { } id) result = await goals.FindGoalAsync(state.Tenant.TenantId, frame.SessionId, id, ct) ?? throw new AccessDeniedException("resource_unavailable");
            else
            {
                var pageIndex = args.TryGetProperty("page", out var p) ? p.GetInt32() : 0;
                if (pageIndex is < 0 or > 100_000) throw new ArgumentOutOfRangeException(nameof(pageIndex));
                var page = await goals.ListGoalsAsync(state.Tenant.TenantId, frame.SessionId, 0, GoalRules.MaxGoals, ct);
                var readable = new List<SessionGoal>();
                foreach (var goal in page.Items) try { await Require(frame, call.Name, goal.GoalId, false, ct); readable.Add(goal); } catch (AccessDeniedException) { }
                result = new { items = readable.Skip(pageIndex * 5).Take(5).ToArray(), page = pageIndex, hasMore = (pageIndex + 1) * 5 < readable.Count };
            }
        }
        else
        {
            if (goalId is null) throw new ArgumentException("Goal identity is required.");
            var revision = args.GetProperty("expectedGoalRevision").GetInt64(); var planRevision = args.GetProperty("expectedPlanRevision").GetInt64();
            var kind = call.Name == "goals.plan" ? revision == 0 ? GoalCommandKind.Create : GoalCommandKind.Plan : call.Name == "goals.progress" ? GoalCommandKind.Progress : GoalCommandKind.Verify;
            var key = "tool:" + frame.RunId.ToString("N") + ":" + GoalRules.Hash(new { call.Id, call.Name, call.ArgumentsJson })[..48];
            var command = new GoalCommand(kind, key, state.Tenant.Revision, revision, planRevision,
                args.TryGetProperty("plan", out var plan) ? plan.Deserialize<GoalPlan>(Json) : null,
                args.TryGetProperty("checkpointId", out var checkpoint) ? checkpoint.GetGuid() : null,
                args.TryGetProperty("note", out var note) ? note.GetString() : null,
                args.TryGetProperty("action", out var action) ? action.GetString() : null,
                args.TryGetProperty("evidence", out var evidence) ? evidence.Deserialize<GoalEvidence>(Json) : null);
            result = await goals.MutateGoalAsync(new(state.Tenant.TenantId, frame.SessionId, goalId.Value, new(Agent: frame, Tool: call.Name), command, ids.NewId(), time.GetUtcNow()), ct);
        }
        await Require(frame, call.Name, goalId, false, ct);
        var text = JsonSerializer.Serialize(result, Json);
        return System.Text.Encoding.UTF8.GetByteCount(text) <= budget ? text : JsonSerializer.Serialize(new { status = "output_limit", message = "Read one Goal or a smaller page; no private partial payload is returned." });
    }
    private async ValueTask<AgentCore.Domain.Security.TenantSecurityState> Require(TenantRuntimeFrame frame, string tool, Guid? goalId, bool mutate, CancellationToken ct)
    {
        var (run, state) = await operations.ResolveAsync(new(frame.Owner.TenantId!.Value, frame.RunId), ct);
        var session = await sessions.FindTenantSessionAsync(state.Tenant.TenantId, frame.SessionId, ct) ?? throw new AccessDeniedException("resource_unavailable");
        var goal = goalId is { } id ? await goals.FindGoalAsync(state.Tenant.TenantId, frame.SessionId, id, ct) : null;
        var spaceId = goal?.SpaceId ?? run.Admission.Configuration!.SpaceContext!.SpaceId;
        var agent = await agents.FindTenantAgentAsync(state.Tenant.TenantId, run.AgentInstanceId, ct);
        var member = (await spaces.ReadMembershipsAsync(state.Tenant.TenantId, spaceId, ct)).SingleOrDefault(m => m.InstanceId == run.AgentInstanceId);
        await GoalAuthority.RequireAsync(state, session, goal, new(Agent: frame, Tool: tool), mutate ? "goals.manage" : "goals.read", new(security),
            await spaces.FindSpaceAsync(state.Tenant.TenantId, spaceId, ct), member, agent, run,
            (await configuration.CurrentPolicyAsync(state.Tenant.TenantId, run.AgentInstanceId, ct)).Definition, time.GetUtcNow(), mutate, ct);
        return state;
    }
}
