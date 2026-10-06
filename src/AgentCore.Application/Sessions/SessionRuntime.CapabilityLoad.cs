using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Application.Observability;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private sealed record LiveOccurrenceCapabilities(Guid ResponseId, Guid Epoch, IReadOnlyList<string> Ids, int Calls);
    private LiveOccurrenceCapabilities? _liveOccurrenceCapabilities;

    private IReadOnlyList<string> LoadedCapabilitiesFor(Guid responseId) =>
        _boundConversationExecution?.ResponseId == responseId ? _boundConversationExecution.LoadedCapabilityIds
        : _liveOccurrenceCapabilities is { } live && live.ResponseId == responseId && live.Epoch == _epoch ? live.Ids : [];

    private int CapabilityLoadCountFor(Guid responseId) =>
        _boundConversationExecution?.ResponseId == responseId ? _boundConversationExecution.CapabilityLoadCount
        : _liveOccurrenceCapabilities is { } live && live.ResponseId == responseId && live.Epoch == _epoch ? live.Calls : 0;

    private async Task<AgentContext> CapabilityProjectionContextAsync(AgentTrigger trigger, ILanguageModel model,
        IReadOnlyList<string> skills, IReadOnlyList<string> loaded, CancellationToken ct) =>
        new(_snapshot.Definition, _snapshot.Entries, _snapshot.Summary, _profile, _snapshot.Mode, _snapshot.PendingTopic,
            false, null, trigger, SessionAttachments: await BuildSessionAttachmentManifestAsync(ct),
            ModelSupportsTools: model.Capabilities.Tools, ModelSupportsVision: model.Capabilities.Vision,
            ActiveSkillIds: skills, LoadedCapabilityIds: loaded, IntermediateMessagingAllowed: _intermediateMessagingAllowed,
            TrustedConnection: await LiveTrustedConnectionAsync(trigger.Kind, ct),
            Harness: await _tools.HarnessContextAsync(_snapshot.AgentInstanceId, ct),
            AgentWorkspaceAvailable: await _tools.AgentWorkspaceAvailableAsync(SessionId, ct),
            AllowAgentConsolidation: await _tools.AllowsAgentConsolidationAsync(_snapshot.AgentInstanceId, ct),
            ContinuityContext: await _tools.ContinuityContextAsync(_snapshot.AgentInstanceId, trigger.Text, SessionId, _snapshot.Definition, ct));

    private async Task<CapabilityLoadMailboxResult> RequestCapabilityLoadAsync(EventContext cause, Guid responseId,
        string json, AgentContext context, CancellationToken ct)
    {
        var completed = new TaskCompletionSource<CapabilityLoadMailboxResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginWork();
        if (!TryMailbox(new CapabilityLoadRequested(NewContext(cause.EventId), responseId, _epoch, json, context, ct, completed)))
        { EndWork(); return new(SkillLoadAdmission.Error("stale", "Capability load is no longer owned by this execution."), null, "load_stale"); }
        return await completed.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task HandleCapabilityLoadAsync(CapabilityLoadRequested input, CancellationToken ct)
    {
        var result = new CapabilityLoadMailboxResult(SkillLoadAdmission.Error("stale", "Capability load is no longer owned by this execution."), null, "load_stale");
        var matches = 0;
        try
        {
            if (_deactivated || _responseTerminal || _activeResponseId != input.ResponseId || _epoch != input.Epoch
                || input.Context.Epoch != _epoch || input.RequestCancellation.IsCancellationRequested) return;
            if (_activeResponseTriggerKind is { } occurrenceKind && ToolResources.IsOccurrence(occurrenceKind)
                && _liveOccurrenceCapabilities is { } live && live.ResponseId == input.ResponseId && live.Epoch == input.Epoch)
            {
                using var occurrenceToken = CancellationTokenSource.CreateLinkedTokenSource(ct, input.RequestCancellation);
                using var arguments = JsonDocument.Parse(input.ArgumentsJson);
                var occurrenceContext = input.ProjectionContext with { Definition = _snapshot.Definition, LoadedCapabilityIds = live.Ids,
                    TrustedConnection = await LiveTrustedConnectionAsync(occurrenceKind, occurrenceToken.Token) };
                if (!ToolPolicy.IsOffered(_snapshot.Definition, occurrenceContext, ToolCatalog.CapabilitiesLoad, _tools.ConfigurationGate)) return;
                var load = CapabilityDiscoveryMatcher.Load(_snapshot.Definition, occurrenceContext, _tools.ConfigurationGate, arguments.RootElement, live.Calls);
                occurrenceToken.Token.ThrowIfCancellationRequested();
                matches = load.Loaded.Count;
                var updatedLive = load.Outcome == "load_over_budget" ? live
                    : live with { Ids = live.Ids.Concat(load.Loaded).Distinct(StringComparer.Ordinal).ToArray(), Calls = live.Calls + 1 };
                _liveOccurrenceCapabilities = updatedLive;
                result = new(load.ToJson(), updatedLive.Ids, load.Outcome);
                return;
            }
            if (_activeResponseTriggerKind != TriggerKind.UserTurn
                || _boundConversationExecution is not { Status: ConversationTurnExecutionStatus.Running, Claim: not null, CancellationRequested: false } bound
                || bound.ResponseId != input.ResponseId || _turnExecutions is null) return;
            var current = await _turnExecutions.GetAsync(bound.ExecutionId, ct);
            if (current is null || current.Revision != bound.Revision || current.Claim?.Generation != bound.Claim.Generation
                || current.CancellationRequested || current.Status != ConversationTurnExecutionStatus.Running) return;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, input.RequestCancellation);
            using var json = JsonDocument.Parse(input.ArgumentsJson);
            var context = input.ProjectionContext with { Definition = _snapshot.Definition, LoadedCapabilityIds = current.LoadedCapabilityIds, ActiveSkillIds = current.PinnedActiveSkillIds };
            if (!ToolPolicy.IsOffered(_snapshot.Definition, context, ToolCatalog.CapabilitiesLoad, _tools.ConfigurationGate)) return;
            var plan = CapabilityDiscoveryMatcher.Load(_snapshot.Definition, context, _tools.ConfigurationGate, json.RootElement, current.CapabilityLoadCount);
            matches = plan.Loaded.Count;
            var updated = plan.Outcome == "load_over_budget" ? current : await _turnExecutions.AdmitCapabilitiesAsync(current.ExecutionId,
                current.Revision, current.Claim!.Generation, plan.Loaded, _time.GetUtcNow(), linked.Token);
            _boundConversationExecution = updated;
            result = new(plan.ToJson(), updated.LoadedCapabilityIds, plan.Outcome);
        }
        catch (AgentCoreException) { result = new(SkillLoadAdmission.Error("invalid", "Capability load arguments or admission were invalid."), null, "load_no_match"); }
        catch (JsonException) { result = new(SkillLoadAdmission.Error("invalid", "Capability load arguments were malformed."), null, "load_no_match"); }
        catch (OperationCanceledException) { }
        finally { RuntimeTelemetry.RecordCapabilityLoad(result.Outcome, matches); input.Completed.TrySetResult(result); }
    }
}
