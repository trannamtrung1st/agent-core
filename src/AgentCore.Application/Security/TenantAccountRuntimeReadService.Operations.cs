using System.Text.Json;
using AgentCore.Application.Drive;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;
using AgentCore.Domain.Credentials;

namespace AgentCore.Application.Security;

public sealed partial class TenantAccountRuntimeReadService
{
    private async ValueTask<PreparedAccountOperationRead> PrepareOperationAsync(Source current, Guid operationId, CancellationToken ct)
    {
        var tenant = current.State.Tenant.TenantId;
        var receipt = await receipts.FindOperationReceiptAsync(tenant, operationId, ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("account_operation_source_unavailable");
        var original = receipt.RequestId is { } requestId ? await approvals.FindApprovalAsync(tenant, requestId, ct).ConfigureAwait(false) : null;
        var action = original?.Operation.AccountAction ?? throw new AccessDeniedException("account_operation_source_unavailable");
        var refs = new List<TenantAccountNativeReference>();
        using var json = JsonDocument.Parse(action.ArgumentsJson); var input = json.RootElement;
        var targets = action.ToolName == ToolCatalog.DriveTrashBatch
            ? DriveMutationArguments.Manifest(input).Select((f, i) => (Id: DriveService.TrashOperationId(operationId, i, f.FileId), Tool: ToolCatalog.DriveTrash, File: (string?)f.FileId)).ToArray()
            : action.ToolName == ToolCatalog.DriveDownload ? [] : new[] { (Id: operationId, Tool: action.ToolName, File: input.TryGetProperty("fileId", out var file) ? file.GetString() : null) };
        // Only canonical receipts may locate the native ledger. No legacy resolver
        // or hash search is used, even when a different root has matching intent.
        foreach (var target in targets)
        {
            var native = await (driveOperations ?? throw new AccessDeniedException("tenant_effect_unavailable")).GetAsync(current.Run.AgentInstanceId, target.Id, ct).ConfigureAwait(false);
            if (native is not null)
            {
                if (native.AgentInstanceId != current.Run.AgentInstanceId) throw new AccessDeniedException("account_operation_source_unavailable");
                TenantAccountOperationReadRules.RequireNative(action, target.Id, target.Tool, target.File, native);
            }
            refs.Add(new(target.Id, native?.FileId ?? target.File, native?.EffectHash, native is null ? null : TenantAccountOperationReadRules.Hash(native.SnapshotJson)));
        }
        var source = new PreparedAccountOperationRead(operationId, original!.RequestId, receipt.RunId, receipt.SessionId, original.Operation.ActionHash,
            action.ToolName, receipt.Outcome.ToString(), JsonSerializer.Serialize(refs));
        return source;
    }
    private async ValueTask RequireOriginalAsync(Source current, PreparedAccountRead read, CancellationToken ct)
    {
        if (read.Source is not { } source) return;
        var tenant = current.State.Tenant.TenantId;
        TenantAccountOperationReadRules.Require(read, tenant, current.Run.AgentInstanceId, current.Agent.PrincipalId,
            await approvals.FindApprovalAsync(tenant, source.RequestId, ct).ConfigureAwait(false),
            await receipts.FindOperationReceiptAsync(tenant, source.OperationId, ct).ConfigureAwait(false),
            await runs.FindTenantRunAsync(tenant, source.RunId, ct).ConfigureAwait(false),
            await sessions.FindTenantSessionAsync(tenant, source.SessionId, ct).ConfigureAwait(false));
    }
    private async ValueTask<ConnectedAccountReadResult> InspectOperationAsync(Source initial, PreparedAccountRead read,
        Func<CancellationToken, ValueTask<Credential>> recheck, Func<CredentialOAuthCheck, CancellationToken, ValueTask> maintain, CancellationToken ct)
    {
        var source = read.Source!; var items = new List<ConnectedAccountOperationItem>();
        var original = (await approvals.FindApprovalAsync(initial.State.Tenant.TenantId, source.RequestId, ct).ConfigureAwait(false))!;
        var transport = await (transports ?? throw new AccessDeniedException("tenant_effect_unavailable")).OpenAsync(
            new(initial.State.Tenant.TenantId, initial.Run.AgentInstanceId, initial.Bound.Binding, initial.Bound.Credential.Value), recheck, maintain, ct: ct).ConfigureAwait(false);
        var access = new DriveAccess(initial.Run.AgentInstanceId, initial.Bound.Binding, initial.Bound.Credential.Value,
            transport.Drive ?? throw new AccessDeniedException("account_unavailable")) { TenantAuthority = async (_, token) => { await recheck(token).ConfigureAwait(false); } };
        foreach (var reference in TenantAccountOperationReadRules.References(source))
        {
            await recheck(ct).ConfigureAwait(false);
            // An absent row at consent time is never converted into permission to
            // create a claim or resume an unattempted batch item.
            if (reference.EffectHash is null) { items.Add(new(reference.OperationId, reference.FileId, "unknown", "no_retained_evidence")); continue; }
            var native = await driveOperations!.GetAsync(initial.Run.AgentInstanceId, reference.OperationId, ct).ConfigureAwait(false)
                ?? throw new AccessDeniedException("account_operation_source_unavailable");
            var tool = source.ToolName == ToolCatalog.DriveTrashBatch ? ToolCatalog.DriveTrash : source.ToolName;
            TenantAccountOperationReadRules.RequireNative(original.Operation.AccountAction!, reference.OperationId, tool,
                tool is ToolCatalog.DriveCreateFolder or ToolCatalog.DriveUpload ? null : reference.FileId, native);
            if (native.FileId != reference.FileId || native.EffectHash != reference.EffectHash || TenantAccountOperationReadRules.Hash(native.SnapshotJson) != reference.SnapshotHash)
                throw new AccessDeniedException("account_operation_source_unavailable");
            if (native.State is "Planned" or "Rejected") { items.Add(new(native.OperationId, native.FileId, "not_dispatched", "retained_native_state")); continue; }
            if (native.State == "Failed") { items.Add(Failure(native, "retained_verification")); continue; }
            try
            {
                var verified = await (drive ?? throw new AccessDeniedException("tenant_effect_unavailable")).InspectOperationAsync(access, native.OperationId, ct).ConfigureAwait(false);
                if (verified.File.Id != native.FileId) throw new AccessDeniedException("account_result_invalid");
                items.Add(new(native.OperationId, native.FileId, "verified", native.State == "Succeeded" ? "retained_verification" : "current_remote_metadata", verified.File));
            }
            catch (DriveIndeterminateException)
            { await recheck(ct).ConfigureAwait(false); items.Add(new(native.OperationId, native.FileId, "indeterminate", "remote_verification_unconfirmed")); }
            catch (AgentCoreException e) when (e.Code == "drive_operation_failed")
            {
                await recheck(ct).ConfigureAwait(false);
                var failed = await driveOperations.GetAsync(initial.Run.AgentInstanceId, native.OperationId, ct).ConfigureAwait(false);
                if (failed?.State != "Failed") throw;
                items.Add(Failure(failed, "current_remote_metadata"));
            }
        }
        await recheck(ct).ConfigureAwait(false);
        return new(Operation: new(source.OperationId, source.ToolName, source.RecordedOutcome, items));
    }
    private static ConnectedAccountOperationItem Failure(DriveOperation native, string evidence)
    {
        var failure = JsonSerializer.Deserialize<DriveOperationFailure>(native.ResultJson ?? "null")
            ?? throw new AccessDeniedException("account_operation_source_unavailable");
        if (failure.Proof is not ("initial_request_not_executed" or "verified_target_trashed")
            || failure.VerifiedFile is { } file && file.Id != native.FileId) throw new AccessDeniedException("account_result_invalid");
        return new(native.OperationId, native.FileId, failure.Proof == "initial_request_not_executed" ? "not_applied" : "failed",
            evidence + ":" + failure.Proof, failure.VerifiedFile);
    }
}
