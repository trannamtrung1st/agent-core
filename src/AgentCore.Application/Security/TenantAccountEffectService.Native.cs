using System.Security.Cryptography;
using System.Text.Json;
using AgentCore.Application.Drive;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantAccountEffectService
{
    private sealed record NativeOutcome(TenantOperationOutcome Outcome, TenantAccountActionResult? Result = null, string? Reason = null);
    private async ValueTask<NativeOutcome> ApplyAsync(TenantApprovalRequest request, AgentRun run, TenantConnectedAccountTransport native,
        Func<CancellationToken, ValueTask<TenantAccountActionSource>> current, CancellationToken ct)
    {
        var action = request.Operation.AccountAction!; var args = TenantAccountActionRules.Arguments(action);
        if (args.ToolName == ToolCatalog.EmailSend) return await SendAsync(request, native.Email!, current, ct).ConfigureAwait(false);
        var access = new DriveAccess(run.AgentInstanceId, (await current(ct).ConfigureAwait(false)).Bound.Binding,
            (await current(ct).ConfigureAwait(false)).Bound.Credential.Value, native.Drive!) { TenantAuthority = async (_, token) => { await current(token).ConfigureAwait(false); } };
        using var json = JsonDocument.Parse(args.Json); var input = json.RootElement;
        // These exact metadata reads are part of the approved compound action. No
        // read performed here can widen its saved target or silently update intent.
        foreach (var saved in TenantApprovalRules.AccountRemoteSources(action))
        {
            var file = await drive.GetAsync(access, saved.Id, ct).ConfigureAwait(false);
            var actual = new PreparedAccountRemoteFile(file.Id, file.Name, file.MimeType, file.ByteSize, file.Version, file.Md5Checksum, file.Parents, file.Trashed);
            if (JsonSerializer.Serialize(actual) != JsonSerializer.Serialize(saved)) return new(TenantOperationOutcome.Denied);
        }
        if (args.Download)
        {
            var content = await drive.ReadAsync(access, input.GetProperty("fileId").GetString()!, input.GetProperty("expectedVersion").GetString(), ct).ConfigureAwait(false);
            await targets.RequireSavedAsync(request, (await current(ct).ConfigureAwait(false)).Run, ct, exactSecret: false).ConfigureAwait(false);
            async ValueTask Commit(CancellationToken token)
            {
                var live = await current(token).ConfigureAwait(false);
                await targets.RequireSavedAsync(request, live.Run, token, exactSecret: false).ConfigureAwait(false);
            }
            var created = await files.TryWriteNewTenantAsync(TenantWorkspaceEffectRules.Owner(run, args.Path!), args.Path!, content.Bytes, Commit, ct).ConfigureAwait(false);
            return created ? new(TenantOperationOutcome.Completed, new("downloaded", FileId: content.File.Id, Path: args.Path,
                ByteSize: content.Bytes.LongLength, Sha256: Convert.ToHexStringLower(SHA256.HashData(content.Bytes)))) : new(TenantOperationOutcome.Denied);
        }
        if (args.ToolName == ToolCatalog.DriveTrashBatch)
        {
            var batch = await drive.TrashBatchAsync(access, request.Operation.OperationId, action.Credential.AuthEpoch, DriveMutationArguments.Manifest(input), ct).ConfigureAwait(false);
            var outcome = batch.TrashedCount == batch.RequestedCount ? TenantOperationOutcome.Completed
                : batch.TrashedCount == 0 && batch.UncertainCount == 0 ? TenantOperationOutcome.Denied : TenantOperationOutcome.Indeterminate;
            return new(outcome, new(outcome == TenantOperationOutcome.Completed ? "completed" : "incomplete", Items: batch.Items.Select(i =>
                new TenantAccountItemResult(i.FileId, i.Status switch { "Succeeded" => "completed", "Failed" => "denied", "Indeterminate" => "indeterminate", _ => "unattempted" })).ToArray()));
        }
        DriveEffect effect; byte[] bytes = []; string? fileId = null;
        if (args.Upload)
        {
            var source = await targets.RequireSavedAsync(request, (await current(ct).ConfigureAwait(false)).Run, ct, exactSecret: false).ConfigureAwait(false)
                ?? throw new AccessDeniedException("account_source_changed");
            await source.RequireCurrentAsync(ct).ConfigureAwait(false); bytes = source.CopyContent();
            if (bytes.LongLength != action.UploadSource!.ByteSize || Convert.ToHexStringLower(SHA256.HashData(bytes)) != action.UploadSource.Sha256Hex)
                return new(TenantOperationOutcome.Denied);
            effect = new(input.GetProperty("name").GetString()!, input.GetProperty("folderId").GetString()!, action.UploadSource.Sha256Hex,
                action.UploadSource.ByteSize, action.UploadSource.ContentType);
        }
        else if (args.ToolName == ToolCatalog.DriveCreateFolder) effect = new(input.GetProperty("name").GetString()!, input.GetProperty("parentId").GetString()!);
        else
        {
            fileId = input.GetProperty("fileId").GetString()!; var source = TenantApprovalRules.AccountRemoteSources(action).Single(f => f.Id == fileId);
            effect = new(args.ToolName == ToolCatalog.DriveRename ? input.GetProperty("name").GetString()! : source.Name,
                args.ToolName == ToolCatalog.DriveMove ? input.GetProperty("destinationParentId").GetString()! : source.Parents.FirstOrDefault() ?? "root",
                ExpectedVersion: input.GetProperty("expectedVersion").GetString(), SourceParent: input.TryGetProperty("sourceParentId", out var parent) ? parent.GetString() : null);
        }
        await current(ct).ConfigureAwait(false);
        try
        {
            var receipt = await drive.EffectAsync(access, request.Operation.OperationId, args.ToolName, action.Credential.AuthEpoch, fileId, effect, bytes, ct).ConfigureAwait(false);
            return new(TenantOperationOutcome.Completed, new("completed", FileId: receipt.File.Id));
        }
        catch (DriveIndeterminateException) { return new(TenantOperationOutcome.Indeterminate); }
        catch (Sessions.AgentCoreException e) when (e.Code == "drive_operation_failed") { return new(TenantOperationOutcome.Denied); }
    }
    private async ValueTask<NativeOutcome> SendAsync(TenantApprovalRequest request, IEmailProvider mailbox,
        Func<CancellationToken, ValueTask<TenantAccountActionSource>> current, CancellationToken ct)
    {
        var action = request.Operation.AccountAction!; var snapshot = TenantAccountActionRules.DraftSnapshot(action);
        try
        {
            if (mailbox is IEmailSendPreflight preflight) await preflight.PrepareSendAsync(ct).ConfigureAwait(false);
            await current(ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested();
        }
        catch (Exception e) when (e is AccessDeniedException or SecurityConflictException or Sessions.AgentCoreException or OperationCanceledException)
        { return new(TenantOperationOutcome.Denied); } // No send claim or native send has been entered.
        var claim = new EmailSendOperation(snapshot.DraftId, action.Credential.CredentialId, snapshot.Revision, request.RequestId,
            request.Operation.ActionHash, null, time.GetUtcNow());
        if (!await drafts.TryClaimSendAsync(claim, ct).ConfigureAwait(false))
        {
            var prior = await drafts.GetSendAsync(snapshot.DraftId, ct).ConfigureAwait(false);
            if (prior is null || prior.AccountId != snapshot.AccountId || prior.DraftRevision != snapshot.Revision) throw new SecurityConflictException("account_source_changed");
            return prior.Result?.Outcome switch { EmailSendOutcome.Sent => new(TenantOperationOutcome.Denied, new("already_sent", DraftId: snapshot.DraftId), "account_draft_already_sent"),
                EmailSendOutcome.Failed => new(TenantOperationOutcome.Denied), _ => new(TenantOperationOutcome.Indeterminate) };
        }
        EmailSendResult result; var dispatchEntered = false;
        try
        {
            await current(ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested(); dispatchEntered = true;
            result = await mailbox.SendDraftAsync(new(snapshot.DraftId, snapshot), ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            result = dispatchEntered ? new(EmailSendOutcome.Indeterminate, null, "dispatch_unconfirmed", "Sending could not be confirmed; this draft cannot be retried.")
                : new(EmailSendOutcome.Failed, null, "not_dispatched", "Current authority was lost before dispatch.");
        }
        using var saveDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10), time);
        // Acknowledged send facts survive caller cancellation. Failure to save a
        // result leaves the existing unique claim uncertain and never retryable.
        await drafts.CompleteSendAsync(snapshot.DraftId, result, saveDeadline.Token).ConfigureAwait(false);
        return result.Outcome switch { EmailSendOutcome.Sent => new(TenantOperationOutcome.Completed, new("sent", DraftId: snapshot.DraftId)),
            EmailSendOutcome.Failed => new(TenantOperationOutcome.Denied), _ => new(TenantOperationOutcome.Indeterminate) };
    }
}
