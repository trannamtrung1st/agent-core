using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Tools;

// Virtual paths select the existing Attachment owner; they never select a physical workspace.
public static class TenantAttachmentVirtualReadContract
{
    public static bool IsTool(string tool) => tool is ToolCatalog.WorkspaceRead or ToolCatalog.OfficeDocumentInspect or ToolCatalog.OfficeSpreadsheetInspect;
    public static bool IsPath(string path) => path == "/attachments" || TenantAttachmentArtifactRules.IsPath(path);
    public static bool AcceptsContentType(string tool, string contentType)
    {
        var type = AttachmentMedia.NormalizeContentType(contentType);
        return tool == ToolCatalog.WorkspaceRead ? AttachmentMedia.IsReadableText(type)
            : tool == ToolCatalog.OfficeDocumentInspect ? type == OfficeLimits.DocumentMime
            : tool == ToolCatalog.OfficeSpreadsheetInspect && type == OfficeLimits.SpreadsheetMime;
    }
    public static TenantAttachmentReadArguments Resolve(string tool, string arguments)
    {
        if (!IsTool(tool)) throw new ArgumentException("Use an exact Attachment file reader.");
        var args = TenantWorkspaceReadContract.Parse(tool, arguments);
        var id = TenantAttachmentArtifactRules.Id(args.Path);
        return tool == ToolCatalog.OfficeDocumentInspect ? new(id, Offset: args.Offset, Limit: args.Limit)
            : tool == ToolCatalog.OfficeSpreadsheetInspect ? new(id, args.Sheet, args.Range, args.Cursor) : new(id);
    }
    public static bool Matches(string tool, string arguments, PreparedTenantOperation operation) =>
        operation.AttachmentRead is { } read && IsTool(tool)
        && IsPath(TenantWorkspaceReadContract.Parse(tool, arguments).Path)
        && AcceptsContentType(tool, read.ContentType)
        && TenantAttachmentReadContract.Canonical(Resolve(tool, arguments)) == read.ArgumentsJson;
    public static string Description => " Exact bound /attachments/{canonical UUID} originals are also readable through this tool when attachments.read is admitted and currently enabled. These are read-only virtual sources, independent of workspace permissions or HomeAccess. workspace.read accepts UTF-8 text only; Office inspectors require the corresponding actual media type. Attachment reads require current independent Session and exact child Attachment reads. If attachment_read_review_required is returned, request inspect_workspace with this exact tool and arguments, or read_attachment with the equivalent attachmentId and read window. Both use the same exact Attachment consent and refusal history; changing tools cannot authorize a refused read.";
}
