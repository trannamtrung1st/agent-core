using System.Security.Cryptography;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Definitions;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Drive;

public sealed record DriveAccountView(string AccountRef, string DisplayName, string EmailAddress, string Status, long AuthEpoch, bool Available, string? Recovery, Guid BindingId, bool Synthetic);
public sealed record DriveAccess(Guid InstanceId, AgentCredentialBinding Binding, Credential Credential, IGoogleDriveProvider Provider);
public sealed record DriveEffect(string Name, string Parent, string? SourceSha256 = null, long SourceBytes = 0, string? MimeType = null, string? ExpectedVersion = null, string? SourceParent = null, string? SourceMd5 = null);
public sealed record DriveReceipt(Guid OperationId, string Status, DriveFile File, bool Reconciled, bool Synthetic, string? Limitation = null);
public sealed class DriveIndeterminateException(Guid operationId, string fileId, string? causeCode = null, bool resumeAllowed = false,
    IReadOnlyList<string>? verificationFailures = null) : Exception(verificationFailures is { Count: > 0 } && !resumeAllowed
    ? "The known file does not match the upload receipt. Inspect the reported verification fields and request exact cleanup approval if needed. No replacement upload or automatic deletion was started."
    : resumeAllowed
    ? "Upload completion is uncertain. Check the operation or continue the same upload; do not create another file."
    : "The remote effect remains uncertain. Inspect the known file in Drive and confirm account access. A missing file alone does not make another write safe.")
{ public Guid OperationId { get; } = operationId; public string FileId { get; } = fileId; public string? CauseCode { get; } = causeCode; public bool ResumeAllowed { get; } = resumeAllowed;
  public IReadOnlyList<string> VerificationFailures { get; } = verificationFailures ?? [];
  public string RecoveryAction => ResumeAllowed ? "continue_same_upload" : VerificationFailures.Count > 0 ? "inspect_target_and_request_cleanup_approval" : "inspect_operation";
}

public sealed partial class DriveService(ICredentialStore credentials, IAgentCredentialBindingStore bindings, IAgentInstanceStore instances,
    IGoogleDriveProviderFactory providers, IDriveOperationStore operations, TimeProvider time, ILogger<DriveService>? logger = null)
{
    // Fixed stripes bound memory while serializing effect/inspection for this single host.
    private readonly SemaphoreSlim[] operationGates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private SemaphoreSlim Gate(DriveAccess access) => operationGates[(uint)HashCode.Combine(access.InstanceId, access.Credential.CredentialId) % 64];
    private static AgentCoreException FailedOperation(DriveOperation op)
    {
        var failure = JsonSerializer.Deserialize<DriveOperationFailure>(op.ResultJson!);
        return Failure("drive_operation_failed", failure?.Proof == "verified_target_trashed" ? failure.Message
            : "This operation did not execute. " + failure?.Message + " Resolve the cause, then explicitly start a new save; this operation cannot run again.", 409);
    }
    private static bool CanResume(DriveAccess access, DriveOperation op) => op.Tool == "drive.upload"
        && op.ProtectedUploadSession is not null && op.AuthEpoch == access.Credential.DriveAccount!.AuthEpoch;
    public const int MaxTransferBytes = 50 * 1024 * 1024;
    private static AgentCoreException Failure(string code, string message, int status = 400) => new(code, message, status);
    public static string Id(string value) => value is { Length: > 0 and <= 160 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? value : throw Failure("drive_invalid", "Use a concrete Drive file/folder ID.");
    public static string Name(string value) => value is { Length: > 0 and <= 255 } && !value.Any(char.IsControl) && !value.Contains('/') && !value.Contains('\\') ? value : throw Failure("drive_invalid", "Use a safe file/folder name, without path separators.");
    private async ValueTask<AgentInstance> Instance(Guid id, bool requireActive, CancellationToken ct) { var instance = await instances.FindAsync(id, ct) ?? throw AgentCoreErrors.NotFound("Agent instance was not found."); if (requireActive && instance.Lifecycle != AgentInstanceLifecycle.Active) throw AgentCoreErrors.Forbidden("Archived Instances cannot use Drive bindings. Unarchive this Instance before using Drive."); return instance; }
    public async ValueTask<IReadOnlyList<DriveAccountView>> AccountsAsync(Guid instance, IReadOnlyList<AgentCredentialBinding>? admitted, CancellationToken ct)
    {
        var owner = await Instance(instance, false, ct); var result = new List<DriveAccountView>();
        foreach (var binding in await bindings.ListBindingsAsync(instance, ct))
        {
            if (admitted is not null && !admitted.Any(b => b.BindingId == binding.BindingId && b.Revision == binding.Revision && b.CredentialId == binding.CredentialId)) continue;
            var c = await credentials.GetAsync(binding.CredentialId, ct); if (c is not { Purpose: CredentialPurpose.GoogleDriveAccount, DriveAccount: not null }) continue;
            var status = owner.Lifecycle != AgentInstanceLifecycle.Active ? "Archived" : !providers.Enabled ? "HostDisabled" : !providers.Configured ? "NotConfigured" : c.Status == CredentialStatus.Disabled ? "Disabled" : c.DriveAccount.ConnectionStatus.ToString(); var available = owner.Lifecycle == AgentInstanceLifecycle.Active && providers.Enabled && providers.Configured && c.Status == CredentialStatus.Active && c.DriveAccount.IsUsable && c.DriveAccount.Synthetic == providers.Synthetic;
            result.Add(new(binding.Reference, c.DisplayName, c.DriveAccount.EmailAddress, status, c.DriveAccount.AuthEpoch, available, available ? null : owner.Lifecycle != AgentInstanceLifecycle.Active ? "Unarchive this Instance before using its Drive bindings." : !providers.Enabled ? "Ask the operator to enable GoogleDrive:Enabled." : !providers.Configured ? "Ask the operator to configure GoogleDriveOAuth." : "Connect/enable this Google Drive Account in Admin → Credentials.", binding.BindingId, providers.Synthetic));
            if (result.Count == 20) break;
        }
        return result;
    }
    public async ValueTask<DriveAccess> ResolveAsync(Guid instance, string? reference, IReadOnlyList<AgentCredentialBinding>? admitted, bool write, CancellationToken ct)
    {
        if (!providers.Enabled) throw Failure("drive_host_disabled", "Google Drive is disabled on this host. Ask the operator to enable GoogleDrive:Enabled.", 403);
        if (!providers.Configured) throw Failure("drive_oauth_not_configured", "Ask the operator to configure GoogleDriveOAuth.", 403);
        await Instance(instance, true, ct); var accounts = await AccountsAsync(instance, admitted, ct);
        if (reference is null) { if (accounts.Count != 1) throw Failure("drive_not_connected", accounts.Count == 0 ? "Bind a connected Google Drive Account to this Instance in Admin → Credentials." : "Choose an accountRef from drive.accounts."); reference = accounts[0].AccountRef; }
        var alias = CredentialRules.Reference(reference); var binding = (await bindings.ListBindingsAsync(instance, ct)).SingleOrDefault(b => b.Reference == alias) ?? throw Failure("drive_not_connected", "This Drive alias is not bound to the current Instance.", 403);
        if (admitted is not null && !admitted.Any(b => b.BindingId == binding.BindingId && b.Revision == binding.Revision && b.CredentialId == binding.CredentialId)) throw Failure("drive_access_changed", "This binding was not admitted for this Run. Start a fresh owner turn.", 403);
        var c = await credentials.GetAsync(binding.CredentialId, ct);
        if (c is not { Purpose: CredentialPurpose.GoogleDriveAccount, Status: CredentialStatus.Active, DriveAccount: not null } || !c.DriveAccount.IsUsable || c.DriveAccount.Synthetic != providers.Synthetic)
            throw Failure("drive_not_connected", "Enable/connect this Google Drive Account in Admin → Credentials. Email authorization does not grant Drive access.", 403);
        return new(instance, binding, c, providers.Create(c.CredentialId, c.Revision));
    }
    public async ValueTask RecheckAsync(DriveAccess access, bool write, CancellationToken ct)
    {
        var current = await ResolveAsync(access.InstanceId, access.Binding.Reference, [access.Binding], write, ct);
        if (current.Credential.CredentialId != access.Credential.CredentialId || current.Credential.Revision != access.Credential.Revision || current.Credential.DriveAccount!.AuthEpoch != access.Credential.DriveAccount!.AuthEpoch)
            throw Failure("drive_access_changed", "Drive authorization changed. Read the current account and file state before acting.", 403);
    }
    public static void Epoch(DriveAccess access, long expected) { if (expected != access.Credential.DriveAccount!.AuthEpoch) throw Failure("drive_access_changed", "Drive authorization epoch changed. Read drive.accounts again before acting.", 409); }
    public async ValueTask<DriveFilePage> ListAsync(DriveAccess access, string? folder, string? query, string? cursor, int limit, CancellationToken ct)
    {
        if (limit is < 1 or > 100 || query?.Length > 200 || cursor?.Length > 2048) throw Failure("drive_invalid", "Drive list/search exceeds bounds."); if (folder is not null) Id(folder);
        var page = await access.Provider.ListAsync(folder, query, cursor, limit, ct); await RecheckAsync(access, false, ct); return page;
    }
    public async ValueTask<DriveFile> GetAsync(DriveAccess access, string id, CancellationToken ct) { var file = await access.Provider.GetAsync(Id(id), ct); await RecheckAsync(access, false, ct); return file; }
    private async ValueTask<DriveFile> ReadForOperation(DriveAccess access, string id, DriveOperation op, string stage, CancellationToken ct)
    {
        try { return await GetAsync(access, id, ct); }
        catch (Exception ex)
        {
            logger?.LogWarning("Drive operation read failed. OperationId {OperationId} Tool {Tool} Stage {Stage} Code {Code}",
                op.OperationId, op.Tool, stage, (ex as AgentCoreException)?.Code ?? "drive_provider_unavailable");
            throw;
        }
    }
    public async ValueTask<(DriveFile File, byte[] Bytes)> ReadAsync(DriveAccess access, string id, string? expectedVersion, CancellationToken ct)
    {
        var file = await GetAsync(access, id, ct); if (file.Folder || file.NativeDocument) throw Failure("drive_native_unsupported", "Preview/download supports binary files. Google-native export and folder download are deferred.");
        if (expectedVersion is not null && file.Version != expectedVersion) throw Failure("drive_remote_conflict", "Drive file changed. Reload its source list.", 409);
        if (file.ByteSize > MaxTransferBytes) throw Failure("drive_transfer_too_large", "Drive file exceeds 50 MiB.", 413);
        var bytes = await access.Provider.DownloadAsync(file, ct); if (bytes.Length > MaxTransferBytes || bytes.LongLength != file.ByteSize || file.Md5Checksum is not null && !string.Equals(Convert.ToHexStringLower(MD5.HashData(bytes)), file.Md5Checksum, StringComparison.OrdinalIgnoreCase)) throw Failure("drive_remote_conflict", "Drive content changed during download. No local bytes were committed.", 409);
        var after = await GetAsync(access, id, ct); if (after.Version != file.Version) throw Failure("drive_remote_conflict", "Drive file changed during download. No local bytes were committed.", 409); return (file, bytes);
    }
    public async ValueTask<DriveReceipt> EffectAsync(DriveAccess access, Guid operationId, string tool, long expectedEpoch, string? fileId, DriveEffect effect, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        var gate = Gate(access); await gate.WaitAsync(ct);
        try { return await EffectCore(access, operationId, tool, expectedEpoch, fileId, effect, bytes, ct); }
        finally { gate.Release(); }
    }
    private async ValueTask<DriveReceipt> EffectCore(DriveAccess access, Guid operationId, string tool, long expectedEpoch, string? fileId, DriveEffect effect, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        if (tool == "drive.move" && (effect.Parent == "root" || effect.Parent == effect.SourceParent)) throw Failure("drive_operation_unsupported", "Move requires a different concrete app-authorized folder ID; root alias moves are deferred.");
        if (tool == "drive.upload")
        {
            if (effect.SourceBytes != bytes.Length || effect.SourceSha256 != Convert.ToHexStringLower(SHA256.HashData(bytes.Span)))
                throw Failure("drive_source_revision_conflict", "Upload bytes do not match the immutable source snapshot.", 409);
            effect = effect with { SourceMd5 = Convert.ToHexStringLower(MD5.HashData(bytes.Span)), MimeType = effect.MimeType ?? "application/octet-stream" };
            if (!MediaTypeHeaderValue.TryParse(effect.MimeType, out var uploadType) || uploadType.MediaType is null
                || uploadType.MediaType.StartsWith("application/vnd.google-apps.", StringComparison.OrdinalIgnoreCase))
                throw Failure("drive_invalid", "Use a valid binary media type; Google-native conversion is excluded.");
        }
        Epoch(access, expectedEpoch); await RecheckAsync(access, true, ct);
        if (tool is "drive.trash" or "drive.move")
        { if (effect.Name.Length is < 1 or > 255 || effect.Name.Any(char.IsControl)) throw Failure("drive_invalid", "Existing target name exceeds its metadata bound."); }
        else Name(effect.Name);
        Id(effect.Parent);
        if (bytes.Length > MaxTransferBytes || effect.MimeType?.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal) == true && tool == "drive.upload") throw Failure("drive_invalid", "Use bounded binary files; Google-native conversion/overwrite is deferred.");
        var snapshot = JsonSerializer.Serialize(new { tool, fileId, effect }); var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot)));
        var known = await operations.GetAsync(access.InstanceId, operationId, ct);
        if (known is not null && (known.CredentialId != access.Credential.CredentialId || known.EffectHash != hash))
            throw AgentCoreErrors.Conflict("Drive operation identity is already claimed.");
        // Exact identity takes precedence over coalescing a later matching effect.
        // A terminal ID must never act as a different pending operation.
        var previous = known ?? await operations.FindAsync(access.InstanceId, access.Credential.CredentialId, hash, ct);
        if (previous is { State: "Planned" } && previous.AuthEpoch != expectedEpoch)
        { await operations.UpdateAsync(previous with { State = "Rejected", Revision = previous.Revision + 1 }, previous.Revision, ct); previous = null; }
        var op = previous ?? await operations.ClaimAsync(new(operationId, access.InstanceId, access.Credential.CredentialId, hash, expectedEpoch, tool, fileId ?? await access.Provider.GenerateIdAsync(ct), snapshot, "Planned", 1, time.GetUtcNow()), ct);
        if (op.State == "Succeeded") { await RecheckAsync(access, false, ct); return new(op.OperationId, "Succeeded", JsonSerializer.Deserialize<DriveFile>(op.ResultJson!)!, true, providers.Synthetic); }
        if (op.State == "Failed") throw FailedOperation(op);
        if (op.State is "Dispatched" or "Indeterminate")
        {
            try { return await Reconcile(access, op, effect, bytes, ct); }
            catch (DriveIndeterminateException ex) when (ex.ResumeAllowed)
            {
                // Only the same pinned bytes/session may continue. GET inspection never sends content.
                op = (await operations.GetAsync(access.InstanceId, op.OperationId, ct))!;
            }
        }
        if (op.State is not ("Planned" or "Indeterminate" or "Dispatched")) throw Failure("drive_operation_pending", "The operation cannot be dispatched again. Inspect its receipt.", 409);
        if (op.AuthEpoch != expectedEpoch) throw Failure("drive_access_changed", "The planned operation belongs to an earlier authorization epoch. Reconnect/inspect before acting.", 409);
        if ((tool is "drive.create_folder" or "drive.upload" or "drive.move") && effect.Parent != "root")
        { var parent = await ReadForOperation(access, effect.Parent, op, "parent_metadata", ct); if (!parent.Folder || parent.Trashed) throw Failure("drive_invalid", "Destination must be an available app-authorized folder."); }
        if (fileId is not null)
        {
            var observed = await ReadForOperation(access, fileId, op, "target_metadata", ct);
            if (effect.ExpectedVersion is null || observed.Version != effect.ExpectedVersion)
            {
                logger?.LogWarning("Drive operation preflight failed. OperationId {OperationId} Tool {Tool} Stage {Stage} Code {Code}", op.OperationId, tool, "preflight_version", "drive_remote_conflict");
                throw Failure("drive_remote_conflict", "Remote version changed. Inspect it before approval/dispatch.", 409);
            }
            if (tool == "drive.trash" && observed.Folder) throw Failure("drive_operation_unsupported", "Folder trash is deferred. Select one file.");
            if (tool == "drive.move" && (effect.SourceParent is null || !observed.Parents.Contains(effect.SourceParent))) throw Failure("drive_remote_conflict", "Source parent changed.", 409);
        }
        await RecheckAsync(access, true, ct);
        if (op.State == "Planned") op = await operations.UpdateAsync(op with { State = "Dispatched", Revision = op.Revision + 1 }, op.Revision, ct);
        try
        {
            logger?.LogDebug("Drive operation dispatch. OperationId {OperationId} Tool {Tool} Stage {Stage}", op.OperationId, tool, "dispatch");
            switch (tool)
            {
                case "drive.create_folder": await access.Provider.CreateFolderAsync(op.FileId, effect.Name, effect.Parent, ct); break;
                case "drive.upload": await access.Provider.UploadAsync(op.FileId, effect.Name, effect.Parent, effect.MimeType ?? "application/octet-stream", bytes, op, ct); break;
                case "drive.rename": await access.Provider.RenameAsync(op.FileId, effect.Name, ct); break;
                case "drive.move": await access.Provider.MoveAsync(op.FileId, effect.SourceParent!, effect.Parent, ct); break;
                case "drive.trash": await access.Provider.TrashAsync(op.FileId, ct); break;
                default: throw Failure("drive_invalid", "Unsupported Drive effect.");
            }
            op = await operations.FindAsync(access.InstanceId, access.Credential.CredentialId, hash, ct) ?? op;
            return await Reconcile(access, op, effect, bytes, ct);
        }
        catch (DriveIndeterminateException) { throw; }
        catch (AgentCoreException ex) when (ex.Code == "drive_operation_failed") { throw; }
        catch (DriveEffectNotExecutedException ex) when (op.ProtectedUploadSession is null)
        {
            logger?.LogWarning("Drive operation rejected. OperationId {OperationId} Tool {Tool} Stage {Stage} Code {Code}", op.OperationId, tool, "dispatch", ex.Code);
            var current = (await operations.GetAsync(access.InstanceId, op.OperationId, CancellationToken.None))!;
            var failed = await operations.UpdateAsync(current with { State = "Failed", Revision = current.Revision + 1,
                ResultJson = JsonSerializer.Serialize(new DriveOperationFailure(ex.Code, ex.Message, "initial_request_not_executed")) }, current.Revision, CancellationToken.None);
            throw FailedOperation(failed);
        }
        catch (Exception ex)
        {
            logger?.LogWarning("Drive operation uncertain. OperationId {OperationId} Tool {Tool} Stage {Stage} Code {Code}", op.OperationId, tool, "dispatch", (ex as AgentCoreException)?.Code ?? "drive_provider_unavailable");
            var current = await operations.FindAsync(access.InstanceId, access.Credential.CredentialId, hash, CancellationToken.None) ?? op;
            if (current.State != "Indeterminate") await operations.UpdateAsync(current with { State = "Indeterminate", Revision = current.Revision + 1 }, current.Revision, CancellationToken.None);
            throw new DriveIndeterminateException(op.OperationId, op.FileId, (ex as AgentCoreException)?.Code, CanResume(access, current));
        }
    }
    public async ValueTask<DriveReceipt> InspectOperationAsync(DriveAccess access, Guid id, CancellationToken ct)
    {
        var gate = Gate(access); await gate.WaitAsync(ct);
        try { return await InspectCore(access, id, ct); }
        finally { gate.Release(); }
    }
    private async ValueTask<DriveReceipt> InspectCore(DriveAccess access, Guid id, CancellationToken ct)
    {
        var op = await operations.GetAsync(access.InstanceId, id, ct);
        if (op is null || op.CredentialId != access.Credential.CredentialId) throw AgentCoreErrors.NotFound("Drive operation was not found.");
        if (op.State == "Succeeded") { await RecheckAsync(access, false, ct); return new(op.OperationId, "Succeeded", JsonSerializer.Deserialize<DriveFile>(op.ResultJson!)!, true, providers.Synthetic); }
        if (op.State == "Failed") { await RecheckAsync(access, false, ct); throw FailedOperation(op); }
        if (op.State is not ("Dispatched" or "Indeterminate")) throw Failure("drive_operation_pending", "This operation has not dispatched a remote effect; inspect the original task before acting.", 409);
        using var snapshot = JsonDocument.Parse(op.SnapshotJson); var effect = snapshot.RootElement.GetProperty("effect").Deserialize<DriveEffect>() ?? throw Failure("drive_invalid", "Drive receipt is invalid.");
        return await Reconcile(access, op, effect, ReadOnlyMemory<byte>.Empty, ct);
    }
    private async ValueTask<DriveReceipt> Reconcile(DriveAccess access, DriveOperation op, DriveEffect effect, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        DriveFile file;
        try { file = await ReadForOperation(access, op.FileId, op, "reconciliation", ct); }
        catch (Exception ex) { if (op.State != "Indeterminate") await operations.UpdateAsync(op with { State = "Indeterminate", Revision = op.Revision + 1 }, op.Revision, CancellationToken.None); throw new DriveIndeterminateException(op.OperationId, op.FileId, (ex as AgentCoreException)?.Code, CanResume(access, op)); }
        if (op.Tool == "drive.upload" && file.Id == op.FileId && file.Trashed)
        {
            await RecheckAsync(access, false, ct);
            var failure = new DriveOperationFailure("drive_upload_target_trashed",
                $"The known upload target ({op.FileId}) is in Google Drive Trash. This operation will not resume and no active upload success is claimed. Restore that file in Drive, or explicitly start a new upload with a new operation ID.",
                "verified_target_trashed", file);
            var failed = await operations.UpdateAsync(op with { State = "Failed", ResultJson = JsonSerializer.Serialize(failure),
                ProtectedUploadSession = null, Revision = op.Revision + 1 }, op.Revision, ct);
            throw FailedOperation(failed);
        }
        var verificationFailures = op.Tool == "drive.upload" ? UploadVerificationFailures(file, effect) : [];
        if (op.Tool == "drive.upload" && file.Id != op.FileId) verificationFailures.Add("fileId");
        var matches = op.Tool switch
        {
            "drive.create_folder" => file.Folder && file.Name == effect.Name && (effect.Parent == "root" || file.Parents.Contains(effect.Parent)),
            "drive.upload" => verificationFailures.Count == 0,
            "drive.rename" => file.Name == effect.Name,
            "drive.move" => file.Parents.Contains(effect.Parent) && !file.Parents.Contains(effect.SourceParent!),
            "drive.trash" => file.Trashed,
            _ => false
        };
        if (!matches) { if (op.State != "Indeterminate") await operations.UpdateAsync(op with { State = "Indeterminate", Revision = op.Revision + 1 }, op.Revision, CancellationToken.None);
            var resume = CanResume(access, op) && (op.Tool != "drive.upload" || string.IsNullOrEmpty(file.Md5Checksum));
            throw new DriveIndeterminateException(op.OperationId, op.FileId, op.Tool == "drive.upload" ? "drive_upload_verification_failed" : null, resume, verificationFailures); }
        await RecheckAsync(access, false, ct); await operations.UpdateAsync(op with { State = "Succeeded", ResultJson = JsonSerializer.Serialize(file), ProtectedUploadSession = null, Revision = op.Revision + 1 }, op.Revision, ct);
        return new(op.OperationId, "Succeeded", file, true, providers.Synthetic, op.Tool is "drive.rename" or "drive.move" or "drive.trash" ? "Remote preconditions are best effort against independent Google clients; no atomic conditional-write guarantee is claimed." : null);
    }

    private static List<string> UploadVerificationFailures(DriveFile file, DriveEffect effect)
    {
        var failures = new List<string>();
        if (file.Folder || file.NativeDocument) failures.Add("binaryType");
        if (file.Name != effect.Name) failures.Add("name");
        if (!MediaTypeHeaderValue.TryParse(file.MimeType, out var observed) || !MediaTypeHeaderValue.TryParse(effect.MimeType, out var requested)
            || !string.Equals(observed.MediaType, requested.MediaType, StringComparison.OrdinalIgnoreCase)) failures.Add("mimeType");
        if (file.ByteSize != effect.SourceBytes) failures.Add("byteSize");
        if (string.IsNullOrEmpty(effect.SourceMd5) || !string.Equals(file.Md5Checksum, effect.SourceMd5, StringComparison.OrdinalIgnoreCase)) failures.Add("checksum");
        if (effect.Parent != "root" && !file.Parents.Contains(effect.Parent)) failures.Add("parent");
        return failures;
    }
}
