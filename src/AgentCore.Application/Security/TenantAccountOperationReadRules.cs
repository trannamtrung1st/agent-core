using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Drive;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantAccountNativeReference(Guid OperationId, string? FileId, string? EffectHash, string? SnapshotHash);
public static class TenantAccountOperationReadRules
{
    public static void Validate(PreparedAccountRead read)
    {
        var args = TenantAccountReadContract.Parse(read.ToolName, read.ArgumentsJson);
        if (args.OperationId is null ? read.Source is not null : read.Source is null || args.OperationId != read.Source.OperationId)
            throw new ArgumentException("An operation read requires its exact canonical source.");
        if (read.Source is not { } source) return;
        if (source.OperationId == Guid.Empty || source.RequestId == Guid.Empty || source.RunId == Guid.Empty || source.SessionId == Guid.Empty
            || source.ActionHash.Length != 64 || source.ActionHash.Any(c => !char.IsAsciiHexDigit(c))
            || !TenantAccountActionContract.Tools.Contains(source.ToolName) || !source.ToolName.StartsWith("drive.", StringComparison.Ordinal)
            || !Enum.TryParse<TenantOperationOutcome>(source.RecordedOutcome, out var outcome) || !Enum.IsDefined(outcome))
            throw new ArgumentException("Invalid canonical operation source.");
        var refs = References(source);
        if (refs.Count > 20 || refs.Select(r => r.OperationId).Distinct().Count() != refs.Count
            || refs.Any(r => r.OperationId == Guid.Empty || r.FileId?.Length > 160
                || (r.EffectHash is null) != (r.SnapshotHash is null)
                || r.EffectHash is { } hash && (hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigit(c)))
                || r.SnapshotHash is { } snapshot && (snapshot.Length != 64 || snapshot.Any(c => !char.IsAsciiHexDigit(c)))))
            throw new ArgumentException("Invalid bounded native operation references.");
    }
    public static IReadOnlyList<TenantAccountNativeReference> References(PreparedAccountOperationRead source)
    {
        if (Encoding.UTF8.GetByteCount(source.NativeReferencesJson) > 16384) throw new ArgumentException("Native references exceed the bound.");
        var values = JsonSerializer.Deserialize<TenantAccountNativeReference[]>(source.NativeReferencesJson) ?? throw new ArgumentException("Native references are required.");
        if (values.Any(v => v is null) || JsonSerializer.Serialize(values) != source.NativeReferencesJson) throw new ArgumentException("Use canonical native references.");
        return values;
    }
    public static void Require(PreparedAccountRead read, Guid tenant, Guid instance, Guid principal,
        TenantApprovalRequest? original, TenantOperationReceipt? receipt, AgentRun? run, TenantSession? session)
    {
        Validate(read); if (read.Source is not { } source) return;
        var action = original?.Operation.AccountAction;
        if (original is null || receipt is null || run is null || session is null || action is null
            || original.TenantId != tenant || original.RequestId != source.RequestId || original.Status != TenantApprovalStatus.Approved
            || original.Operation.OperationId != source.OperationId || original.Operation.ActionHash != source.ActionHash
            || original.Operation.InstanceId != instance || original.ExecutorPrincipalId != principal || original.SessionId != source.SessionId
            || action.ToolName != source.ToolName || action.SessionId != source.SessionId
            || action.Credential with { SecretRevision = read.Credential.SecretRevision } != read.Credential
            || receipt.TenantId != tenant || receipt.OperationId != source.OperationId || receipt.RequestId != source.RequestId
            || receipt.RunId != source.RunId || receipt.SessionId != source.SessionId || receipt.InstanceId != instance
            || receipt.ExecutorPrincipalId != principal || receipt.ActionHash != source.ActionHash || receipt.Outcome.ToString() != source.RecordedOutcome
            || receipt.RootActivationId != original.RootActivationId || run.Owner.TenantId != tenant || run.AgentRunId != source.RunId
            || run.SessionId != source.SessionId || run.AgentInstanceId != instance || run.Owner.ExecutorPrincipalId != principal
            || run.Admission.ExecutionContext?.RootActivationId != original.RootActivationId
            || session.Owner.TenantId != tenant || session.Owner.SessionId != source.SessionId || session.Owner.AgentInstanceId != instance
            || session.Owner.ExecutorPrincipalId != principal || session.Snapshot.DurablyDeletedAt is not null)
            throw new AccessDeniedException("account_operation_source_unavailable");
        var references = References(source);
        using var args = JsonDocument.Parse(action.ArgumentsJson);
        if (action.ToolName == ToolCatalog.DriveDownload)
        { if (references.Count != 0) throw new AccessDeniedException("account_operation_source_unavailable"); }
        else if (action.ToolName == ToolCatalog.DriveTrashBatch)
        {
            var manifest = DriveMutationArguments.Manifest(args.RootElement);
            if (references.Count != manifest.Count || references.Where((r, i) => r.OperationId != DriveService.TrashOperationId(source.OperationId, i, manifest[i].FileId)
                || r.FileId != manifest[i].FileId).Any()) throw new AccessDeniedException("account_operation_source_unavailable");
        }
        else if (references.Count != 1 || references[0].OperationId != source.OperationId
            || args.RootElement.TryGetProperty("fileId", out var file) && references[0].FileId != file.GetString())
            throw new AccessDeniedException("account_operation_source_unavailable");
    }
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static void RequireNative(PreparedAccountAction action, Guid operationId, string tool, string? fileId, DriveOperation native)
    {
        using var json = JsonDocument.Parse(action.ArgumentsJson); var input = json.RootElement;
        var saved = TenantApprovalRules.AccountRemoteSources(action);
        DriveEffect expected;
        if (tool == ToolCatalog.DriveCreateFolder) expected = new(input.GetProperty("name").GetString()!, input.GetProperty("parentId").GetString()!);
        else if (tool == ToolCatalog.DriveUpload)
        {
            var source = action.UploadSource ?? throw new AccessDeniedException("account_operation_source_unavailable");
            using var snapshot = JsonDocument.Parse(native.SnapshotJson);
            var effect = snapshot.RootElement.GetProperty("effect").Deserialize<DriveEffect>()!;
            if (effect.SourceMd5 is not { Length: 32 } md5 || md5.Any(c => !char.IsAsciiHexDigit(c))) throw new AccessDeniedException("account_operation_source_unavailable");
            expected = new(input.GetProperty("name").GetString()!, input.GetProperty("folderId").GetString()!, source.Sha256Hex,
                source.ByteSize, source.ContentType ?? "application/octet-stream", SourceMd5: effect.SourceMd5);
        }
        else
        {
            var source = saved.Single(f => f.Id == fileId);
            var version = action.ToolName == ToolCatalog.DriveTrashBatch
                ? input.GetProperty("files").EnumerateArray().Single(f => f.GetProperty("fileId").GetString() == fileId).GetProperty("expectedVersion").GetString()
                : input.GetProperty("expectedVersion").GetString();
            expected = new(tool == ToolCatalog.DriveRename ? input.GetProperty("name").GetString()! : source.Name,
                tool == ToolCatalog.DriveMove ? input.GetProperty("destinationParentId").GetString()! : source.Parents.FirstOrDefault() ?? "root",
                ExpectedVersion: version, SourceParent: input.TryGetProperty("sourceParentId", out var parent) ? parent.GetString() : null);
        }
        var snapshotJson = JsonSerializer.Serialize(new { tool, fileId, effect = expected });
        if (native.OperationId != operationId || native.CredentialId != action.Credential.CredentialId || native.AuthEpoch != action.Credential.AuthEpoch
            || native.Tool != tool || fileId is not null && native.FileId != fileId || native.SnapshotJson != snapshotJson || native.EffectHash != Hash(snapshotJson))
            throw new AccessDeniedException("account_operation_source_unavailable");
    }
}
