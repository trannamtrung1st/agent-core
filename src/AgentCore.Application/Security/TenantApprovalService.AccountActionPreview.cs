using System.Text.Json;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantApprovalService
{
    private static IReadOnlyList<TenantApprovalFieldChange> AccountActionChanges(PreparedAccountAction action, TenantApprovalSnapshot? snapshot)
    {
        var changes = new List<TenantApprovalFieldChange> {
            new("accountName", null, snapshot?.Credential?.DisplayName, false),
            new("accountPurpose", null, action.Purpose == Domain.Credentials.CredentialPurpose.EmailAccount ? "Email" : "Google Drive", false),
            new("accountReference", null, action.Credential.Reference, false),
            new("accountActionTool", null, action.ToolName, false), new("accountActionArguments", null, action.ArgumentsJson, false)
        };
        if (action.Draft is { } draft)
        {
            var value = TenantAccountActionRules.DraftSnapshot(action);
            changes.AddRange([new("emailSender", null, draft.Sender, false), new("emailTo", null, string.Join("\n", value.To), false),
                new("emailCc", null, string.Join("\n", value.Cc), false), new("emailBcc", null, string.Join("\n", value.Bcc), false),
                new("emailSubject", null, draft.Subject, false), new("emailBody", null, draft.Body, false), new("emailDraftId", null, draft.DraftId, false),
                new("accountActionDisclosure", null, action.ToolName == ToolCatalog.EmailSend
                    ? "Attempt delivery of this exact immutable draft once. The provider may accept a message before its acknowledgement is lost. An uncertain send cannot be repeated."
                    : "Save this exact immutable draft in this conversation. Saving a draft sends no message. Sending requires a separate exact operation and current authority.", false)]);
        }
        else
        {
            foreach (var file in TenantApprovalRules.AccountRemoteSources(action))
                changes.Add(new("accountRemoteSource", null, JsonSerializer.Serialize(new { file.Id, file.Name, file.MimeType, file.ByteSize, file.Version, file.Md5Checksum, file.Parents }), false));
            if (action.UploadSource is { } source)
                changes.AddRange([new("accountSourcePath", null, source.Path, false), new("accountSourceBytes", null, source.ByteSize, false), new("accountSourceChecksum", null, source.Sha256Hex, false)]);
            if (action.DestinationObservationHash is not null)
                changes.Add(new("accountActionDisclosure", null, "Download the exact original binary bytes into the saved new workspace path. Existing destinations are never overwritten. Source or destination changes require fresh review.", false));
            else changes.Add(new("accountActionDisclosure", null, "Attempt only this exact saved Drive action. Source, version, account or destination changes require fresh review. Earlier batch effects can remain after failure. Uncertain work cannot be repeated.", false));
        }
        changes.Add(new("accountActionLimit", null, "This consent grants only the exact primary operation. Current account use, source reads, reviewer authority and workspace access remain independently required. No role or future operation is granted.", false));
        return changes;
    }
}
