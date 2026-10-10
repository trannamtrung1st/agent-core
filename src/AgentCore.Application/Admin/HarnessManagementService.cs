using AgentCore.Application.Observability;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Agents;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Admin;

public sealed record HarnessReview(
    Guid InstanceId, long InstanceRevision, int ActiveVersion, HarnessManagementState State,
    AgentDefinitionDraft? Draft, DefinitionDraftDiffResult? Diff,
    IReadOnlyList<AgentDefinitionDraftResource> Resources);

/// <summary>Agent-originated calls enter the same Authoring owners as Admin. No runtime state is mutated.</summary>
public sealed class HarnessManagementService(
    IAgentInstanceStore instances,
    IAgentDefinitionStore definitions,
    IRoleKnowledgeContentResolver roleKnowledge,
    IAgentDefinitionAdminStore definitionStore,
    AgentDefinitionLifecycleService lifecycle,
    AgentDefinitionResourceService resources,
    AgentDefinitionDraftValidationService validation,
    AgentDefinitionDraftEvaluationService evaluation,
    AgentDefinitionDraftDiffService diff,
    AgentDefinitionDraftPublishService publisher,
    AdminAgentInstanceService adminInstances,
    AdminLifecycleCoordinator gates,
    IToolConfigurationGate toolConfiguration,
    IIdGenerator ids,
    TimeProvider time,
 AgentInstanceSettingsService localSettings, AgentInstanceResourceService localResources,
    AgentCore.Application.Execution.AgentRunConfigurationResolver runConfigurations)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Bound(string value, int max) => value.Length <= max ? value : value[..max];
    public async ValueTask<HarnessReview> ReviewAsync(Guid instanceId, CancellationToken ct = default)
    {
        var instance = await RequireInstanceAsync(instanceId, ct);
        var state = instance.HarnessManagement ?? new(HarnessManagementPolicy.Disabled);
        AgentDefinitionDraft? draft = null;
        DefinitionDraftDiffResult? changes = null;
        IReadOnlyList<AgentDefinitionDraftResource> items = [];
        if (state.Preparation is { } preparation)
        {
            draft = await definitionStore.GetDraftAsync(preparation.DraftId, ct);
            if (draft is not null)
            {
                changes = await diff.GetDraftDiffAsync(draft.DraftId, ct);
                items = await resources.ListDraftResourcesAsync(draft.DraftId, ct);
            }
        }
        return new(instanceId, instance.Revision, instance.ActiveVersion, state, draft, changes, items);
    }

    public ValueTask<AgentInstance> ConfigureAsync(Guid instanceId, long expectedRevision,
        HarnessManagementPolicy policy, CancellationToken ct = default) =>
        gates.WithInstanceAsync(instanceId, async token =>
        {
            var instance = await RequireInstanceAsync(instanceId, token);
            RequireRevision(instance, expectedRevision);
            if (!Enum.IsDefined(policy.Mode) || policy.Scopes.Any(scope => !Enum.IsDefined(scope))
                || policy.Scopes.Count > 4 || policy.Sources.Count > 24 || policy.EligibleTools.Count > ToolRegistry.All.Count())
                throw AgentCoreErrors.Validation("Harness policy is invalid or exceeds its bounds.");
            foreach (var source in policy.Sources)
            {
                ValidateText(source, 2048, "source");
                if (!source.StartsWith("knowledge:", StringComparison.Ordinal) && !source.StartsWith("candidate:", StringComparison.Ordinal)
                    && (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)))
                    throw AgentCoreErrors.Validation("Sources must be approved knowledge, candidate resource identities, or credential-free public URLs.");
            }
            var active = await RequireDefinitionAsync(instance, token);
            var old = instance.HarnessManagement ?? new(HarnessManagementPolicy.Disabled);
            foreach (var tool in policy.EligibleTools)
                if ((!RolePermissions.AllowsTool(active, tool) && !old.Policy.EligibleTools.Contains(tool, StringComparer.Ordinal))
                    || tool is ToolCatalog.SkillsLoad or ToolCatalog.AppMessageSend or ToolCatalog.WorkComplete
                    || !ToolRegistry.TryGet(tool, out var descriptor) || descriptor.OfferRule == ToolOfferRule.HarnessAuthority || !toolConfiguration.IsConfigured(tool))
                    throw AgentCoreErrors.Validation("Only currently authorized, configured tools can be eligible.");
            // Policy changes invalidate every prior preparation grant. Re-enable must create a new fork.
            var next = old with
            {
                Policy = policy with { Scopes = policy.Scopes.Distinct().ToArray(), Sources = policy.Sources.Distinct().ToArray(), EligibleTools = policy.EligibleTools.Concat(old.Policy.EligibleTools).Concat(RoleEnvironments.Of(active).ToolList.Append(ToolCatalog.AttachmentsRead)).Where(t => ToolRegistry.TryGet(t, out var d) && d.OfferRule != ToolOfferRule.HarnessAuthority && toolConfiguration.IsConfigured(t)).Distinct().ToArray() },
                PolicyRevision = old.PolicyRevision + 1,
                Preparation = old.Preparation is null || old.Preparation.Status == HarnessPreparationStatus.Published ? old.Preparation : old.Preparation with
                {
                    Status = HarnessPreparationStatus.Cancelled,
                    Approvals = old.Preparation.Approvals.Select(a => a.Status == "Pending" ? a with { Status = "Cancelled" } : a).ToArray()
                }
            };
            return await SaveAsync(instance, next, "policy", policy.Frozen ? "Frozen" : policy.Mode.ToString(), false, token);
        }, ct);

    public ValueTask<AgentInstance> StartAsync(Guid instanceId, long expectedRevision, string purpose, CancellationToken ct = default) =>
        gates.WithInstanceAsync(instanceId, async token =>
        {
            var instance = await RequireInstanceAsync(instanceId, token);
            RequireRevision(instance, expectedRevision);
            return await StartCoreAsync(instance, purpose, token);
        }, ct);

    public ValueTask<AgentInstance> ProposeSharedAsync(Guid instanceId, long expectedRevision, string purpose,
        IReadOnlyDictionary<string, IReadOnlyList<string>> selectedFields, IReadOnlyList<string> resourceKeys, CancellationToken ct = default) =>
        gates.WithInstanceAsync(instanceId, async token =>
        {
            var instance = await RequireInstanceAsync(instanceId, token);
            RequireRevision(instance, expectedRevision);
            var baseline = await RequireDefinitionAsync(instance, token);
            var patch = new InstanceSettingsOverrides();
            foreach (var section in selectedFields)
            {
                var available = InstanceSettingsResolver.Overrides(instance.SettingsOverrides ?? new(), section.Key);
                if (section.Value.Distinct(StringComparer.Ordinal).Count() != section.Value.Count || section.Value.Any(field => !available.ContainsKey(field)))
                    throw AgentCoreErrors.Validation("Select only existing Instance overrides for shared promotion.");
                patch = InstanceSettingsResolver.Patch(patch, section.Key,
                    section.Value.ToDictionary(field => field, field => JsonSerializer.SerializeToElement(available[field])), []);
            }
            if (resourceKeys.Distinct(StringComparer.Ordinal).Count() != resourceKeys.Count || resourceKeys.Count > AgentResourceLimits.MaxItemsPerDraft)
                throw AgentCoreErrors.Validation("Selected resources are duplicated or exceed the quota.");
            var local = await localResources.ListAsync(instanceId, token);
            var selected = resourceKeys.Select(key => local.Resources.SingleOrDefault(r => r.Key == key && r.Origin == "Instance")
                ?? throw AgentCoreErrors.Validation("Shared promotion requires explicitly selected Instance-owned resources.")).ToArray();
            if (!selectedFields.Any(section => section.Value.Count > 0) && selected.Length == 0)
                throw AgentCoreErrors.Validation("Select at least one local override or resource.");
            var effective = InstanceSettingsResolver.Resolve(baseline, patch);
            instance = await StartCoreAsync(instance, purpose, token);
            var prep = instance.HarnessManagement!.Preparation!;
            var draft = await lifecycle.GetDraftAsync(prep.DraftId, token);
            var candidate = AgentDefinitionCandidate.FromDefinition(effective);
            // Keep the fork's already copied immutable resources. Only selected local content is added.
            foreach (var resource in selected)
            {
                var bytes = await localResources.ReadAsync(instanceId, resource.Key, token);
                if (DefinitionResourceContentHasher.ComputeSha256Hex(bytes) != resource.ContentSha256) throw AgentCoreErrors.Conflict("Selected resource changed. Review it again.");
                var stored = await resources.StoreDraftContentAsync(draft.DraftId, resource.MediaType, bytes, token);
                var path = "instance-promoted/" + resource.LogicalPath;
                await resources.UpsertDraftResourceAsync(draft.DraftId, draft.Revision, null, path, resource.Kind,
                    stored.MediaType, stored.ContentSha256, stored.ByteLength, token);
                draft = await lifecycle.GetDraftAsync(draft.DraftId, token);
                if (resource.Kind == AgentDefinitionResourceKind.Knowledge)
                    candidate = candidate with { Environment = RoleEnvironments.Of(candidate.ToPublished(1)) with { KnowledgeSources = [
                        .. RoleEnvironments.Of(candidate.ToPublished(1)).KnowledgeList,
                        new("promoted-" + AgentInstanceResourceService.ParseKey(resource.Key).Id.ToString("N"), resource.LogicalPath, path, path)] } };
            }
            draft = await lifecycle.UpdateDraftAsync(draft.DraftId, draft.Revision, candidate, token);
            var state = instance.HarnessManagement!;
            var evidence = new HarnessVerificationEvidence("Owner", draft.Revision, "Selected Instance promotion proposal", HarnessEvidenceStatus.PartiallyVerified,
                "Only explicitly selected local settings and resources enter a reusable draft.",
                $"Source Instance revision {expectedRevision}; {selectedFields.Sum(s => s.Value.Count)} fields and {selected.Length} resources selected.",
                "Review the diff and run candidate verification before explicitly publishing. External production effects are unverified.");
            return await SaveAsync(instance, state with { Preparation = prep with { Evidence = [evidence] } }, "sharedProposal", "Preparing", false, token);
        }, ct);

    private async ValueTask<AgentInstance> StartCoreAsync(AgentInstance instance, string purpose, CancellationToken ct)
    {
            var state = RequireEnabled(instance);
            ValidateText(purpose, 2000, "purpose");
            if (state.Preparation is { Status: HarnessPreparationStatus.Preparing or HarnessPreparationStatus.AwaitingApproval or HarnessPreparationStatus.Ready })
                throw AgentCoreErrors.Conflict("Cancel or finish the current preparation before starting another.");
            var effectiveSource = await lifecycle.GetEffectiveExactSourceAsync(instance.DefinitionId, instance.ActiveVersion, ct);
            var draft = await lifecycle.ForkDraftAsync(instance.DefinitionId, instance.ActiveVersion,
                effectiveSource.Kind, ct);
            var active = effectiveSource.Definition;
            if (effectiveSource.Kind == DefinitionDraftSourceKind.ForkBuiltIn)
            {
                // Built-in knowledge becomes ordinary Definition resources in the fork; publication never falls back to host files.
                foreach (var source in RoleEnvironments.Of(active).KnowledgeList)
                {
                    var text = await roleKnowledge.ReadContentAsync(active, source.Identity, ct);
                    if (text is null) throw AgentCoreErrors.Validation("Active knowledge cannot be copied to the candidate.");
                    var stored = await resources.StoreDraftContentAsync(draft.DraftId, "text/markdown", Encoding.UTF8.GetBytes(text), ct);
                    await resources.UpsertDraftResourceAsync(draft.DraftId, draft.Revision, null, KnowledgeSourcePaths.ResolveBackingPath(source),
                        AgentDefinitionResourceKind.Knowledge, stored.MediaType, stored.ContentSha256, stored.ByteLength, ct);
                    draft = await lifecycle.GetDraftAsync(draft.DraftId, ct);
                }
            }
            var preparation = new HarnessPreparation(ids.NewId(), draft.DraftId, instance.ActiveVersion, purpose,
                state.PolicyRevision, HarnessPreparationStatus.Preparing, [], []);
            return await SaveAsync(instance, state with { Preparation = preparation }, "start", "Preparing", false, ct);
    }

    public ValueTask<AgentInstance> CancelAsync(Guid instanceId, long expectedRevision, CancellationToken ct = default) =>
        gates.WithInstanceAsync(instanceId, async token =>
        {
            var instance = await RequireInstanceAsync(instanceId, token);
            RequireRevision(instance, expectedRevision);
            var state = instance.HarnessManagement ?? throw AgentCoreErrors.Validation("No preparation exists.");
            var prep = state.Preparation ?? throw AgentCoreErrors.Validation("No preparation exists.");
            if (prep.Status == HarnessPreparationStatus.Published)
                throw AgentCoreErrors.Conflict("A published preparation cannot be cancelled; start another fork for further work.");
            return await SaveAsync(instance, state with { Preparation = prep with
            {
                Status = HarnessPreparationStatus.Cancelled,
                Approvals = prep.Approvals.Select(a => a.Status == "Pending" ? a with { Status = "Cancelled" } : a).ToArray()
            } }, "cancel", "Cancelled", false, token);
        }, ct);

    public ValueTask<AgentInstance> RequestOperationAsync(Guid instanceId, Guid preparationId,
        HarnessAuthoringOperation operation, CancellationToken ct = default) =>
        gates.WithInstanceAsync(instanceId, async token =>
        {
            var instance = await RequireInstanceAsync(instanceId, token);
            var (state, prep, draft) = await RequireContextAsync(instance, preparationId, operation.DraftRevision, token);
            ValidateOperation(state.Policy, draft.Candidate, operation);
            if (state.Policy.Mode == HarnessManagementMode.Assisted || operation.Kind.StartsWith("tool.", StringComparison.Ordinal))
            {
                if (prep.Approvals.Count >= ToolLimits.MaxSteps) throw AgentCoreErrors.Validation("Approval budget exhausted.");
                var hash = ActionHash(prep, state.PolicyRevision, operation);
                if (prep.Approvals.Any(a => a.ActionHash == hash && a.Status == "Pending")) return instance;
                var approval = new HarnessAuthoringApproval(ids.NewId(), hash, operation, "Pending");
                return await SaveAsync(instance, state with { Preparation = prep with
                {
                    Status = HarnessPreparationStatus.AwaitingApproval,
                    Approvals = [.. prep.Approvals, approval]
                } }, operation.Kind, "AwaitingApproval", true, token);
            }
            await ApplyAsync(instance, draft, operation, token);
            return await SaveAsync(instance, state with { Preparation = prep with { Status = HarnessPreparationStatus.Preparing } },
                operation.Kind, "Applied", true, token);
        }, ct);

    public ValueTask<AgentInstance> DecideApprovalAsync(Guid instanceId, long expectedRevision,
        Guid approvalId, string actionHash, bool approve, CancellationToken ct = default) =>
        gates.WithInstanceAsync(instanceId, async token =>
        {
            var instance = await RequireInstanceAsync(instanceId, token);
            RequireRevision(instance, expectedRevision);
            var state = RequireEnabled(instance);
            var prep = state.Preparation ?? throw AgentCoreErrors.Validation("No candidate exists.");
            var approval = prep.Approvals.SingleOrDefault(a => a.ApprovalId == approvalId)
                ?? throw AgentCoreErrors.NotFound("Approval was not found.");
            if (approval.Status != "Pending" || actionHash != approval.ActionHash
                || actionHash != ActionHash(prep, state.PolicyRevision, approval.Operation))
                throw AgentCoreErrors.Conflict("Approval no longer matches the exact candidate operation.");
            if (approve)
            {
                var (_, _, draft) = await RequireContextAsync(instance, prep.PreparationId, approval.Operation.DraftRevision, token);
                ValidateOperation(state.Policy, draft.Candidate, approval.Operation);
                // Consume first: a crash cannot replay a side effect. The draft's CAS guards the other boundary.
                instance = await SaveAsync(instance, state with { Preparation = prep with
                {
                    Approvals = prep.Approvals.Select(a => a.ApprovalId == approvalId ? a with { Status = "Consumed" } : a).ToArray()
                } }, approval.Operation.Kind, "Consumed", false, token);
                await ApplyAsync(instance, draft, approval.Operation, token);
                state = instance.HarnessManagement!;
                prep = state.Preparation!;
            }
            var approvals = prep.Approvals.Select(a => a.ApprovalId == approvalId
                ? a with { Status = approve ? "Approved" : "Rejected" } : a).ToArray();
            return await SaveAsync(instance, state with { Preparation = prep with
            {
                Approvals = approvals,
                Status = approvals.Any(a => a.Status == "Pending") ? HarnessPreparationStatus.AwaitingApproval : HarnessPreparationStatus.Preparing
            } }, approval.Operation.Kind, approve ? "Approved" : "Rejected", false, token);
        }, ct);

    public ValueTask<AgentInstance> RecordAgentEvidenceAsync(Guid instanceId, Guid preparationId,
        HarnessVerificationEvidence evidence, CancellationToken ct = default) =>
        gates.WithInstanceAsync(instanceId, async token =>
        {
            var instance = await RequireInstanceAsync(instanceId, token);
            var (state, prep, _) = await RequireContextAsync(instance, preparationId, evidence.DraftRevision, token);
            if (!Enum.IsDefined(evidence.Status)) throw AgentCoreErrors.Validation("Invalid evidence status.");
            ValidateText(evidence.Check, 200, "check");
            ValidateText(evidence.Expected, 2000, "expected");
            ValidateText(evidence.Observed, 2000, "observed");
            if (evidence.Limitation is not null) ValidateText(evidence.Limitation, 2000, "limitation");
            if (prep.Evidence.Count >= 64) throw AgentCoreErrors.Validation("Evidence budget exhausted.");
            return await SaveAsync(instance, state with { Preparation = prep with
            {
                Evidence = [.. prep.Evidence, evidence with { Actor = "Agent" }],
                Status = StatusAfterEvidence(prep, evidence)
            } }, "agentEvidence", evidence.Status.ToString(), true, token);
        }, ct);

    public ValueTask<AgentInstance> TestKnowledgeAsync(Guid instanceId, Guid preparationId, string identity, string expectedText, CancellationToken ct = default) =>
        gates.WithInstanceAsync(instanceId, async token =>
        {
            var instance = await RequireInstanceAsync(instanceId, token);
            var review = await ReviewAsync(instanceId, token);
            var (state, prep, draft) = await RequireContextAsync(instance, preparationId, review.Draft?.Revision ?? 0, token);
            ValidateText(expectedText, 500, "expected text");
            var source = draft.Candidate.Environment?.KnowledgeList.SingleOrDefault(k => k.Identity == identity);
            var item = source is null ? null : review.Resources.SingleOrDefault(r => r.LogicalPath == KnowledgeSourcePaths.ResolveBackingPath(source));
            var bytes = item is null ? null : await resources.ReadDraftResourceContentAsync(draft.DraftId, item.ResourceId, token);
            var matched = bytes is not null && Encoding.UTF8.GetString(bytes).Contains(expectedText, StringComparison.Ordinal);
            var check = new HarnessVerificationEvidence("Core", draft.Revision, "Candidate knowledge readback", matched ? HarnessEvidenceStatus.Verified : HarnessEvidenceStatus.Failed,
                "Read bound candidate knowledge and find the expected excerpt.", matched ? "Content readback matched the expected excerpt and source binding." : "Expected content was unavailable or did not match.");
            return await SaveAsync(instance, state with { Preparation = prep with { Evidence = [.. prep.Evidence.TakeLast(63), check], Status = StatusAfterEvidence(prep, check) } },
                "knowledgeReadback", check.Status.ToString(), true, token);
        }, ct);

    public ValueTask<AgentInstance> VerifyAsync(Guid instanceId, Guid preparationId, CancellationToken ct = default, bool agent = false) =>
        gates.WithInstanceAsync(instanceId, async token =>
        {
            var instance = await RequireInstanceAsync(instanceId, token);
            return await VerifyCoreAsync(instance, preparationId, token, agent);
        }, ct);

    private async ValueTask<AgentInstance> VerifyCoreAsync(AgentInstance instance, Guid preparationId, CancellationToken ct, bool agent)
    {
            var review = await ReviewAsync(instance.InstanceId, ct);
            var (state, prep, draft) = await RequireContextAsync(instance, preparationId, review.Draft?.Revision ?? 0, ct);
            var checks = await validation.ValidateDraftAsync(draft.DraftId, ct);
            var evidence = prep.Evidence.Where(e => e.Actor != "Core" || e.DraftRevision != draft.Revision || e.Check is "Candidate knowledge readback").ToList();
            evidence.Add(new("Core", draft.Revision, "Structure and resource policy", checks.HasBlockingFindings ? HarnessEvidenceStatus.Failed : HarnessEvidenceStatus.Verified,
                "Candidate passes existing Definition and resource validation.", checks.HasBlockingFindings
                    ? string.Join("; ", checks.Findings.Select(f => f.Message)) : "Validation passed."));
            evidence.Add(new("Core", draft.Revision, "Authoring authority", HarnessEvidenceStatus.Verified,
                "Policy, instance, preparation and candidate revision match.", agent
                    ? "Current grant and selected base version match; shared publication requires the explicit promotion gate."
                    : "Current grant and active base version match; Admin controls this preparation."));
            evidence.Add(new("Core", draft.Revision, "Tool approvals", prep.Approvals.Any(a => a.Status is "Pending" or "Consumed")
                ? HarnessEvidenceStatus.RequiresExternalEvidence : HarnessEvidenceStatus.Verified,
                "No unresolved authoring or tool change approvals.", prep.Approvals.Any(a => a.Status is "Pending" or "Consumed") ? "Owner decisions or recovery required." : "No unresolved approvals."));
            foreach (var scenario in await evaluation.ListScenariosAsync(draft.DraftId, ct))
            {
                var result = await evaluation.RunScenarioAsync(draft.DraftId, scenario.ScenarioId, ct);
                evidence.Add(new("Core", draft.Revision, scenario.Title, result.Passed ? HarnessEvidenceStatus.Verified : HarnessEvidenceStatus.Failed,
                    scenario.Prompt, result.Passed ? "Synthetic representative scenario passed." : string.Join("; ", result.Findings)));
            }
            var status = checks.HasBlockingFindings || evidence.Any(e => e.DraftRevision == draft.Revision && e.Status == HarnessEvidenceStatus.Failed)
                ? HarnessPreparationStatus.Failed : prep.Approvals.Any(a => a.Status is "Pending" or "Consumed")
                    ? HarnessPreparationStatus.AwaitingApproval : evidence.Any(e => (e.Actor == "Agent" || !agent && e.Actor == "Owner" && e.Check == "Selected Instance promotion proposal") && e.DraftRevision == draft.Revision)
                        ? HarnessPreparationStatus.Ready : HarnessPreparationStatus.Preparing;
            return await SaveAsync(instance, state with { Preparation = prep with { Evidence = evidence.TakeLast(64).ToArray(), Status = status } },
                "verify", status.ToString(), agent, ct);
    }

    public ValueTask<AgentInstance> PromoteAsync(Guid instanceId, long expectedRevision, long draftRevision,
        CancellationToken ct = default) => gates.WithInstanceAsync(instanceId, async token =>
        {
            var instance = await RequireInstanceAsync(instanceId, token);
            RequireRevision(instance, expectedRevision);
            return await PromoteCoreAsync(instance, draftRevision, token);
        }, ct);

    private async ValueTask<AgentInstance> PromoteCoreAsync(AgentInstance instance, long draftRevision, CancellationToken ct, bool agent = false)
    {
            var state = RequireEnabled(instance);
            var prep = state.Preparation ?? throw AgentCoreErrors.Validation("No candidate exists.");
            if (prep.Status != HarnessPreparationStatus.Ready
                || prep.Evidence.Any(e => e.DraftRevision == draftRevision && e.Status == HarnessEvidenceStatus.Failed)
                || prep.Approvals.Any(a => a.Status is "Pending" or "Consumed")
                || !(prep.Evidence.Any(e => (e.Actor == "Agent" || !agent && e.Actor == "Owner" && e.Check == "Selected Instance promotion proposal") && e.DraftRevision == draftRevision)
                    || agent && prep.Evidence.Any(e => e.Actor == "Core" && e.DraftRevision == draftRevision
                        && e.Check == "Candidate knowledge readback" && e.Status == HarnessEvidenceStatus.Verified))
                || !prep.Evidence.Any(e => e.Actor == "Core" && e.DraftRevision == draftRevision && e.Check == "Structure and resource policy" && e.Status == HarnessEvidenceStatus.Verified))
                throw AgentCoreErrors.Validation("Review current verification and resolve approvals before promotion.");
            await RequireContextAsync(instance, prep.PreparationId, draftRevision, ct);
            var reviewedDiff = await diff.GetDraftDiffAsync(prep.DraftId, ct);
            var publication = await publisher.PublishDraftAsync(prep.DraftId, draftRevision, ct, agent ? AdminEventActorKind.Agent : AdminEventActorKind.LocalOwner);
            // Existing adoption commits the active version, preparation result and owner history together.
            // A failure can leave an unused immutable publication, but the instance stays on its old version/state.
            var publishedState = state with { Preparation = prep with
            {
                Status = HarnessPreparationStatus.Published, PublishedVersion = publication.Version,
                PublishedDraftRevision = draftRevision,
                PublishedChanges = reviewedDiff.Sections.Select(s => new HarnessPublishedChange(s.SectionId, s.Label,
                    s.ChangeKind.ToString(), s.BeforeSummary, s.AfterSummary)).ToArray()
            } };
            return await adminInstances.ReassociateActiveVersionAsync(instance.InstanceId, publication.Version, instance.Revision, ct, publishedState, agent ? AdminEventActorKind.Agent : AdminEventActorKind.LocalOwner);
    }

    public async ValueTask<HarnessChatContext?> ChatContextAsync(Guid instanceId, CancellationToken ct)
    {
        var instance = await instances.FindAsync(instanceId, ct);
        if (instance is null || instance.Lifecycle != AgentInstanceLifecycle.Active
            || instance.HarnessManagement is not { } state || state.Policy.Frozen || state.Policy.Mode == HarnessManagementMode.Disabled) return null;
        return new(state.Policy, state.PolicyRevision, instance.ActiveVersion);
    }

    public async ValueTask<object> InspectChatAsync(Guid instanceId, int currentRunDefinitionVersion, CancellationToken ct)
    {
        var instance = await RequireInstanceAsync(instanceId, ct);
        var state = RequireEnabled(instance);
        var active = (await runConfigurations.ResolveAsync(instanceId, ct)).Configuration.Definition;
        return new
        {
            activeDefinitionVersion = instance.ActiveVersion, policyRevision = state.PolicyRevision,
            currentRunDefinitionVersion = currentRunDefinitionVersion, instanceRevision = instance.Revision, changesApplyToNextRun = true,
            mode = state.Policy.Mode.ToString(), scopes = state.Policy.Scopes.Select(s => s.ToString()),
            authoringEligibleTools = state.Policy.EligibleTools.Where(t => toolConfiguration.IsConfigured(t)),
            instructions = ToolJsonResults.ClipUtf8Prefix(active.SystemInstructions, 1024),
            instructionsTruncated = System.Text.Encoding.UTF8.GetByteCount(active.SystemInstructions) > 1024,
            knowledge = RoleEnvironments.Of(active).KnowledgeList,
            skills = active.SkillList, activeDefinitionAuthorizedCapabilities = RoleEnvironments.Of(active).ToolList,
            activation = "Changes customize this Instance only and apply to the next new AgentRun, including existing Sessions. Current Runs remain unchanged. Shared Definition promotion requires separate owner review.",
            authoringGuide = "Inspect, read relevant external source material using authorized ordinary tools, then call the offered operation. Correct named validation errors. Reinspect on conflicts. Report saved only after a successful tool result."

        };
    }

    public ValueTask<object> AuthorChatAsync(Guid instanceId, string toolName, JsonElement args,
        ToolApprovalGrant? approval, IReadOnlyList<HarnessSourceReceipt> sourceReceipts, string? ownerText,
        CancellationToken ct) => gates.WithInstanceAsync<object>(instanceId, async token =>
        {
            var instance = await RequireInstanceAsync(instanceId, token);
            var state = RequireEnabled(instance);
            var context = new HarnessChatContext(state.Policy, state.PolicyRevision, instance.ActiveVersion);
            if (!HarnessChatTools.Allows(toolName, context)) throw AgentCoreErrors.Validation("This harness area is not granted.");
            if (!args.TryGetProperty("expectedInstanceRevision", out var instanceRevision) || !instanceRevision.TryGetInt64(out var expectedInstanceRevision) || expectedInstanceRevision != instance.Revision)
                throw AgentCoreErrors.Conflict("Instance revision changed; inspect and request a fresh operation.");
            if (!args.TryGetProperty("expectedVersion", out var version) || !version.TryGetInt32(out var expectedVersion)
                || !args.TryGetProperty("policyRevision", out var revision) || !revision.TryGetInt64(out var policyRevision)
                || expectedVersion != instance.ActiveVersion || policyRevision != state.PolicyRevision)
                throw AgentCoreErrors.Conflict("Harness authority or active version changed; inspect and request a fresh operation.");
            if (HarnessChatTools.NeedsApproval(toolName, context) && (approval is null || approval.ToolName != toolName
                || approval.ActionHash != ToolActionHash.Compute(toolName, args)))
                throw AgentCoreErrors.Conflict("This exact harness change requires owner approval in Chat.");
            HarnessAuthoringOperation operation;
            try
            {
                operation = JsonSerializer.Deserialize<HarnessAuthoringOperation>(args.GetRawText(), Json)
                    ?? throw AgentCoreErrors.Validation("A semantic operation is required.");
            }
            catch (JsonException)
            {
                throw AgentCoreErrors.Validation("Payload field types do not match the tool schema. "
                    + "Use the required fields and types shown by this tool.");
            }
            operation = operation with { Kind = HarnessChatTools.Operations[toolName].Kind };
            var active = await RequireDefinitionAsync(instance, token);

            string Read(string name) => args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()! : throw AgentCoreErrors.Validation($"{name} is required.");
            var hasExpected = args.TryGetProperty("expected", out _);
            var hasObserved = args.TryGetProperty("observed", out _);
            if (operation.Kind == "skill.upsert" && hasExpected != hasObserved)
                throw AgentCoreErrors.Validation("Supply both expected and observed for an Agent assessment, or omit both and use Core knowledge verification.");
            var expected = hasExpected ? Read("expected") : null;
            var observed = hasObserved ? Read("observed") : null;
            if (operation.Kind != "skill.upsert" && (expected is null || observed is null))
                throw AgentCoreErrors.Validation("expected and observed are required.");
            if (expected is not null) ValidateText(expected, 2000, "expected");
            if (observed is not null) ValidateText(observed, 2000, "observed");
            var limitation = args.TryGetProperty("limitation", out var limit) && limit.ValueKind == JsonValueKind.String
                ? limit.GetString() : operation.Kind == "skill.upsert" && expected is null
                    ? "Core verification does not establish production outcomes or subjective procedure quality."
                    : "Agent assessment is partial; production effects and subjective procedure quality require external evidence.";
            if (limitation is not null) ValidateText(limitation, 2000, "limitation");
            var sourceRead = operation.Kind != "knowledge.upsert" || operation.Source == "conversation:user" && !string.IsNullOrWhiteSpace(ownerText)
                || sourceReceipts.Any(r => r.Source == operation.Source);
            if (!sourceRead) throw AgentCoreErrors.Validation("Read the source with an authorized ordinary tool in this turn before retaining it; owner-provided material uses conversation:user.");
            if (operation.Kind is "tool.select" or "tool.configure" && (operation.Id is null || !toolConfiguration.IsConfigured(operation.Id)))
                throw AgentCoreErrors.Validation("The proposed tool is not currently configured.");
            // Validate before any Instance mutation. Shared publications are outside ordinary Chat authority.
            ValidateOperation(state.Policy, AgentDefinitionCandidate.FromDefinition(active), operation, sourceRead);
            var receipt = new HarnessInstanceChange(ids.NewId(), operation.Kind, instance.Revision + 1,
                instance.ActiveVersion, state.PolicyRevision, approval?.ApprovalId, ToolActionHash.Compute(toolName, args), operation.Source,
                time.GetUtcNow(), new("Agent", instance.Revision + 1, "Instance self-management", HarnessEvidenceStatus.PartiallyVerified,
                    expected ?? "Typed operation succeeds.", observed ?? "Instance content saved.", limitation));
            var localState = state with { InstanceChanges = [.. (state.InstanceChanges ?? []).TakeLast(63), receipt] };
            switch (operation.Kind)
            {
                case "instructions.update":
                    await localSettings.PatchAsync(instanceId, "instructions", instance.Revision,
                        new Dictionary<string, JsonElement> { ["systemInstructions"] = JsonSerializer.SerializeToElement(operation.Content!) }, [], token, AdminEventActorKind.Agent, localState);
                    break;
                case "tool.select":
                    var effective = InstanceSettingsResolver.Resolve(active, instance.SettingsOverrides);
                    var selected = RoleEnvironments.Of(effective).ToolList;
                    var next = operation.Enabled == true ? selected.Append(operation.Id!).Distinct(StringComparer.Ordinal).ToArray()
                        : selected.Where(t => t != operation.Id).ToArray();
                    await localSettings.PatchAsync(instanceId, "capabilities", instance.Revision,
                        new Dictionary<string, JsonElement> { ["selected"] = JsonSerializer.SerializeToElement(next) }, [], token, AdminEventActorKind.Agent, localState);
                    break;
                case "tool.configure":
                    await localSettings.PatchAsync(instanceId, "capabilities", instance.Revision,
                        new Dictionary<string, JsonElement> { ["allowUnreadUnsupportedTypes"] = JsonSerializer.SerializeToElement(operation.AllowUnreadUnsupportedTypes!.Value) }, [], token, AdminEventActorKind.Agent, localState);
                    break;
                case "knowledge.upsert":
                    var collection = await localResources.ListAsync(instanceId, token);
                    var existing = collection.Resources.SingleOrDefault(r => r.Origin == "Instance" && r.LogicalPath == "knowledge/" + operation.Id);
                    await localResources.UpsertAsync(instanceId, instance.Revision, existing is null ? null : AgentInstanceResourceService.ParseKey(existing.Key).Id,
                        existing?.Revision, "knowledge/" + operation.Id, AgentDefinitionResourceKind.Knowledge, "text/markdown",
                        Encoding.UTF8.GetBytes(operation.Content!), true, SkillAuthor.Agent, token, harnessManagement: localState);
                    break;
                case "knowledge.remove":
                    var catalog = await localResources.ListAsync(instanceId, token);
                    var source = RoleEnvironments.Of(active).KnowledgeList.SingleOrDefault(k => k.Identity == operation.Id);
                    var item = catalog.Resources.SingleOrDefault(r => r.Key == operation.Id)
                        ?? catalog.Resources.SingleOrDefault(r => r.Origin == "Instance" && r.LogicalPath == "knowledge/" + operation.Id)
                        ?? (source is null ? null : catalog.Resources.SingleOrDefault(r => r.Origin == "Definition" && r.LogicalPath == KnowledgeSourcePaths.ResolveBackingPath(source)))
                        ?? throw AgentCoreErrors.NotFound("Knowledge resource was not found. Inspect its trusted source identity or origin-qualified key.");
                    if (item.Kind != AgentDefinitionResourceKind.Knowledge) throw AgentCoreErrors.Validation("Only Knowledge resources can be removed through knowledge.remove.");
                    if (item.Origin == "Instance") await localResources.DeleteAsync(instanceId, item.Key, instance.Revision, item.Revision, SkillAuthor.Agent, token, localState);
                    else await localResources.SetEnabledAsync(instanceId, item.Key, instance.Revision, item.Revision, false, SkillAuthor.Agent, token, localState);
                    break;
                default: throw AgentCoreErrors.Validation("Use the ordinary skills tools for Instance Skill changes.");
            }
            var updated = await RequireInstanceAsync(instanceId, token);
            return new { saved = true, changed = updated.Revision != instance.Revision, instanceRevision = updated.Revision,
                activeVersion = updated.ActiveVersion, owner = "instance", appliesTo = "next new AgentRun, including existing Sessions",
                currentRunUnchanged = true, verification = "Typed policy/content validation passed; Agent assessment is partial.", limitation };

        }, ct);

    public async ValueTask<object> InspectAsync(Guid instanceId, Guid preparationId, CancellationToken ct = default)
    {
        var instance = await RequireInstanceAsync(instanceId, ct);
        var review = await ReviewAsync(instanceId, ct);
        var (state, prep, draft) = await RequireContextAsync(instance, preparationId, review.Draft?.Revision ?? 0, ct);
        return new
        {
            instanceId, activeVersion = instance.ActiveVersion, preparationId, draftId = draft.DraftId, draftRevision = draft.Revision,
            purpose = prep.Purpose, mode = state.Policy.Mode.ToString(), scopes = state.Policy.Scopes.Select(s => s.ToString()),
            permittedSources = state.Policy.Sources, eligibleTools = state.Policy.EligibleTools,
            instructions = draft.Candidate.SystemInstructions, skills = draft.Candidate.SkillList,
            knowledge = draft.Candidate.Environment?.KnowledgeList ?? [], selectedTools = draft.Candidate.Environment?.ToolList ?? [],
            resources = review.Resources.Select(r => new { r.ResourceId, r.LogicalPath, r.Kind, r.ByteLength }),
            evidence = prep.Evidence, approvals = prep.Approvals.Select(a => new { a.ApprovalId, a.Operation.Kind, a.Status })
        };
    }

    public async ValueTask<byte[]> ReadCandidateResourceAsync(Guid instanceId, Guid preparationId, Guid resourceId, CancellationToken ct)
    {
        var review = await ReviewAsync(instanceId, ct);
        await RequireContextAsync(await RequireInstanceAsync(instanceId, ct), preparationId, review.Draft?.Revision ?? 0, ct);
        return await resources.ReadDraftResourceContentAsync(review.Draft!.DraftId, resourceId, ct)
            ?? throw AgentCoreErrors.NotFound("Resource content was not found.");
    }

    public ValueTask<AgentInstance> CheckpointBudgetAsync(Guid instanceId, Guid preparationId, int steps, int outputBytes, long remainingMs, CancellationToken ct) =>
        gates.WithInstanceAsync(instanceId, async token =>
        {
            var instance = await RequireInstanceAsync(instanceId, token);
            var review = await ReviewAsync(instanceId, token);
            var (state, prep, _) = await RequireContextAsync(instance, preparationId, review.Draft?.Revision ?? 0, token);
            if (steps < prep.StepsUsed || steps > ToolLimits.MaxSteps || outputBytes < prep.OutputBytes || outputBytes > ToolLimits.MaxOutputBytes
                || remainingMs <= 0 || remainingMs > prep.RemainingOverallBudgetMs)
                throw AgentCoreErrors.Validation("Preparation execution budget exhausted or stale.");
            return await SaveAsync(instance, state with { Preparation = prep with { StepsUsed = steps, OutputBytes = outputBytes, RemainingOverallBudgetMs = remainingMs } },
                "executionBudget", "Checkpointed", true, token);
        }, ct);

    public ValueTask<AgentInstance> MarkFailureAsync(Guid instanceId, Guid preparationId, Guid? diagnosticId, bool cancelled, CancellationToken ct) =>
        gates.WithInstanceAsync(instanceId, async token =>
        {
            var instance = await RequireInstanceAsync(instanceId, token);
            var state = instance.HarnessManagement;
            if (state?.Preparation is not { } prep || prep.PreparationId != preparationId
                || prep.Status is HarnessPreparationStatus.Cancelled or HarnessPreparationStatus.Published) return instance;
            return await SaveAsync(instance, state with { Preparation = prep with
            {
                Status = cancelled ? HarnessPreparationStatus.Cancelled : HarnessPreparationStatus.Failed,
                DiagnosticId = diagnosticId
            } }, "execution", cancelled ? "Cancelled" : "Failed", true, token);
        }, ct);

    private async ValueTask ApplyAsync(AgentInstance instance, AgentDefinitionDraft draft, HarnessAuthoringOperation operation, CancellationToken ct)
    {
        var candidate = draft.Candidate;
        var environment = candidate.Environment ?? RoleEnvironment.Empty;
        switch (operation.Kind)
        {
            case "knowledge.upsert":
                var path = KnowledgeSourcePaths.ResolveBackingPath(operation.Id!, null);
                var existing = (await resources.ListDraftResourcesAsync(draft.DraftId, ct)).SingleOrDefault(r => r.LogicalPath == path);
                var stored = await resources.StoreDraftContentAsync(draft.DraftId, "text/markdown", Encoding.UTF8.GetBytes(operation.Content!), ct);
                await resources.UpsertDraftResourceAsync(draft.DraftId, draft.Revision, existing?.ResourceId, path,
                    AgentDefinitionResourceKind.Knowledge, stored.MediaType, stored.ContentSha256, stored.ByteLength, ct, history: DraftEvent(instance, draft, operation.Kind, "ResourceCommitted"));
                var current = await lifecycle.GetDraftAsync(draft.DraftId, ct);
                if (current.Revision != draft.Revision + 1)
                    throw AgentCoreErrors.Conflict("Candidate changed during the resource update; inspect again.");
                await lifecycle.UpdateDraftAsync(draft.DraftId, current.Revision, current.Candidate with { Environment = environment with
                {
                    KnowledgeSources = [.. environment.KnowledgeList.Where(k => k.Identity != operation.Id),
                        new(operation.Id!, operation.Id!, operation.Source!, path)]
                } }, ct, history: DraftEvent(instance, current, operation.Kind, "CandidateCommitted"));
                break;
            case "knowledge.remove":
                var reference = environment.KnowledgeList.SingleOrDefault(k => k.Identity == operation.Id)
                    ?? throw AgentCoreErrors.NotFound("Knowledge source was not found.");
                var resource = (await resources.ListDraftResourcesAsync(draft.DraftId, ct)).SingleOrDefault(r => r.LogicalPath == reference.ResourcePath);
                if (resource is not null) await resources.RemoveDraftResourceAsync(draft.DraftId, draft.Revision, resource.ResourceId, ct, history: DraftEvent(instance, draft, operation.Kind, "ResourceRemoved"));
                var afterRemove = await lifecycle.GetDraftAsync(draft.DraftId, ct);
                if (afterRemove.Revision != draft.Revision + (resource is null ? 0 : 1))
                    throw AgentCoreErrors.Conflict("Candidate changed during the resource removal; inspect again.");
                await lifecycle.UpdateDraftAsync(draft.DraftId, afterRemove.Revision, afterRemove.Candidate with { Environment = environment with
                    { KnowledgeSources = environment.KnowledgeList.Where(k => k.Identity != operation.Id).ToArray() } }, ct, history: DraftEvent(instance, afterRemove, operation.Kind, "CandidateCommitted"));
                break;
            case "instructions.update":
                await lifecycle.UpdateDraftAsync(draft.DraftId, draft.Revision, candidate with { SystemInstructions = operation.Content! }, ct, history: DraftEvent(instance, draft, operation.Kind, "CandidateCommitted"));
                break;
            case "tool.select":
                await lifecycle.UpdateDraftAsync(draft.DraftId, draft.Revision, candidate with { Environment = environment with
                {
                    ToolAllowlist = environment.Capabilities is null ? (operation.Enabled == true ? environment.ToolList.Append(operation.Id!).Distinct().ToArray()
                        : environment.ToolList.Where(t => t != operation.Id).ToArray()) : null,
                    Capabilities = environment.Capabilities is null ? null : new CapabilityAuthorization("Selected", operation.Enabled == true
                        ? environment.ToolList.Append(operation.Id!).Distinct().ToArray() : environment.ToolList.Where(t => t != operation.Id).ToArray()),
                    Projection = environment.Projection is null ? null : new CapabilityProjectionPolicy(environment.Projection.AlwaysCapabilities.Where(t => operation.Enabled == true || t != operation.Id).ToArray())
                } }, ct, history: DraftEvent(instance, draft, operation.Kind, "CandidateCommitted"));
                break;
            case "tool.configure":
                await lifecycle.UpdateDraftAsync(draft.DraftId, draft.Revision, candidate with { Environment = environment with
                    { Attachments = new(operation.AllowUnreadUnsupportedTypes!.Value) } }, ct, history: DraftEvent(instance, draft, operation.Kind, "CandidateCommitted"));
                break;
            default: throw AgentCoreErrors.Validation("Unknown semantic authoring operation.");
        }
    }

    private static HarnessPreparationStatus StatusAfterEvidence(HarnessPreparation prep, HarnessVerificationEvidence evidence) =>
        evidence.Status == HarnessEvidenceStatus.Failed ? HarnessPreparationStatus.Failed
            : prep.Status == HarnessPreparationStatus.Ready ? HarnessPreparationStatus.Preparing : prep.Status;

    private static void ValidateOperation(HarnessManagementPolicy policy, AgentDefinitionCandidate candidate, HarnessAuthoringOperation op, bool sourceRead = false)
    {
        var scope = op.Kind switch
        {
            "knowledge.upsert" or "knowledge.remove" => HarnessManagementScope.KnowledgeResources,
            "instructions.update" => HarnessManagementScope.Instructions,
            "tool.select" or "tool.configure" => HarnessManagementScope.ToolSelection,
            _ => throw AgentCoreErrors.Validation("Unknown semantic authoring operation.")
        };
        if (!policy.Allows(scope)) throw AgentCoreErrors.Validation("This authoring scope is not granted.");
        if (op.Kind is "knowledge.upsert" or "knowledge.remove")
        {
            ValidateText(op.Id, 80, "knowledge identity");
            if (op.Kind == "knowledge.remove" && op.Id!.Contains(':')) _ = AgentInstanceResourceService.ParseKey(op.Id);
            else if (op.Id!.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
                throw AgentCoreErrors.Validation("Knowledge identity must be a simple name.");
        }
        if (op.Kind == "knowledge.upsert")
        {
            ValidateText(op.Content, 32000, "knowledge content");
            ValidateText(op.Source, 2048, "knowledge provenance");
            if (Uri.TryCreate(op.Source, UriKind.Absolute, out var sourceUri) && sourceUri.Scheme is "http" or "https" && !string.IsNullOrEmpty(sourceUri.UserInfo))
                throw AgentCoreErrors.Validation("Knowledge provenance cannot contain URL credentials.");
            if (op.Source is null || (!sourceRead && !policy.Sources.Contains(op.Source, StringComparer.Ordinal)))
                throw AgentCoreErrors.Validation("Knowledge provenance must name an owner-permitted source.");
        }

        if (op.Kind == "instructions.update") ValidateText(op.Content, 32000, "instructions");
        if (op.Kind.StartsWith("tool.", StringComparison.Ordinal))
        {
            if (op.Id is null || !policy.EligibleTools.Contains(op.Id, StringComparer.Ordinal))
                throw AgentCoreErrors.Validation("Tool is outside the eligible authorized catalog.");
            if (op.Kind == "tool.select" && op.Id == ToolCatalog.AttachmentsRead) throw AgentCoreErrors.Validation("Attachment readability uses a configuration proposal, not tool selection.");
            if (op.Kind == "tool.select" && op.Enabled is null) throw AgentCoreErrors.Validation("enabled is required.");
            if (op.Kind == "tool.configure" && (op.Id != ToolCatalog.AttachmentsRead || op.AllowUnreadUnsupportedTypes is null))
                throw AgentCoreErrors.Validation("Only the existing attachment readability setting is configurable.");
        }
    }

    private async ValueTask<(HarnessManagementState State, HarnessPreparation Preparation, AgentDefinitionDraft Draft)> RequireContextAsync(
        AgentInstance instance, Guid preparationId, long revision, CancellationToken ct)
    {
        var state = RequireEnabled(instance);
        var prep = state.Preparation;
        if (prep is null || prep.PreparationId != preparationId || prep.PolicyRevision != state.PolicyRevision
            || prep.BaseVersion != instance.ActiveVersion || prep.Status is HarnessPreparationStatus.Cancelled or HarnessPreparationStatus.Published)
            throw AgentCoreErrors.Conflict("Preparation grant is stale, cancelled, or already promoted.");
        var draft = await lifecycle.GetDraftAsync(prep.DraftId, ct);
        if (draft.DefinitionId != instance.DefinitionId || draft.SourceVersion != prep.BaseVersion || draft.Revision != revision)
            throw AgentCoreErrors.Conflict("Candidate revision or base version is stale; inspect again.");
        return (state, prep, draft);
    }

    private static HarnessManagementState RequireEnabled(AgentInstance instance)
    {
        var state = instance.HarnessManagement;
        if (state is null || state.Policy.Mode == HarnessManagementMode.Disabled || state.Policy.Frozen)
            throw AgentCoreErrors.Validation("Harness management is disabled or frozen.");
        return state;
    }

    private async ValueTask<AgentInstance> RequireInstanceAsync(Guid id, CancellationToken ct)
    {
        var instance = await instances.FindAsync(id, ct) ?? throw AgentCoreErrors.NotFound("Agent instance was not found.");
        if (instance.Lifecycle != AgentInstanceLifecycle.Active)
            throw AgentCoreErrors.Validation("Harness management requires an active managed Agent Instance.");
        return instance;
    }

    private async ValueTask<AgentDefinition> RequireDefinitionAsync(AgentInstance instance, CancellationToken ct) =>
        await definitions.GetAsync(instance.DefinitionId, instance.ActiveVersion, ct) ?? throw AgentCoreErrors.NotFound("Active Definition was not found.");

    private static void RequireRevision(AgentInstance instance, long revision)
    {
        if (instance.Revision != revision) throw AgentCoreErrors.Conflict("Agent instance revision is stale; reload before continuing.");
    }

    private static void ValidateText(string? text, int bound, string field)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > bound) throw AgentCoreErrors.Validation($"{field} is required and must be bounded.");
        DefinitionResourcePolicies.RejectSecretsInTextualContent("text/plain", Encoding.UTF8.GetBytes(text));
    }

    private static string ActionHash(HarnessPreparation preparation, long policyRevision, HarnessAuthoringOperation operation) =>
        ToolActionHash.Compute("harness.author", JsonSerializer.SerializeToElement(new { preparation.PreparationId, preparation.DraftId, policyRevision, operation }, Json));

    private AdminEventAppend DraftEvent(AgentInstance instance, AgentDefinitionDraft draft, string operation, string outcome) =>
        new(ids.NewId(), time.GetUtcNow(), AdminEventActorKind.Agent, AdminEventOperationKind.HarnessPreparationChanged,
            "agent.instance", instance.InstanceId.ToString("D"), draft.Revision + 1, instance.ActiveVersion,
            JsonSerializer.Serialize(new { instanceId = instance.InstanceId, draftId = draft.DraftId, draftRevision = draft.Revision + 1,
                operation, outcome, policyRevision = instance.HarnessManagement!.PolicyRevision }, Json));

    private ValueTask<AgentInstance> SaveAsync(AgentInstance instance, HarnessManagementState state,
        string operation, string outcome, bool agent, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var history = new AdminEventAppend(ids.NewId(), now, agent ? AdminEventActorKind.Agent : AdminEventActorKind.LocalOwner,
            operation == "policy" ? AdminEventOperationKind.HarnessPolicyChanged : AdminEventOperationKind.HarnessPreparationChanged,
            "agent.instance", instance.InstanceId.ToString("D"), instance.Revision + 1, instance.ActiveVersion,
            JsonSerializer.Serialize(new { instanceId = instance.InstanceId, draftId = state.Preparation?.DraftId,
                operation, outcome, policyRevision = state.PolicyRevision }, Json));
        return instances.UpdateWithExpectedRevisionAsync(new(instance.InstanceId, instance.Revision, HarnessManagement: state, History: history), now, ct);
    }
}
