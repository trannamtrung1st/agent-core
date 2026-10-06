using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Workspaces;

public static class WorkspaceTextPatches
{
    public static byte[] Apply(byte[] bytes, IReadOnlyList<WorkspaceTextEdit> edits)
    {
        if (edits.Count is < 1 or > WorkspaceLimits.MaxPatchEdits || edits.Any(e => string.IsNullOrEmpty(e.OldText)))
            throw AgentCoreErrors.Validation("Patch requires 1–32 edits with non-empty oldText.");
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { throw AgentCoreErrors.Validation("Workspace file must be strict UTF-8 text."); }
        foreach (var edit in edits)
        {
            var first = text.IndexOf(edit.OldText, StringComparison.Ordinal);
            if (first < 0 || text.IndexOf(edit.OldText, first + edit.OldText.Length, StringComparison.Ordinal) >= 0)
                throw AgentCoreErrors.Conflict("Each edit oldText must match exactly once in the current file content.");
            text = text.Replace(edit.OldText, edit.NewText, StringComparison.Ordinal);
        }
        return Encoding.UTF8.GetBytes(text);
    }
}
