using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantApprovalFieldChange(string Field, object? Before, object? After, bool BeforeAvailable = true,
    bool AfterAvailable = true, bool RestoresDefault = false);
public sealed record TenantApprovalReview(string AgentName, string? RequesterName, string? ReviewerName,
    IReadOnlyList<TenantApprovalFieldChange> Changes, bool CanDecide, bool CanCancel, string? DecisionUnavailableReason,
    bool IndependentReview, DateTimeOffset ServerNow, string ExecutionState);

public sealed partial class TenantApprovalService
{
    // Presentation and action hints only. POST decisions and effects revalidate current authority and witnesses.
    private async ValueTask<TenantApprovalReview> ReviewAsync(TenantApprovalView view, TenantSecurityState state,
        TenantSessionInitiator human, CancellationToken ct)
    {
        if (view.Request.Operation.AutomationAdmission is not null) return await ReviewAutomationAdmissionAsync(view, state, human, ct).ConfigureAwait(false);
        var r = view.Request; var op = r.Operation; var at = time.GetUtcNow();
        var target = await agents.FindTenantAgentAsync(r.TenantId, op.InstanceId, ct).ConfigureAwait(false);
        var snapshot = r.Snapshot;
        var matches = target is not null && target.Instance.Revision == op.ExpectedInstanceRevision && target.Instance.PersonaRevision == op.ExpectedPersonaRevision;
        if (snapshot is null && matches)
        {
            if (op.Kind == TenantOperationKind.Persona) snapshot = TenantApprovalSnapshotRules.Persona(target!);
            else if (op.Kind == TenantOperationKind.ConversationPreferences)
            {
                try
                {
                    var (live, baseline) = await configuration.BaselineAsync(r.TenantId, op.InstanceId, ct).ConfigureAwait(false);
                    if (live.Instance.Revision == op.ExpectedInstanceRevision) snapshot = TenantApprovalSnapshotRules.Preferences(live, baseline);
                }
                catch (AccessDeniedException) { } // Historical requests remain readable when their definition is unavailable.
            }
        }
        async ValueTask<string?> Name(Guid? principal)
        {
            var user = state.Users.SingleOrDefault(u => u.PrincipalId == principal);
            return user is null ? null : (await security.ReadAccountAsync(user.AccountId, ct).ConfigureAwait(false))?.DisplayName;
        }
        var scope = TenantApprovalRules.Scope(r.TenantId, r.Operation);
        var policy = TenantOperationPolicyService.Effective(state, PermissionCatalog.Get(r.Permission));
        var independent = policy.IndependentReview;
        var source = await operations.FindRunAsync(new(r.TenantId, r.RunId), ct).ConfigureAwait(false);
        var waiting = op.BackgroundReport is not null || op.ReportSourceRead is not null ? source?.IsTerminal == true : source?.Status == AgentRunStatus.WaitingForApproval && source.ApprovalRequestId == r.RequestId;
        var cancel = r.Status == TenantApprovalStatus.Pending && at < r.ExpiresAt && waiting && target is not null
            && (await authorizer.CheckTenantAsync(state, human.PrincipalId, PermissionCatalog.Get("approvals.cancel"),
                AccessScope.ForResource(r.TenantId, "approval", r.RequestId.ToString("D")), ct).ConfigureAwait(false)).Allowed;
        string? unavailable = r.Status != TenantApprovalStatus.Pending ? "request_terminal" : at >= r.ExpiresAt ? "approval_expired"
            : target is null || target.Instance.Lifecycle != AgentCore.Domain.Definitions.AgentInstanceLifecycle.Active ? "executor_unavailable"
            : !matches ? "operation_witness_changed" : !waiting ? "run_unavailable" : null;
        if (unavailable is null)
        {
            try
            {
                AgentRun run; TenantSecurityState current;
                if (op.ReportSourceRead is not null)
                {
                    await (reportSourceReads ?? throw new AccessDeniedException("tenant_effect_unavailable")).RequireAsync(r, ct, pending: true).ConfigureAwait(false);
                    run = source ?? throw new AccessDeniedException("completion_unavailable"); current = state;
                }
                else if (op.BackgroundReport is not null)
                {
                    var reportState = await (backgroundReports ?? throw new AccessDeniedException("tenant_effect_unavailable")).RequireAsync(r, ct).ConfigureAwait(false);
                    (run, current) = (reportState.Source.Child, reportState.State);
                }
                else (run, current) = await operations.ResolveAsync(new(r.TenantId, r.RunId), ct).ConfigureAwait(false);
                if (current.Tenant.Revision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision");
                TenantApprovalRules.Validate(r, run, target!);
                if (op.ReportSourceRead is not null)
                {
                    independent = await TenantCompletionReadRules.IndependentAsync(state, TenantReportSourceReadRules.Targets(op.ReportSourceRead), r.OriginalInitiatorPrincipalId, authorizer, ct).ConfigureAwait(false);
                    await TenantReportSourceReadRules.ReviewerAsync(state, r, human.PrincipalId, authorizer, ct).ConfigureAwait(false);
                }
                else if (op.BackgroundReport is not null)
                    await TenantBackgroundReportRules.ReviewerAsync(state, r, human.PrincipalId, authorizer, ct).ConfigureAwait(false);
                else if (op.BackgroundStart is not null)
                {
                    independent = await TenantBackgroundCreationRules.IndependentAsync(state, r, authorizer, ct).ConfigureAwait(false);
                    await (backgroundCreations ?? throw new AccessDeniedException("tenant_effect_unavailable")).RequireAsync(r, run, state, ct).ConfigureAwait(false);
                    await TenantBackgroundCreationRules.ReviewerAsync(state, r, human.PrincipalId, authorizer, ct).ConfigureAwait(false);
                }
                else if (op.ArtifactPublication is not null)
                {
                    independent = await TenantArtifactReviewRules.IndependentAsync(state, r, authorizer, ct).ConfigureAwait(false);
                    await RequireArtifactAsync(r, run, state, ct).ConfigureAwait(false);
                    await TenantArtifactReviewRules.ReviewerAsync(state, r, human.PrincipalId, authorizer, ct).ConfigureAwait(false);
                }
                else if (op.CredentialCheck is { } check)
                {
                    if (credentialTargets is null) throw new AccessDeniedException("tenant_effect_unavailable");
                    var (_, bound) = await credentialTargets.ResolveAsync(run, check.Reference, ct).ConfigureAwait(false);
                    TenantCredentialOperationRules.RequireWitness(op, bound);
                    var read = await authorizer.CheckTenantAsync(state, run.Admission.ExecutionContext!.AgentPrincipalId,
                        PermissionCatalog.Get("credentials.read"), scope, ct).ConfigureAwait(false);
                    if (!read.Allowed) unavailable = "executor_read_required";
                }
                else if (op.WorkspaceMutation is not null)
                {
                    independent = await TenantWorkspaceMutationOperationRules.IndependentAsync(state, r, authorizer, ct).ConfigureAwait(false);
                    await (workspaceMutations ?? throw new AccessDeniedException("tenant_effect_unavailable")).RequireSavedAsync(r, run, ct).ConfigureAwait(false);
                    await TenantWorkspaceMutationOperationRules.ReviewerAsync(state, r, human.PrincipalId, authorizer, ct).ConfigureAwait(false);
                }
                else if (op.WorkspaceRead is not null)
                {
                    var reads = workspaceReads ?? throw new AccessDeniedException("tenant_effect_unavailable");
                    independent = await reads.InspectionIndependentAsync(r, state, ct).ConfigureAwait(false);
                    await reads.RequireInspectionAsync(r, run, ct).ConfigureAwait(false);
                    await reads.RequireInspectionReviewerAsync(r, state, human.PrincipalId, ct).ConfigureAwait(false);
                }
                else if (op.AttachmentRead is not null)
                {
                    var reads = attachmentReads ?? throw new AccessDeniedException("tenant_effect_unavailable");
                    independent = await reads.InspectionIndependentAsync(r, state, ct).ConfigureAwait(false);
                    await reads.RequireInspectionAsync(r, run, ct).ConfigureAwait(false);
                    await reads.RequireInspectionReviewerAsync(r, state, human.PrincipalId, ct).ConfigureAwait(false);
                }
                else if (op.CompletionRead is not null)
                {
                    var reads = completionReads ?? throw new AccessDeniedException("tenant_effect_unavailable");
                    independent = await reads.InspectionIndependentAsync(r, state, ct).ConfigureAwait(false);
                    await reads.RequireInspectionAsync(r, run, ct).ConfigureAwait(false);
                    await reads.RequireInspectionReviewerAsync(r, state, human.PrincipalId, ct).ConfigureAwait(false);
                }
                else if (op.ArtifactVerification is not null)
                {
                    var reads = artifactReads ?? throw new AccessDeniedException("tenant_effect_unavailable");
                    independent = await reads.InspectionIndependentAsync(r, state, ct).ConfigureAwait(false);
                    await reads.RequireInspectionAsync(r, run, ct).ConfigureAwait(false);
                    await reads.RequireInspectionReviewerAsync(r, state, human.PrincipalId, ct).ConfigureAwait(false);
                }
                else if (op.AccountAction is not null)
                {
                    independent = await TenantAccountActionRules.IndependentAsync(state, r, authorizer, ct).ConfigureAwait(false);
                    await (accountActions ?? throw new AccessDeniedException("tenant_effect_unavailable")).RequireSavedAsync(r, run, ct).ConfigureAwait(false);
                    await TenantAccountActionRules.ReviewerAsync(state, r, human.PrincipalId, authorizer, ct).ConfigureAwait(false);
                }
                else if (op.AccountRead is { } accountRead)
                {
                    independent = await TenantAccountReadRules.IndependentAsync(state, accountRead, r.OriginalInitiatorPrincipalId, authorizer, ct).ConfigureAwait(false);
                    await (accountReads ?? throw new AccessDeniedException("tenant_effect_unavailable")).RequireInspectionAsync(r, run, ct).ConfigureAwait(false);
                    await TenantAccountReadRules.ReviewerAsync(state, r, human.PrincipalId, authorizer, ct).ConfigureAwait(false);
                }
                else if (op.CredentialRead is not null)
                {
                    var reads = credentialReads ?? throw new AccessDeniedException("tenant_effect_unavailable");
                    independent = await reads.InspectionIndependentAsync(r, state, ct).ConfigureAwait(false);
                    await reads.RequireInspectionAsync(r, run, ct).ConfigureAwait(false);
                    await reads.RequireInspectionReviewerAsync(r, state, human.PrincipalId, ct).ConfigureAwait(false);
                }
                else if (op.BrowserCommand is { } browser)
                {
                    independent = await TenantBrowserOperationRules.IndependentAsync(r, state, authorizer, ct).ConfigureAwait(false);
                    await (browserTargets ?? throw new AccessDeniedException("tenant_effect_unavailable")).RequireAsync(r, run, ct).ConfigureAwait(false);
                    await TenantBrowserOperationRules.RequireReviewerAsync(state, op.InstanceId, browser, r.OriginalInitiatorPrincipalId, human.PrincipalId, authorizer, ct, r.Kind).ConfigureAwait(false);
                }
                else if (op.SandboxCommand is not null)
                    await (sandboxTargets ?? throw new AccessDeniedException("tenant_effect_unavailable")).RequireAsync(r, run, ct).ConfigureAwait(false);
                else if (op.SandboxControl is not null)
                {
                    var controls = controlTargets ?? throw new AccessDeniedException("tenant_effect_unavailable");
                    await controls.RequireAsync(r, run, ct).ConfigureAwait(false);
                    await controls.RequireReviewerAsync(r, state, human.PrincipalId, ct).ConfigureAwait(false);
                }
                else if (op.SandboxRead is not null)
                {
                    var reads = sandboxReads ?? throw new AccessDeniedException("tenant_effect_unavailable");
                    independent = await reads.InspectionIndependentAsync(r, state, ct).ConfigureAwait(false);
                    await reads.RequireInspectionAsync(r, run, ct).ConfigureAwait(false);
                    await reads.RequireInspectionReviewerAsync(r, state, human.PrincipalId, ct).ConfigureAwait(false);
                }
                else
                {
                    var effective = await configuration.ResolveAsync(r.TenantId, op.InstanceId, ct).ConfigureAwait(false);
                    unavailable = TenantLocalEffectRules.Eligibility(run, effective.Agent, effective.Configuration.Configuration.Definition);
                }
                if (unavailable is null)
                {
                    var eligible = await operations.Evaluate(state, run.Admission.ExecutionContext!, r.Permission, scope, ct).ConfigureAwait(false);
                    if (eligible.Decision == OperationDecisionKind.Denied || r.Kind == ApprovalKind.Review && eligible.ApprovalKind == ApprovalKind.ExceptionalGrant)
                        unavailable = "approval_authority_changed";
                    else if (!await operations.QualifiesAsync(state, r.OriginalInitiatorPrincipalId, human.PrincipalId,
                        PermissionCatalog.Get(r.Permission), scope, r.Kind, ct).ConfigureAwait(false))
                        unavailable = independent && human.PrincipalId == r.OriginalInitiatorPrincipalId ? "independent_reviewer_required" : "approver_unqualified";
                }
            }
            catch (AccessDeniedException ex) { unavailable = ex.ReasonCode; }
            catch (SecurityConflictException ex) when (ex.ReasonCode != "stale_revision") { unavailable = ex.ReasonCode; }
            catch (AgentCoreException) { unavailable = "operation_configuration_changed"; }
        }
        var execution = view.OperationResult is not null ? "recorded" : r.Status == TenantApprovalStatus.Pending ? "waitingDecision"
            : r.Status != TenantApprovalStatus.Approved ? "notAuthorized" : "unrecorded";
        return new(snapshot?.AgentName ?? target?.Instance.Persona.Name ?? "Unavailable agent", await Name(r.OriginalInitiatorPrincipalId).ConfigureAwait(false),
            await Name(view.Decision?.HumanPrincipalId).ConfigureAwait(false), Changes(op, snapshot), unavailable is null, cancel, unavailable,
            independent, at, execution);
    }

    private static IReadOnlyList<TenantApprovalFieldChange> Changes(PreparedTenantOperation op, TenantApprovalSnapshot? snapshot)
    {
        if (op.BackgroundReport is { } report)
        {
            var evidence = System.Text.Json.JsonSerializer.Deserialize<AgentCore.Application.Execution.AgentRunAdmissionFactory.SignalInput>(report.EvidenceJson)!;
            return [new("reportDestination", null, $"Exact saved conversation {report.ParentSessionId:D}", false),
                new("reportContent", null, AgentCore.Application.Execution.BackgroundCompletionProjection.ReportText(evidence.Text!), false), new("reportModel", null, report.Model.CatalogKey, false)];
        }
        if (op.BackgroundStart is { } task) return [new("backgroundObjective", null, task.Objective, false), new("backgroundTitle", null, task.Title, false),
            new("backgroundReportCompletion", null, task.ReportCompletion, false), new("backgroundModel", null, task.Model.CatalogKey, false), new("backgroundPrimaryPermission", null, task.Permission, false)];
        if (op.WorkspaceMutation is { } mutation)
            return [new("workspaceMutationTool", null, mutation.ToolName, false), new("workspaceMutationArguments", null, mutation.ArgumentsJson, false),
                new("workspaceMutationDisclosure", null, "Authorize only these exact saved workspace changes once. Current source changes require fresh review. No role, file read, recipient download or later mutation authority is granted.", false),
                new("workspaceMutationPartialEffects", null, "Copies and ordered batches are coarse filesystem operations. Earlier or partial effects can remain on failure; there is no atomic rollback and uncertain work must not be repeated.", false)];
        if (op.WorkspaceRead is { } workspace)
        {
            var args = AgentCore.Application.Tools.TenantWorkspaceReadContract.Parse(workspace.ToolName, workspace.ArgumentsJson);
            if (workspace.Knowledge is { } knowledge)
                return [new("path", null, knowledge.Path, false), new("readPage", null, $"Knowledge: {knowledge.Title}; identity {knowledge.Identity}; citation {knowledge.Citation}.", false),
                    new("readDisclosure", null, "Return only this saved approved knowledge text to the Agent. Changed identity, citation, binding or source requires fresh review. No file changes, later discovery or recipient download access.", false),
                    new("readLimit", null, "One bounded knowledge observation in this exact continuation. Later reads require current policy again.", false)];
            var page = workspace.ToolName == AgentCore.Application.Tools.ToolCatalog.OfficeDocumentInspect ? $"Word document excerpt: offset {args.Offset}; up to {args.Limit} elements."
                : workspace.ToolName == AgentCore.Application.Tools.ToolCatalog.OfficeSpreadsheetInspect ? $"Excel content: sheet {args.Sheet ?? (args.Range is null ? "workbook order" : "first worksheet")}; " + (args.Range is not null ? $"exact range {args.Range}." : args.Cursor is not null ? $"sparse continuation cursor {args.Cursor}." : "first sparse page.")
                : workspace.ToolName == AgentCore.Application.Tools.ToolCatalog.WorkspaceRead ? "One exact current UTF-8 file"
                : workspace.ToolName == AgentCore.Application.Tools.ToolCatalog.WorkspaceSearch ? $"Search for {args.Query}; glob: {args.Glob ?? "all files"}; up to {args.MaxResults} matches. Scan bounds: 200 files, 4096 entries, depth 8 and 256 KiB per file."
                : $"Up to {args.Limit} directory entries" + (args.Cursor is null ? "." : $", after {args.Cursor}.");
            return [new("path", null, args.Path, false), new("readPage", null, page, false),
                new("readDisclosure", null, "Return only this saved file or directory observation to the Agent. Changed source requires fresh review. No file mutation, recipient download or later discovery is authorized.", false),
                new("readLimit", null, "One bounded observation in this exact continuation. Later reads require current policy again.", false)];
        }
        if (op.AttachmentRead is { } attachment)
        {
            var args = AgentCore.Application.Tools.TenantAttachmentReadContract.Parse(attachment.ArgumentsJson);
            var page = attachment.ContentType == AgentCore.Application.Ports.OfficeLimits.DocumentMime
                ? $"Word document: offset {args.Offset ?? 0}; up to {args.Limit ?? 100} elements."
                : attachment.ContentType == AgentCore.Application.Ports.OfficeLimits.SpreadsheetMime
                    ? $"Excel: sheet {args.Sheet ?? (args.Range is null ? "workbook order" : "first worksheet")}; " + (args.Range is not null ? $"exact range {args.Range}." : args.Cursor is not null ? $"sparse continuation cursor {args.Cursor}." : "first sparse page.")
                    : attachment.Vision && AgentCore.Domain.Conversation.AttachmentMedia.IsImage(attachment.ContentType) ? "One sanitized image for this vision-capable model."
                    : "One bounded file observation; unsupported content remains inert.";
            return [new("attachmentDisplayName", null, attachment.DisplayName, false), new("attachmentContentType", null, attachment.ContentType, false),
                new("attachmentByteSize", null, attachment.ByteSize, false), new("attachmentChecksum", null, attachment.Sha256Hex, false), new("readPage", null, page, false),
                new("readDisclosure", null, "Return only this saved file and read window to the Agent. Changed source or window requires fresh review. No file changes, publication or recipient download access.", false),
                new("readLimit", null, "One bounded observation in this exact continuation. Later reads require current policy again.", false)];
        }
        if (op.ReportSourceRead is { } reportSource)
            return [new("reportSourceParentSessionId", null, reportSource.ParentSessionId, false), new("reportSourceInitialRunId", null, reportSource.InitialRunId, false),
                new("reportSourceRunId", null, reportSource.SourceRunId, false), new("reportSourceSessionId", null, reportSource.ChildSessionId, false),
                new("reportSourceObjective", null, reportSource.Objective, false), new("reportSourceObservation", null, reportSource.ObservationJson, false),
                new("reportSourceHash", null, reportSource.ObservationHash, false),
                new("readDisclosure", null, "Read only this exact saved terminal result for the requested report in the original conversation. No file metadata or file bytes are included.", false),
                new("readLimit", null, "This read consent creates no report Run and confirms no delivery. Report creation requires separate authority; source and reviewer access are checked again before use.", false)];
        if (op.CompletionRead is { } completion)
            return [new("completionInitialRunId", null, completion.InitialRunId, false), new("completionSourceRunId", null, completion.SourceRunId, false),
                new("completionSessionId", null, completion.ChildSessionId, false), new("completionObjective", null, completion.Objective, false),
                new("completionObservation", null, completion.ObservationJson, false), new("completionObservationHash", null, completion.ObservationHash, false), new("completionMetadata", null, completion.ArtifactsJson, false),
                new("readDisclosure", null, "Return this exact terminal child result and the saved metadata links. No file bytes, new discovery or mutation authority.", false),
                new("readLimit", null, "The unchanged observation may be consumed only in this same task while current raw read and reviewer authority remain valid.", false)];
        if (op.ArtifactVerification is { } verification)
            return [new("artifactDisplayName", null, verification.DisplayName, false), new("artifactContentType", null, verification.ContentType, false),
                new("artifactStoredByteSize", null, verification.ByteSize, false), new("artifactChecksum", null, verification.Sha256Hex, false),
                new("readDisclosure", null, "Return only this exact file's name, type, size, checksum and creation time. No file body, workspace path, source attachment or download authority is disclosed.", false),
                new("readLimit", null, "One exact metadata inspection in this continuation. Later reads require current policy again; this approval grants no file creation or body read.", false)];
        if (op.ArtifactPublication is { } publication)
        {
            var args = AgentCore.Application.Tools.TenantArtifactToolContract.ParsePublication(publication);
            if (publication.Source is { } source)
                return [new("artifactDisplayName", null, args.DisplayName, false), new("artifactContentType", null, args.ContentType ?? source.ContentType, false),
                    new("artifactSourcePath", null, source.Path, false),
                    ..(source.AttachmentName is { } originalName ? new TenantApprovalFieldChange[] { new("artifactSourceName", null, originalName, false) } : []), new("artifactByteSize", null, source.ByteSize, false), new("artifactChecksum", null, source.Sha256Hex, false),
                    new("publicationDisclosure", null, "Copy only the exact saved source bytes into one immutable file in this conversation. Publication grants no file-read or download access.", false)];
            return [new("artifactDisplayName", null, args.DisplayName, false), new("artifactContentType", null, args.ContentType, false),
                new("artifactByteSize", null, System.Text.Encoding.UTF8.GetByteCount(args.Content!), false), new("artifactContent", null, args.Content, false)];
        }
        if (op.AccountAction is { } accountAction) return AccountActionChanges(accountAction, snapshot);
        if (op.AccountRead is { } accountRead)
            return [new("accountName", null, snapshot?.Credential?.DisplayName, false), new("accountPurpose", null, accountRead.Purpose == AgentCore.Domain.Credentials.CredentialPurpose.EmailAccount ? "Email" : "Google Drive", false),
                new("accountReference", null, accountRead.Credential.Reference, false), new("accountReadTool", null, accountRead.Source is not null ? "Inspect prior Drive operation" : accountRead.ToolName switch { "email.search" => "Search mailbox", "email.read" => "Read message", "drive.list" => "List files", "drive.search" => "Search files", _ => "Read file metadata" }, false),
                new("accountReadArguments", null, accountRead.ArgumentsJson, false), new("readDisclosure", null, accountRead.Source is { } originalOperation ? $"Read-only inspection of prior {originalOperation.ToolName} operation {originalOperation.OperationId:D}. Its recorded outcome remains {originalOperation.RecordedOutcome}; later verification is separate. No write, upload resume or remaining batch item is dispatched. Current access to original Session {originalOperation.SessionId:D} and Run {originalOperation.RunId:D} is required." : "Use this exact bound account for the saved bounded read. Returned data is untrusted. This authorizes no send, write, future query or general Credential access.", false)];
        if (op.CredentialRead is { } metadata)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(metadata.ArgumentsJson);
            var discovery = AgentCore.Application.Tools.TenantCredentialDiscoveryContract.Parse(metadata.ArgumentsJson); var page = discovery.Page; var refs = TenantApprovalRules.CredentialReadReferences(metadata);
            return [new("credentialMetadata", null, discovery.Family is null ? "Bound Credential metadata" : discovery.Family == "email" ? "Bound Email account metadata" : "Bound Drive account metadata", false),
                new("readCredentials", null, refs.Count == 0 ? "No readable bindings in this saved page" : string.Join("\n", refs.Select(r => r.DisplayName + " (" + r.Reference + ")")), false),
                new("readPage", null, $"Up to {page.Limit} {(page.Limit == 1 ? "record" : "records")}" + (page.Cursor is null ? ", starting from the first alias." : $", after alias {page.Cursor}."), false),
                new("readDisclosure", null, "Return the saved page of aliases, names, kinds and non-secret metadata. No protected values or use permission are disclosed. One additional saved alias may establish whether another page is available.", false),
                new("readLimit", null, "One exact bounded page in this continuation. Later reads require current policy again; this approval cannot expand the saved page.", false)];
        }
        if (op.BrowserCommand is { } browser)
            return [new("browserTool", null, browser.ToolName, false), new("browserOrigin", null, snapshot?.Browser?.Origin ?? "No live page", false, snapshot?.Browser is not null),
                new("browserProfileMode", null, snapshot?.Browser?.ProfileMode switch { "EphemeralSession" => "Separate for this Session", "PersistentAgent" => "Shared across this agent's Sessions", _ => null }, false, snapshot?.Browser is not null),
                new("browserArguments", null, TenantApprovalRules.BrowserDescription(browser), false),
                new("browserEffect", null, browser.ReadOnly ? "Inspect the exact Browser target once." : "Attempt this exact Browser command once. External changes may be irreversible.", false),
                new("browserLimit", null, "Approving records consent for one exact attempt under current authority. If the target, Credential or policy changes, prepare a new request.", false)];
        if (op.SandboxRead is { } read)
        {
            var labels = snapshot?.SandboxRead is null ? null : TenantApprovalRules.ReadSnapshotLabels(op, snapshot.SandboxRead);
            var changes = new List<TenantApprovalFieldChange> { new("sandboxInspection", null, TenantSandboxReadOperationRules.Summary(read.Kind), false),
                new("readProfiles", null, labels is null ? null : labels.Count == 0 ? "No readable profiles in this saved request" : string.Join("\n", labels.Select(p => p.Name + " (revision " + p.Revision + ")")), false, labels is not null) };
            if (read.Kind != SandboxReadKind.Profiles) changes.Add(new("generation", null, read.Generation, false));
            if (read.ExecutionId is not null) changes.Add(new("execution", null, read.ExecutionId, false));
            changes.Add(new("readDisclosure", null, read.Kind == SandboxReadKind.Process ? "Return bounded stored stdout/stderr and execution evidence for this exact receipt. Owner command text and initialization details are omitted."
                : "Return only this exact readable profile set or stored environment summary. Command output and initialization details are omitted.", false));
            changes.Add(new("readLimit", null, "One stored observation in the saved continuation. No SDK refresh, command or cleanup occurs. Stored state is not live authority; later reads require current policy again.", false));
            return changes;
        }
        if (op.SandboxControl is { } control)
            return [new("sandboxControl", null, control.Kind == SandboxControlKind.StopProcess ? "Stop the exact process" : "Reset the existing environment", false),
                new("sandboxProfile", null, snapshot?.SandboxControl?.ProfileName, false, snapshot?.SandboxControl is not null),
                new("image", null, snapshot?.SandboxControl?.ImageReference, false, snapshot?.SandboxControl is not null),
                new("generation", null, control.Generation, false), new("execution", null, control.ExecutionId, false),
                new("storedEnvironmentState", null, snapshot?.SandboxControl?.StoredState, false, snapshot?.SandboxControl is not null),
                new("storedExecutionState", null, snapshot?.SandboxControl?.ExecutionState, false, snapshot?.SandboxControl is not null),
                new("cleanupConsequence", null, control.Kind == SandboxControlKind.StopProcess
                    ? "Request one interrupt; uncertain termination may retire this environment. Writes may continue until cleanup is confirmed."
                    : "Retire this generation and clear saved profile selection. Writes may continue until retirement is confirmed. Another command requires fresh exact Review.", false),
                new("fileRetention", null, "Canonical /working, /home and Artifacts are retained. Unsynchronized environment-local files may be lost.", false)];
        if (op.SandboxCommand is { } sandbox)
        {
            var command = TenantApprovalRules.SandboxCommand(sandbox);
            return [new("sandboxProfile", null, snapshot?.Sandbox?.ProfileName, false, snapshot?.Sandbox is not null),
                new("image", null, snapshot?.Sandbox?.ImageReference, false, snapshot?.Sandbox is not null),
                new("command", null, command.Command ?? System.Text.Json.JsonSerializer.Serialize(command.Argv), false),
                new("workingDirectory", null, command.Cwd, false), new("timeoutSeconds", null, command.TimeoutSeconds, false), new("background", null, command.Background, false),
                new("homeAccess", null, sandbox.HomeAccess.ToString(), false),
                new("cpu", null, snapshot?.Sandbox?.Cpu, false, snapshot?.Sandbox is not null),
                new("memoryMiB", null, snapshot?.Sandbox?.MemoryMiB, false, snapshot?.Sandbox is not null),
                new("storageBytes", null, snapshot?.Sandbox?.StorageBytes, false, snapshot?.Sandbox is not null),
                new("initializationRequired", null, snapshot?.Sandbox?.WillInitialize, false, snapshot?.Sandbox is not null),
                new("profileInitialization", null, snapshot?.Sandbox?.ProfileInitializationScript, false, snapshot?.Sandbox is not null),
                new("instanceInitialization", null, snapshot?.Sandbox?.InstanceInitializationScript, false, snapshot?.Sandbox is not null)];
        }
        if (op.CredentialCheck is { } check)
            return [new("credential", snapshot?.Credential?.DisplayName, snapshot?.Credential?.DisplayName, snapshot?.Credential is not null, snapshot?.Credential is not null),
                new("bindingReference", check.Reference, check.Reference),
                new("connectionCheck", snapshot?.Credential?.ConnectionStatus, "Verify the bound Google connection; refresh grant rotation or reconnection may be recorded.", snapshot?.Credential is not null)];
        if (op.Persona is { } persona)
            return [new("name", snapshot?.BeforePersona?.Name, persona.Name, snapshot?.BeforePersona is not null),
                new("role", snapshot?.BeforePersona?.Role, persona.Role, snapshot?.BeforePersona is not null),
                new("description", snapshot?.BeforePersona?.Description, persona.Description, snapshot?.BeforePersona is not null),
                new("tone", snapshot?.BeforePersona?.Tone, persona.Tone, snapshot?.BeforePersona is not null)];
        var p = op.Preferences!; var defaults = snapshot?.PreferenceDefaults; var before = snapshot?.PreferenceOverrides;
        var values = new List<TenantApprovalFieldChange>();
        void Add(string field, object? original, object? value, bool restore, object? inherited) =>
            values.Add(new(field, original, restore ? inherited : value, snapshot is not null, !restore || defaults is not null, restore));
        if (p.ResponseLength is not null || p.ClearResponseLength) Add("responseLength", before?.ResponseLength ?? defaults?.ResponseLength, p.ResponseLength, p.ClearResponseLength, defaults?.ResponseLength);
        if (p.Language is not null || p.ClearLanguage) Add("language", before?.Language ?? defaults?.Language, p.Language, p.ClearLanguage, defaults?.Language);
        if (p.AskOneQuestionAtATime is not null || p.ClearAskOneQuestionAtATime) Add("askOneQuestionAtATime", before?.AskOneQuestionAtATime ?? defaults?.AskOneQuestionAtATime,
            p.AskOneQuestionAtATime, p.ClearAskOneQuestionAtATime, defaults?.AskOneQuestionAtATime);
        return values;
    }
}
