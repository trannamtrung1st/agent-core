using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Application.Observability;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private IReadOnlyList<string> LoadedCapabilitiesFor(Guid responseId) =>
        _boundAgentRun?.ResponseId == responseId ? _boundAgentRun.LoadedCapabilityIds : [];

    private int CapabilityLoadCountFor(Guid responseId) =>
        _boundAgentRun?.ResponseId == responseId ? _boundAgentRun.CapabilityLoadCount : 0;

    private async Task<AgentContext> CapabilityProjectionContextAsync(AgentTrigger trigger, ILanguageModel model,
        IReadOnlyList<AgentCore.Domain.Definitions.EffectiveSkill> catalog, IReadOnlyList<string> skills, IReadOnlyList<string> loaded, CancellationToken ct)
    {
        var tenantFiles = _runtimeOwner is not null && _boundAgentRun is { } run
            ? await (_tenantAttachments ?? throw new InvalidOperationException("Tenant attachment binding is required."))
                .PrepareAsync(run, _snapshot, model.Capabilities.Vision, ct, processCurrentContents: false).ConfigureAwait(false) : null;
        var tenantSkills = await SkillContextAsync(_boundAgentRun, catalog, ct).ConfigureAwait(false);
        return new(ExecutionDefinition, tenantFiles?.History ?? _snapshot.Entries, _snapshot.Summary, _profile, _snapshot.Mode, _snapshot.PendingTopic,
            false, null, trigger, SessionAttachments: tenantFiles?.Manifest ?? await BuildSessionAttachmentManifestAsync(ct),
            ModelSupportsTools: model.Capabilities.Tools, ModelSupportsVision: model.Capabilities.Vision,
            PinnedSkillCatalog: tenantSkills.Catalog, ActiveSkillKeys: skills, LoadedCapabilityIds: loaded, IntermediateMessagingAllowed: _intermediateMessagingAllowed,
            DetachedExecution: IsInitialBackgroundRun, OwnedSessionId: SessionId, AgentInstanceId: _snapshot.AgentInstanceId,
            ConfigurationContinuationTarget: ContinuationTarget(_boundAgentRun),
            OutputContract: _boundAgentRun?.Admission.OutputContract ?? AgentRunOutputContract.ConversationResponse,
            AuthoredAutomation: _boundAgentRun?.Admission.Activation.DedupeKey.StartsWith("automation:", StringComparison.Ordinal) == true,
            HasBackgroundClaim: _boundAgentRun is { Claim: { } claim } bound && await _agentRuns.HasCompletionClaimAsync(bound.Owner, bound.AgentRunId, claim.Generation, _time.GetUtcNow(), ct),
            CredentialMetadataAvailable: _boundAgentRun?.Owner.TenantId is not null ? _boundAgentRun.Admission.Configuration?.CredentialBindings.Count > 0
                : await _tools.CredentialMetadataAvailableAsync(_snapshot.AgentInstanceId, ct),
            Harness: await HarnessContextAsync(_boundAgentRun, ct),
            AgentWorkspaceAvailable: await _tools.AgentWorkspaceAvailableAsync(SessionId, ct),
            AllowAgentConsolidation: (await CurrentContinuitySettingsAsync(_boundAgentRun, ct).ConfigureAwait(false))?.AllowMaintenance == true,
            ContinuityContext: null)
            { TenantManagementReadSources = tenantSkills.Sources, ExecutionContext = _boundAgentRun?.Admission.ExecutionContext, ApprovalOutcome = _tenantApprovalOutcome,
                Configuration = _boundAgentRun?.Admission.Configuration, CurrentContinuitySettings = await CurrentContinuitySettingsAsync(_boundAgentRun, ct).ConfigureAwait(false), RunTaskIntent = _boundAgentRun?.Admission.TaskIntent };
    }

    private async Task<CapabilityLoadMailboxResult> RequestCapabilityLoadAsync(EventContext cause, Guid responseId,
        string json, AgentContext context, CancellationToken ct, bool dialogRecovery = false)
    {
        var completed = new TaskCompletionSource<CapabilityLoadMailboxResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginWork();
        if (!TryMailbox(new CapabilityLoadRequested(WorkerContext(cause), responseId, cause.Epoch, json, context, ct, completed, dialogRecovery)))
        { EndWork(); return new(CapabilityLoadResult.Failure("stale", "load_stale", "This execution no longer owns discovery. Stop this attempt."), null, "load_stale"); }
        return await completed.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task HandleCapabilityLoadAsync(CapabilityLoadRequested input, CancellationToken ct)
    {
        var result = new CapabilityLoadMailboxResult(CapabilityLoadResult.Failure("stale", "load_stale", "This execution no longer owns discovery. Stop this attempt."), null, "load_stale");
        var matches = 0;
        try
        {
            if (input.RequestCancellation.IsCancellationRequested)
            {
                result = new(CapabilityLoadResult.Failure("cancelled", "load_cancelled", "Discovery was cancelled. Stop this attempt."), null, "load_cancelled");
                return;
            }
            if (_deactivated || _responseTerminal || _activeResponseId != input.ResponseId || _epoch != input.Epoch
                || input.Context.Epoch != _epoch
                || !await OwnsWorkerAsync(input.Context, input.ResponseId, ct).ConfigureAwait(false)) return;
            if (_boundAgentRun is not { Status: AgentRunStatus.Running, Claim: not null, CancellationRequested: false } bound
                || bound.ResponseId != input.ResponseId) return;
            var current = await _agentRuns.GetAsync(bound.Owner, bound.AgentRunId, ct);
            if (current is null || current.Revision != bound.Revision || current.Claim?.Generation != bound.Claim.Generation
                || current.CancellationRequested || current.Status != AgentRunStatus.Running) return;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, input.RequestCancellation);
            using var json = JsonDocument.Parse(input.ArgumentsJson);
            var context = input.ProjectionContext with {
                Definition = await ProjectionDefinitionAsync(ExecutionDefinition, current, _snapshot.AgentInstanceId, linked.Token).ConfigureAwait(false),
                Harness = await HarnessContextAsync(current, linked.Token).ConfigureAwait(false),
                LoadedCapabilityIds = current.LoadedCapabilityIds, ActiveSkillKeys = current.ActiveSkillKeys, PinnedSkillCatalog = current.PinnedSkillCatalog
            };
            CapabilityLoadResult plan;
            if (input.DialogRecovery)
            {
                if (!ToolCatalog.Eligible(context.Definition, context, _tools.ConfigurationGate).Any(t => t.Name == ToolCatalog.BrowserDialog)
                    || current.LoadedCapabilityIds.Contains(ToolCatalog.BrowserDialog)) return;
                // One exact registered recovery capability, with unchanged authorization/provider/trigger gates.
                plan = new([ToolCatalog.BrowserDialog], [], "load_matched");
            }
            else
            {
                if (!ToolPolicy.IsOffered(context.Definition, context, ToolCatalog.CapabilitiesLoad, _tools.ConfigurationGate))
                {
                    result = new(CapabilityLoadResult.Failure("forbidden", "load_unavailable", "Discovery is not eligible in this execution. Use offered tools or report the restriction."), null, "load_unavailable");
                    return;
                }
                plan = CapabilityDiscoveryMatcher.Load(context.Definition, context, _tools.ConfigurationGate, json.RootElement, current.CapabilityLoadCount);
            }
            matches = plan.Loaded.Count;
            var updated = plan.Outcome == "load_over_budget" ? current : await _agentRuns.ApplyAsync(current.Owner, current.AgentRunId,
                input.DialogRecovery ? new AgentRunCommand.ProjectBrowserDialog(current.Revision, _time.GetUtcNow(), current.Claim!.Generation)
                    : new AgentRunCommand.LoadCapabilities(current.Revision, _time.GetUtcNow(), current.Claim!.Generation, plan.Loaded), linked.Token);
            _boundAgentRun = updated;
            result = new(plan.ToJson(), updated.LoadedCapabilityIds, plan.Outcome);
        }
        catch (AgentCoreException e) when (e.Code is "Conflict" or "StaleCommand") { }
        catch (AgentCoreException) { result = new(CapabilityLoadResult.Failure("invalid", "load_invalid", "Use query (1–200 characters, no wildcard) and optional integer limit (1–8)."), null, "load_invalid"); }
        catch (JsonException) { result = new(CapabilityLoadResult.Failure("invalid", "load_invalid", "Use a JSON object with query and optional limit."), null, "load_invalid"); }
        catch (OperationCanceledException) { result = new(CapabilityLoadResult.Failure("cancelled", "load_cancelled", "Discovery was cancelled. Stop this attempt."), null, "load_cancelled"); }
        finally { RuntimeTelemetry.RecordCapabilityLoad(result.Outcome, matches); input.Completed.TrySetResult(result); }
    }
}
