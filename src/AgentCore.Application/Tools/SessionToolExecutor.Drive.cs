using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Drive;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Application.Ports;
namespace AgentCore.Application.Tools;

public sealed partial class SessionToolExecutor
{
    private async Task<string> ExecuteDriveAsync(AgentDefinition definition, Guid sessionId, ModelToolCall call, JsonElement args, ToolExecutionAdmission? admission, ToolApprovalGrant? grant, CancellationToken ct)
    {
        if (driveService is null || agentWorkspace is null || admission?.AgentInstanceId is not Guid instanceId || await agentWorkspace.SessionOwnerAsync(sessionId, ct) != instanceId)
            return Error("forbidden", "Drive requires the current owned Session/Agent Instance.");
        try
        {
            using var schema = JsonDocument.Parse(ToolRegistry.Get(call.Name).ModelDefinition.ParametersJson);
            var allowed = schema.RootElement.GetProperty("properties").EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
            if (args.ValueKind != JsonValueKind.Object || args.EnumerateObject().Any(property => !allowed.Contains(property.Name)) || args.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != args.EnumerateObject().Count()) return Error("invalid", "Drive arguments must match the closed tool schema.");
            foreach (var property in args.EnumerateObject())
            {
                var propertySchema = schema.RootElement.GetProperty("properties").GetProperty(property.Name);
                if (propertySchema.TryGetProperty("type", out var type) && (type.GetString() == "string" && property.Value.ValueKind != JsonValueKind.String
                    || type.GetString() == "integer" && (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt64(out _)))) return Error("invalid", "Drive argument type does not match its schema.");
            }
            string? Text(string key) => args.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            string Required(string key) => Text(key) ?? throw AgentCoreErrors.Validation(key + " is required.");
            if (call.Name == "drive.accounts") return JsonSerializer.Serialize(new { accounts = await driveService.AccountsAsync(instanceId, admission.PinnedCredentialBindings, ct), limitedToAppAuthorizedFiles = true }, OfficeJson);
            var write = call.Name is not ("drive.list" or "drive.search" or "drive.get");
            var account = await driveService.ResolveAsync(instanceId, Text("accountRef"), admission.PinnedCredentialBindings, write, ct);
            if (call.Name is "drive.list" or "drive.search")
            {
                var limit = args.TryGetProperty("limit", out var l) && l.TryGetInt32(out var value) ? value : 20;
                var page = await driveService.ListAsync(account, Text("folderId"), call.Name == "drive.search" ? Required("query") : Text("query"), Text("cursor"), limit, ct); return JsonSerializer.Serialize(new { untrusted = true, page }, OfficeJson);
            }
            if (call.Name == "drive.get")
            {
                if (Text("operationId") is { } operationText) { if (!Guid.TryParse(operationText, out var priorId)) return Error("invalid", "operationId must be a UUID."); return JsonSerializer.Serialize(new { untrusted = true, receipt = await driveService.InspectOperationAsync(account, priorId, ct) }, OfficeJson); }
                return JsonSerializer.Serialize(new { untrusted = true, file = await driveService.GetAsync(account, Required("fileId"), ct) }, OfficeJson);
            }
            if (call.Name == "drive.download")
            {
                var path = AgentWorkspacePaths.Resolve(Required("path"), sessionId, admission.WorkspaceCwd ?? "/home", true);
                if (!path.StartsWith("/home/", StringComparison.Ordinal) && !path.StartsWith(WorkspaceLogicalPath.WorkingDirectory + "/", StringComparison.Ordinal)) return Error("forbidden", "Use /home or this Session's /working.");
                var content = await driveService.ReadAsync(account, Required("fileId"), Required("expectedVersion"), ct); await driveService.RecheckAsync(account, true, ct);
                await agentWorkspace.CommitBinaryAsync(sessionId, path, content.File.MimeType, content.Bytes, null, ct);
                return JsonSerializer.Serialize(new { path = AgentWorkspacePaths.Public(path), sha256 = Convert.ToHexStringLower(SHA256.HashData(content.Bytes)), byteSize = content.Bytes.Length, remoteFileId = content.File.Id }, OfficeJson);
            }
            if (!args.TryGetProperty("authEpoch", out var epoch) || !epoch.TryGetInt64(out var authEpoch) || authEpoch < 1) return Error("invalid", "authEpoch from drive.accounts is required for remote writes.");
            if (call.Name is "drive.rename" or "drive.move" or "drive.trash")
                if (grant is null || grant.ToolName != call.Name || grant.ActionHash != ToolActionHash.Compute(call.Name, args)) return Error("approval_required", "Exact current Drive action approval is required.");
            var operationBytes = SHA256.HashData(Encoding.UTF8.GetBytes((admission.AgentRunId ?? sessionId).ToString("D") + "|" + call.Id + "|" + call.Name)); var operationId = new Guid(operationBytes.AsSpan(0, 16));
            DriveEffect effect; string? fileId = null; byte[] bytes = [];
            if (call.Name == "drive.upload")
            {
                var path = AgentWorkspacePaths.Resolve(Required("path"), sessionId, admission.WorkspaceCwd ?? "/home", true);
                if (!path.StartsWith("/home/", StringComparison.Ordinal) && !path.StartsWith(WorkspaceLogicalPath.WorkingDirectory + "/", StringComparison.Ordinal)) return Error("forbidden", "Upload uses current owned /home or /working bytes.");
                if (workspace is null) return Error("unavailable", "Workspace is unavailable."); await workspace.EnsureAsync(sessionId, definition, ct); var content = await ReadExecutionWorkspaceAsync(sessionId, definition, path, ct); bytes = content.Bytes;
                var hash = Convert.ToHexStringLower(SHA256.HashData(bytes)); if (!string.Equals(hash, Required("expectedSha256"), StringComparison.OrdinalIgnoreCase)) return Error("drive_source_revision_conflict", "The source file changed. Read its current SHA before uploading; no remote effect was dispatched.");
                effect = new(Text("name") ?? Path.GetFileName(path), Text("folderId") ?? "root", hash, bytes.Length, content.ContentType);
            }
            else if (call.Name == "drive.create_folder") effect = new(Required("name"), Text("parentId") ?? "root");
            else
            {
                fileId = Required("fileId"); var file = await driveService.GetAsync(account, fileId, ct);
                effect = new(call.Name == "drive.rename" ? Required("name") : file.Name, call.Name == "drive.move" ? Required("destinationParentId") : file.Parents.FirstOrDefault() ?? "root", ExpectedVersion: Required("expectedVersion"), SourceParent: Text("sourceParentId"));
            }
            var receipt = await driveService.EffectAsync(account, operationId, call.Name, authEpoch, fileId, effect, bytes, ct); return JsonSerializer.Serialize(new { untrusted = true, receipt }, OfficeJson);
        }
        catch (DriveIndeterminateException ex) { return JsonSerializer.Serialize(new { error = "drive_effect_indeterminate", message = ex.Message, operationId = ex.OperationId, fileId = ex.FileId, causeCode = ex.CauseCode, retryAllowed = false }, OfficeJson); }
        catch (OperationCanceledException) { throw; }
        catch (AgentCoreException ex) { return Error(ex.Code, ex.Message); }
        catch (Exception ex) when (ex is ArgumentException or JsonException or FormatException or InvalidOperationException) { return Error("drive_invalid", "Drive arguments/provider data could not be interpreted safely."); }
    }
}
