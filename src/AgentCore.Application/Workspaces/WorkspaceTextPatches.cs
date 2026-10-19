using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Workspaces;

public static class WorkspaceTextPatches
{
    public static byte[] Apply(byte[] bytes, IReadOnlyList<WorkspaceTextEdit> edits, long maxBytes = AgentWorkspaceLimits.MaxFileBytes)
    {
        if (edits.Count is < 1 or > WorkspaceLimits.MaxPatchEdits || edits.Any(e => e is null || string.IsNullOrEmpty(e.OldText) || e.NewText is null))
            throw AgentCoreErrors.Validation("Patch requires 1–32 edits with non-empty oldText.");
        if (bytes.LongLength > maxBytes) throw AgentCoreErrors.WorkspaceQuotaExceeded();
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { throw AgentCoreErrors.Validation("Workspace file must be strict UTF-8 text."); }
        foreach (var edit in edits)
        {
            var first = text.IndexOf(edit.OldText, StringComparison.Ordinal);
            if (first < 0 || text.IndexOf(edit.OldText, first + 1, StringComparison.Ordinal) >= 0)
                throw AgentCoreErrors.Conflict("Each edit oldText must match exactly once in the current file content.");
            if ((long)Encoding.UTF8.GetByteCount(text) - Encoding.UTF8.GetByteCount(edit.OldText) + Encoding.UTF8.GetByteCount(edit.NewText) > maxBytes)
                throw AgentCoreErrors.WorkspaceQuotaExceeded();
            text = string.Concat(text.AsSpan(0, first), edit.NewText.AsSpan(), text.AsSpan(first + edit.OldText.Length));
        }
        return Encoding.UTF8.GetBytes(text);
    }
}
