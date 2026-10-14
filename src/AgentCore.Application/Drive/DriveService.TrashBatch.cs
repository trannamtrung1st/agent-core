using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;

namespace AgentCore.Application.Drive;

public sealed record DriveTrashBatchItem(string FileId, string? Name, string ExpectedVersion, string Status,
    Guid? OperationId = null, string? ErrorCode = null, string? Message = null);
public sealed record DriveTrashBatchResult(int RequestedCount, int TrashedCount, int FailedCount, int UncertainCount,
    int NotAttemptedCount, IReadOnlyList<DriveTrashBatchItem> Items, string Scope = "app_authorized_files_only",
    string Policy = "Not atomic. Stops on the first failure or uncertain outcome. Inspect remaining files and obtain fresh exact approval; no blind retry or rollback.");

public sealed partial class DriveService
{
    private static Guid TrashOperationId(Guid batch, int index, string id)
        => new(SHA256.HashData(Encoding.UTF8.GetBytes($"{batch:D}|{index}|{id}")).AsSpan(0, 16));
    private static DriveTrashBatchResult BatchResult(DriveTrashBatchItem[] items) => new(items.Length,
        items.Count(i => i.Status == "Succeeded"), items.Count(i => i.Status == "Failed"),
        items.Count(i => i.Status == "Indeterminate"), items.Count(i => i.Status == "NotAttempted"), items);

    public async ValueTask<DriveTrashBatchResult> TrashBatchAsync(DriveAccess access, Guid batchId, long epoch,
        IReadOnlyList<DriveTrashTarget> manifest, CancellationToken ct)
    {
        // Freeze the ordered approved targets before yielding to a gate or provider.
        manifest = manifest.ToArray();
        if (batchId == Guid.Empty) throw AgentCoreErrors.Validation("Batch operation identity is required.");
        if (manifest.Count is < 1 or > DriveMutationArguments.MaxBatchFiles || manifest.Select(t => t.FileId).Distinct(StringComparer.Ordinal).Count() != manifest.Count)
            throw AgentCoreErrors.Validation("Trash batch requires 1–20 distinct exact targets.");
        foreach (var target in manifest) { Id(target.FileId); DriveMutationArguments.Version(target.ExpectedVersion); }
        var gate = Gate(access); await gate.WaitAsync(ct);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60), time);
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            return await TrashBatchCore(access, batchId, epoch, manifest, bounded.Token);
        }
        finally { gate.Release(); }
    }
    private async ValueTask<DriveTrashBatchResult> TrashBatchCore(DriveAccess access, Guid batchId, long epoch,
        IReadOnlyList<DriveTrashTarget> manifest, CancellationToken ct)
    {
        Epoch(access, epoch); await RecheckAsync(access, true, ct);
        var items = manifest.Select(t => new DriveTrashBatchItem(t.FileId, null, t.ExpectedVersion, "NotAttempted")).ToArray();
        var effects = new DriveEffect?[manifest.Count];
        // Resolve every new target before the first mutation. Replay reads exact receipts first.
        for (var index = 0; index < manifest.Count; index++)
        {
            var target = manifest[index]; var operationId = TrashOperationId(batchId, index, target.FileId);
            try
            {
                var prior = await operations.GetAsync(access.InstanceId, operationId, ct);
                if (prior is not null)
                {
                    using var snapshot = JsonDocument.Parse(prior.SnapshotJson);
                    var priorEffect = snapshot.RootElement.GetProperty("effect").Deserialize<DriveEffect>();
                    if (prior.Tool != "drive.trash" || prior.FileId != target.FileId || prior.CredentialId != access.Credential.CredentialId
                        || prior.AuthEpoch != epoch || priorEffect?.ExpectedVersion != target.ExpectedVersion)
                        throw AgentCoreErrors.Conflict("Batch receipt intent changed. Request fresh exact approval.");
                    if (prior.State is "Succeeded" or "Dispatched" or "Indeterminate" or "Failed")
                    {
                        var receipt = await InspectCore(access, operationId, ct);
                        items[index] = items[index] with { Name = receipt.File.Name, Status = "Succeeded", OperationId = operationId };
                        continue;
                    }
                }
                var file = await GetAsync(access, target.FileId, ct);
                items[index] = items[index] with { Name = file.Name };
                if (file.Version != target.ExpectedVersion) throw Failure("drive_remote_conflict", "A target changed before batch dispatch. Refresh the manifest and approval.", 409);
                if (file.Folder || file.Trashed) throw Failure("drive_operation_unsupported", "Folder trash and already-trashed new targets are excluded.");
                effects[index] = new(file.Name, file.Parents.FirstOrDefault() ?? "root", ExpectedVersion: target.ExpectedVersion);
            }
            catch (DriveIndeterminateException ex)
            {
                items[index] = items[index] with { Status = "Indeterminate", OperationId = ex.OperationId, ErrorCode = "drive_effect_indeterminate", Message = ex.Message };
                return BatchResult(items);
            }
            catch (AgentCoreException ex)
            {
                items[index] = items[index] with { Status = "Failed", ErrorCode = ex.Code, Message = ex.Message };
                return BatchResult(items);
            }
        }
        for (var index = 0; index < manifest.Count; index++)
        {
            if (items[index].Status == "Succeeded") continue;
            if (ct.IsCancellationRequested) return BatchResult(items);
            var operationId = TrashOperationId(batchId, index, manifest[index].FileId);
            try
            {
                var receipt = await EffectCore(access, operationId, "drive.trash", epoch, manifest[index].FileId,
                    effects[index]!, ReadOnlyMemory<byte>.Empty, ct);
                items[index] = items[index] with { Name = receipt.File.Name, Status = "Succeeded", OperationId = operationId };
            }
            catch (DriveIndeterminateException ex)
            {
                items[index] = items[index] with { Status = "Indeterminate", OperationId = ex.OperationId, ErrorCode = "drive_effect_indeterminate", Message = ex.Message };
                break;
            }
            catch (AgentCoreException ex)
            {
                items[index] = items[index] with { Status = "Failed", OperationId = operationId, ErrorCode = ex.Code, Message = ex.Message };
                break;
            }
            catch (OperationCanceledException) { break; }
        }
        return BatchResult(items);
    }
}
