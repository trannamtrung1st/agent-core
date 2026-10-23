using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantApprovedOperationService
{
    // The local approval owner already has these source readers. Continuation keeps
    // Run/checkpoint delivery without a duplicate set of reader dependencies.
    internal async ValueTask RequireStoredAccountReadsAsync(AgentRun run, CancellationToken ct)
    {
        if (accountReads is not null) await accountReads.RequireStoredAsync(run, ct).ConfigureAwait(false);
    }

    internal async ValueTask RequireStoredResourceReadsAsync(AgentRun run, CancellationToken ct)
    {
        if (credentialReads is not null) await credentialReads.RequireStoredAsync(run, ct).ConfigureAwait(false);
        if (attachmentReads is not null) await attachmentReads.RequireStoredAsync(run, ct).ConfigureAwait(false);
        if (artifactReads is not null) await artifactReads.RequireStoredAsync(run, ct).ConfigureAwait(false);
        if (workspaceReads is not null) await workspaceReads.RequireStoredAsync(run, ct).ConfigureAwait(false);
    }

    internal async ValueTask<(string? Text, string? UnavailableReason)> InspectApprovedReadAsync(
        AgentRun run, TenantApprovalRequest request, TenantOperationReceipt receipt, CancellationToken ct)
    {
        if (receipt.Outcome != TenantOperationOutcome.Completed) return (null, null);
        var frame = new TenantRuntimeFrame(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation);
        var budget = ToolLimits.MaxOutputBytes - 512;
        try
        {
            var text = request.Operation.Kind switch
            {
                TenantOperationKind.ManagementRead => await (managementTargets ?? throw Unavailable()).Reads.InspectApprovedAsync(frame, request, receipt, budget, ct).ConfigureAwait(false),
                TenantOperationKind.AutomationRead => await (automationTargets ?? throw Unavailable()).Reads.InspectApprovedAsync(frame, request, receipt, budget, ct).ConfigureAwait(false),
                TenantOperationKind.ExperienceOperation when request.Operation.ExperienceOperation!.ToolName == TenantExperienceToolContract.Source
                    => await (experienceOperations ?? throw Unavailable()).InspectApprovedAsync(frame, request, receipt, budget, ct).ConfigureAwait(false),
                TenantOperationKind.ContinuityRead => await (continuityReads ?? throw Unavailable()).InspectApprovedAsync(frame, request, receipt, budget, ct).ConfigureAwait(false),
                TenantOperationKind.MemoryOperation when TenantMemoryToolContract.ReadOnly(request.Operation.MemoryOperation!.ToolName)
                    => await (memoryOperations ?? throw Unavailable()).InspectApprovedAsync(frame, request, receipt, budget, ct).ConfigureAwait(false),
                TenantOperationKind.WorkspaceRead => await (workspaceReads ?? throw Unavailable()).InspectApprovedForRunAsync(frame, request, receipt, budget, ct).ConfigureAwait(false),
                TenantOperationKind.AttachmentRead => await (attachmentReads ?? throw Unavailable()).InspectApprovedForRunAsync(frame, request, receipt, budget, ct).ConfigureAwait(false),
                TenantOperationKind.CompletionRead => await (completionReads ?? throw Unavailable()).InspectApprovedForRunAsync(frame, request, receipt, budget, ct).ConfigureAwait(false),
                TenantOperationKind.ArtifactVerification => await (artifactReads ?? throw Unavailable()).InspectApprovedForRunAsync(frame, request, receipt, budget, ct).ConfigureAwait(false),
                TenantOperationKind.AccountRead => await (accountReads ?? throw Unavailable()).InspectApprovedForRunAsync(frame, request, receipt, budget, ct).ConfigureAwait(false),
                TenantOperationKind.CredentialRead => await (credentialReads ?? throw Unavailable()).InspectApprovedForRunAsync(frame, request, receipt, budget, ct).ConfigureAwait(false),
                TenantOperationKind.SandboxRead => await (sandboxReads ?? throw Unavailable()).InspectApprovedForRunAsync(frame, request, receipt, ToolLimits.MaxOutputBytes, ct).ConfigureAwait(false),
                _ => null
            };
            return (text, null);
        }
        catch (AccessDeniedException error) { return (null, error.ReasonCode); }
        catch (SecurityConflictException error) { return (null, error.ReasonCode); }
        catch (AgentCore.Application.Sessions.AgentCoreException error) when (request.Operation.AccountRead is not null)
        { return (null, error.Code); }
    }

    private static AccessDeniedException Unavailable() => new("tenant_effect_unavailable");
}
