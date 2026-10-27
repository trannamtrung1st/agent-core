using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Goals;
using AgentCore.Domain.Security;
namespace AgentCore.Application.Goals;

// Called by the incumbent coordinator's bounded maintenance pass. No independent worker or queue.
public sealed class GoalContinuationService(ITenantGoalContinuationStore store, IIamStore security, TenantRunConfigurationResolver configuration,
    ITenantModelCatalogReader models, IIdGenerator ids, TimeProvider time, AutoModelRouter? routing = null)
{
    public async ValueTask<int> ObserveAsync(int limit, CancellationToken ct)
    {
        var admitted = 0;
        foreach (var candidate in await store.ListGoalContinuationCandidatesAsync(Math.Clamp(limit, 1, 8), ct))
        {
            try
            {
                var state = await security.ReadTenantAsync(candidate.Goal.TenantId, ct) ?? throw new AccessDeniedException("tenant_unavailable");
                var resolved = await configuration.ResolveInSpaceAsync(candidate.Goal.TenantId, candidate.Source.AgentInstanceId, candidate.Session.Snapshot.ContextSelection!, ct);
                var definition = resolved.Configuration.Configuration.Definition;
                var catalog = await models.ReadAsync(state, ct);
                var selected = routing is null ? SessionModelBinder.BindPreference(catalog, candidate.Session.Snapshot.ModelPreference ?? AgentCore.Domain.Models.ModelPreference.Inherit, definition.ModelDefaults)
                    : await routing.ResolveAsync(catalog, candidate.Session.Snapshot.ModelPreference ?? AgentCore.Domain.Models.ModelPreference.Inherit, definition.ModelDefaults, false, true, "goalContinuation", RoutingTaskSignals.Project(candidate.Goal.Plan.Title, "goalContinuation"), ct);
                var source = candidate.Source; var checkpoint = GoalRules.Next(candidate.Goal)!; var at = time.GetUtcNow(); var activationId = ids.NewId(); var runId = ids.NewId();
                var previous = source.Admission.ExecutionContext!;
                var binding = new GoalWorkContext(candidate.Goal.GoalId, checkpoint.CheckpointId, candidate.Goal.PlanRevision, candidate.Goal.Revision, GoalRules.Hash(candidate.Goal.Plan),
                    source.SessionId, candidate.Goal.SpaceId, candidate.Session.Snapshot.GoalFocusRevision, GoalRules.Hash(checkpoint), source.Admission.GoalWork!.InputSequence, GoalRules.Task(candidate.Goal, checkpoint));
                var activation = new Activation(activationId, source.SessionId, ActivationKind.GoalContinuation, [], source.ActivationId, null, source.SessionId, source.AgentRunId,
                    GoalContinuationRules.Key(source, candidate.Goal), at, previous.RootActivationId, source.ActivationId,
                    JsonSerializer.Serialize(new { TriggerKind = TriggerKind.UserTurn, EnvironmentKind = "goal-continuation", InputBoundarySequence = binding.InputSequence, Text = "Continue the explicitly accepted Goal checkpoint. Preserve prior effects; never replay commands. Task data: " + binding.TaskJson }));
                var context = new TrustedExecutionContext(previous.TenantId, previous.AgentPrincipalId, previous.OriginalInitiatorPrincipalId, source.SessionId, runId,
                    activationId, previous.RootActivationId, source.AgentRunId, source.ActivationId, ExecutionOrigin.GoalContinuation);
                var run = AgentRun.Create(runId, source.Owner, new(activation, definition.Id, definition.Version, resolved.Configuration.Persona, ids.NewId(),
                    AgentRunOutputContract.ConversationResponse, ExecutionBudgetResolver.Resolve(definition, resolved.Configuration.InstanceBudgets, false), resolved.Configuration.Configuration,
                    executionContext: context, goalWork: binding), new(selected.OfferingId, selected.ConnectionId, selected.ExternalModelId, selected.ReasoningEffort, selected.Binding, selected.Decision),
                    1, at, resolved.Configuration.Skills, resolved.Configuration.Skills.Where(s => s.Projection == SkillProjection.Always).Select(s => s.Key).ToArray());
                if (await store.TryAdmitGoalContinuationAsync(new(run, source.AgentRunId, state.Tenant.Revision, candidate.Goal.Revision, candidate.Session.Snapshot.Revision), ids.NewId(), at, ct)) admitted++;
            }
            catch (AccessDeniedException) { }
            catch (SecurityConflictException) { }
            catch (AgentCore.Application.Sessions.AgentCoreException) { }
        }
        return admitted;
    }
    public static async ValueTask RequireAsync(TenantSecurityState state, TenantAgent agent, AgentRun source, IamAuthorizer iam, CancellationToken ct)
    {
        TenantSessionRules.RequireExecutor(state, agent);
        if (agent.Instance.Lifecycle != AgentInstanceLifecycle.Active || source.Admission.ExecutionContext?.OriginalInitiatorPrincipalId is not { } human) throw new AccessDeniedException("goal_continuation_unavailable");
        foreach (var principal in new[] { human, agent.PrincipalId })
            foreach (var (permission, scope) in new[] { ("goals.read", TenantSessionRules.Scope(state.Tenant.TenantId, source.SessionId)),
                ("goals.manage", TenantSessionRules.Scope(state.Tenant.TenantId, source.SessionId)), ("sessions.read", TenantSessionRules.Scope(state.Tenant.TenantId, source.SessionId)),
                ("agent-runs.create", AccessScope.ForTenant(state.Tenant.TenantId)), ("agents.chat", AccessScope.ForResource(state.Tenant.TenantId, "agent", source.AgentInstanceId.ToString("D"))) })
            {
                await GoalAuthority.Require(state, principal, permission, scope, iam, ct);
                if (TenantOperationPolicyService.Effective(state, PermissionCatalog.Get(permission)).Mode == OperationApprovalMode.Always) throw new AccessDeniedException("goal_continuation_review_required");
            }
    }
}
