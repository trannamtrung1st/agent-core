using System.Text.Json;
using System.Text.Encodings.Web;
using AgentCore.Application.Drive;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;

namespace AgentCore.Application.Tools;

public sealed partial class SessionToolExecutor
{
    public async ValueTask<(ToolApprovalPreparation? Preparation, string? ErrorJson)> PrepareDriveApprovalAsync(
        ModelToolCall call, JsonElement args, Guid sessionId, ToolExecutionAdmission? admission, CancellationToken ct)
    {
        if (driveService is null || agentWorkspace is null || admission?.AgentInstanceId is not Guid owner
            || await agentWorkspace.SessionOwnerAsync(sessionId, ct) != owner)
            return (null, Error("forbidden", "Drive approval requires the current owned Session/Instance."));
        try
        {
            DriveMutationArguments.Validate(call.Name, args);
            var access = await driveService.ResolveAsync(owner, args.TryGetProperty("accountRef", out var alias) ? alias.GetString() : null,
                admission.PinnedCredentialBindings, true, ct);
            DriveService.Epoch(access, DriveMutationArguments.Epoch(args));
            var targets = call.Name == "drive.trash_batch" ? DriveMutationArguments.Manifest(args)
                : [new DriveTrashTarget(DriveService.Id(DriveMutationArguments.Text(args, "fileId")), DriveMutationArguments.Version(DriveMutationArguments.Text(args, "expectedVersion")))];
            var files = new List<object>();
            foreach (var target in targets)
            {
                var file = await driveService.GetAsync(access, target.FileId, ct);
                if (file.Version != target.ExpectedVersion) throw new AgentCoreException("drive_remote_conflict", "A target changed. Inspect the files and request a fresh exact approval.", 409);
                if (call.Name is "drive.trash" or "drive.trash_batch" && (file.Folder || file.Trashed))
                    throw new AgentCoreException("drive_operation_unsupported", "Select available files; folder trash and already-trashed targets are excluded.", 400);
                if (file.Name.Length > 255 || file.Name.Any(char.IsControl)) throw AgentCoreErrors.Validation("Target name exceeds the approval display bound.");
                files.Add(new { fileId = file.Id, name = file.Name, expectedVersion = file.Version });
            }
            var details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Account"] = ToolApprovalPreview.BoundDetailValue(access.Credential.DisplayName + " · " + access.Credential.DriveAccount!.EmailAddress)
                    + (access.Credential.DriveAccount!.Synthetic ? " · Synthetic" : ""),
                ["Scope"] = targets.Count + " listed app-authorized Drive " + (targets.Count == 1 ? "file" : "files"),
                ["Files"] = JsonSerializer.Serialize(files, new JsonSerializerOptions(OfficeJson) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
                ["Limitations"] = "Exact IDs, versions, account and epoch only. Independent Google clients can change files; execution rechecks before each effect."
            };
            if (details["Files"].Length > 24 * 1024) throw AgentCoreErrors.Validation("Drive approval file manifest exceeds its display bound.");
            string summary;
            if (call.Name is "drive.trash" or "drive.trash_batch")
            {
                details["Action"] = "Move to Google Drive Trash";
                details["Reversible"] = "Can be restored from Google Drive Trash; no permanent deletion.";
                summary = targets.Count == 1 ? "Move this app-authorized file to Google Drive Trash?" : $"Move {targets.Count} listed app-authorized Drive files to Trash?";
            }
            else if (call.Name == "drive.rename")
            {
                details["Action"] = "Rename file";
                details["New name"] = DriveService.Name(DriveMutationArguments.Text(args, "name"));
                summary = "Rename this app-authorized Drive file?";
            }
            else
            {
                var file = await driveService.GetAsync(access, targets[0].FileId, ct);
                var source = DriveService.Id(DriveMutationArguments.Text(args, "sourceParentId"));
                var destination = DriveService.Id(DriveMutationArguments.Text(args, "destinationParentId"));
                if (destination == "root" || source == destination || !file.Parents.Contains(source))
                    throw AgentCoreErrors.Validation("Move requires the current source and a different concrete destination.");
                var folder = await driveService.GetAsync(access, destination, ct);
                if (!folder.Folder || folder.Trashed) throw AgentCoreErrors.Validation("Destination must be an available app-authorized folder.");
                // Target metadata proves source membership; no extra source-folder authority is required.
                details["Action"] = "Move file";
                details["Source folder"] = source;
                details["Destination folder"] = folder.Name;
                summary = "Move this app-authorized Drive file?";
            }
            await driveService.RecheckAsync(access, true, ct);
            return (new(ToolActionHash.Compute(call.Name, args), summary, call.ArgumentsJson, details), null);
        }
        catch (OperationCanceledException) { throw; }
        catch (AgentCoreException ex) { return (null, Error(ex.Code, ex.Message)); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        { return (null, Error("drive_invalid", "Drive approval requires bounded current target metadata and exact arguments.")); }
    }
}
