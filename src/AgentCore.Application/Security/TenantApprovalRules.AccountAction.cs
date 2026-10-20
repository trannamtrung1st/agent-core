using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static partial class TenantApprovalRules
{
    internal static void ValidateAccountWitness(Guid operation, Guid instance, PreparedCredentialConnectionCheck value,
        Domain.Credentials.CredentialPurpose purpose, long instanceRevision, long personaRevision)
    {
        if (operation == Guid.Empty || instance == Guid.Empty || value.CredentialId == Guid.Empty || value.BindingId == Guid.Empty
            || value.Reference != Domain.Credentials.CredentialRules.Reference(value.Reference) || value.BindingRevision < 1
            || value.CredentialRevision < 1 || value.SecretRevision < (purpose == Domain.Credentials.CredentialPurpose.EmailAccount ? 0 : 1) || instanceRevision < 1 || personaRevision < 1
            || purpose is not (Domain.Credentials.CredentialPurpose.EmailAccount or Domain.Credentials.CredentialPurpose.GoogleDriveAccount)
            || value.AuthEpoch < (purpose == Domain.Credentials.CredentialPurpose.EmailAccount ? 0 : 1)) throw new ArgumentException("Invalid exact bound account witness.");
    }
    private static readonly JsonSerializerOptions AccountSourcesJson = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public static IReadOnlyList<PreparedAccountRemoteFile> AccountRemoteSources(PreparedAccountAction action)
    {
        if (Encoding.UTF8.GetByteCount(action.RemoteSourcesJson) > 32768) throw new ArgumentException("Bound remote source metadata.");
        var files = JsonSerializer.Deserialize<PreparedAccountRemoteFile[]>(action.RemoteSourcesJson, AccountSourcesJson)
            ?? throw new ArgumentException("Use exact remote source metadata.");
        if (files.Length > 21 || files.Select(f => f.Id).Distinct(StringComparer.Ordinal).Count() != files.Length
            || JsonSerializer.Serialize(files) != action.RemoteSourcesJson) throw new ArgumentException("Use distinct canonical remote sources.");
        foreach (var f in files)
        {
            _ = Drive.DriveService.Id(f.Id); _ = Drive.DriveMutationArguments.Version(f.Version);
            if (f.Name.Length is < 1 or > 255 || f.Name.Any(char.IsControl) || f.MimeType.Length is < 1 or > 255
                || f.MimeType.Any(char.IsControl) || f.ByteSize < 0
                || f.Parents.Count > 2 || f.Parents.Any(p => Drive.DriveService.Id(p) != p) || f.Trashed
                || f.Md5Checksum is { } hash && (hash.Length != 32 || hash.Any(c => !char.IsAsciiHexDigit(c))))
                throw new ArgumentException("Invalid remote source metadata.");
        }
        return Array.AsReadOnly(files);
    }
    public static PreparedTenantOperation PrepareAccountAction(Guid operation, Guid instance, PreparedAccountAction action,
        long instanceRevision, long personaRevision)
    {
        var args = TenantAccountActionContract.Parse(action.SessionId, action.ToolName, action.ArgumentsJson);
        ValidateAccountWitness(operation, instance, action.Credential, action.Purpose, instanceRevision, personaRevision);
        if (args.Json != action.ArgumentsJson || args.Purpose != action.Purpose || args.Reference != action.Credential.Reference || args.Permission != action.Permission)
            throw new ArgumentException("Invalid exact bound-account action.");
        var remote = AccountRemoteSources(action);
        using var doc = JsonDocument.Parse(args.Json); var input = doc.RootElement;
        if (args.Purpose == Domain.Credentials.CredentialPurpose.EmailAccount)
        {
            if (remote.Count != 0 || action.UploadSource is not null || action.DestinationObservationHash is not null || action.Draft is not { } draft
                || draft.DraftId.Length is < 1 or > 200 || draft.DraftId.Any(char.IsControl) || draft.Revision < 1
                || !EmailHeaderSafety.TryValidateAddress(draft.Sender, out _)) throw new ArgumentException("Invalid immutable email draft evidence.");
            if (Encoding.UTF8.GetByteCount(draft.RecipientsJson) > 32768) throw new ArgumentException("Bound exact draft recipients.");
            using var recipients = JsonDocument.Parse(draft.RecipientsJson);
            TenantApprovalToolContract.Require(recipients.RootElement, ["to", "cc", "bcc"]);
            var checkedDraft = TenantAccountActionContract.Parse(action.SessionId, ToolCatalog.EmailCreateDraft, JsonSerializer.Serialize(new {
                accountRef = args.Reference, to = recipients.RootElement.GetProperty("to"), cc = recipients.RootElement.GetProperty("cc"), bcc = recipients.RootElement.GetProperty("bcc"), subject = draft.Subject, body = draft.Body }));
            if (args.ToolName == ToolCatalog.EmailCreateDraft ? args.Json != checkedDraft.Json : args.DraftId != draft.DraftId)
                throw new ArgumentException("Draft intent does not match its immutable content.");
        }
        else
        {
            if (action.Draft is not null || !args.Upload && action.UploadSource is not null || !args.Download && action.DestinationObservationHash is not null)
                throw new ArgumentException("Unexpected account source evidence.");
            if (args.Upload)
            {
                if (action.UploadSource is not { } upload || upload.ResourceKey is not null || upload.AttachmentId is not null)
                    throw new ArgumentException("Upload requires exact owned workspace bytes.");
                TenantArtifactSourceRules.ValidateWitness(upload, args.Path!);
                if (upload.ByteSize > Drive.DriveService.MaxTransferBytes) throw new ArgumentException("Upload exceeds the byte ceiling.");
            }
            if (args.Download && !Hash(action.DestinationObservationHash)) throw new ArgumentException("Download requires an exact new-destination witness.");
            if (!args.Download && input.GetProperty("authEpoch").GetInt64() != action.Credential.AuthEpoch) throw new ArgumentException("Account epoch changed.");
            var expected = new HashSet<string>(StringComparer.Ordinal);
            void File(string id, string version)
            {
                expected.Add(id); var file = remote.SingleOrDefault(f => f.Id == id);
                if (file is null || file.Version != version) throw new ArgumentException("Missing exact inspected remote file.");
                if ((args.Download || args.ToolName is ToolCatalog.DriveTrash or ToolCatalog.DriveTrashBatch) && file.MimeType == "application/vnd.google-apps.folder"
                    || args.Download && file.MimeType.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal))
                    throw new ArgumentException("Folder trash/download and native exports are unsupported.");
            }
            void Folder(string id)
            {
                if (id == "root") return;
                expected.Add(id);
                if (!remote.Any(f => f.Id == id && f.MimeType == "application/vnd.google-apps.folder")) throw new ArgumentException("Missing inspected destination folder.");
            }
            if (args.ToolName == ToolCatalog.DriveTrashBatch)
                foreach (var file in Drive.DriveMutationArguments.Manifest(input)) File(file.FileId, file.ExpectedVersion);
            else if (input.TryGetProperty("fileId", out var id)) File(id.GetString()!, input.GetProperty("expectedVersion").GetString()!);
            if (args.ToolName == ToolCatalog.DriveCreateFolder) Folder(input.GetProperty("parentId").GetString()!);
            if (args.Upload) Folder(input.GetProperty("folderId").GetString()!);
            if (args.ToolName == ToolCatalog.DriveMove)
            {
                Folder(input.GetProperty("destinationParentId").GetString()!);
                if (!remote.Single(f => f.Id == input.GetProperty("fileId").GetString()).Parents.Contains(input.GetProperty("sourceParentId").GetString()!, StringComparer.Ordinal))
                    throw new ArgumentException("Move source membership changed.");
            }
            if (!expected.SetEquals(remote.Select(f => f.Id))) throw new ArgumentException("Unexpected remote source metadata.");
        }
        var json = JsonSerializer.Serialize(new { operation = "accounts.action", permission = action.Permission,
            expectedInstanceRevision = instanceRevision, expectedPersonaRevision = personaRevision, action });
        if (Encoding.UTF8.GetByteCount(json) > 128 * 1024) throw new ArgumentException("Account action evidence exceeds bounds.");
        return new(operation, instance, null, instanceRevision, personaRevision, json,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))), AccountAction: action);
        static bool Hash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);
    }
}
