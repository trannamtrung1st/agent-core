using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Security;

// Server-emitted observation provenance. A bare ID grants no authority.
public static class TenantCompletionReadReferences
{
    public static IReadOnlyList<(Guid Session, Guid Source, Guid Request)> Read(AgentRun parent)
    {
        if (!AgentRunToolCallCheckpoint.TryRead(parent.Checkpoint, out var messages)) return [];
        var values = new HashSet<(Guid, Guid, Guid)>();
        foreach (var message in messages!.Where(m => m.Role == ModelRole.Tool && (TenantCompletionToolContract.IsTool(m.Name ?? "")
            || m.Name is ToolCatalog.RequestAuthorization or ToolCatalog.ExecutionWait)))
        {
            using var json = JsonDocument.Parse(message.Text); var root = json.RootElement;
            void Add(JsonElement value)
            {
                if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("readRequestId", out var id) || id.ValueKind != JsonValueKind.String) return;
                values.Add((value.GetProperty("backgroundSessionId").GetGuid(), value.GetProperty("sourceAgentRunId").GetGuid(), id.GetGuid()));
            }
            if (root.TryGetProperty("completion", out var completion) && completion.ValueKind == JsonValueKind.Object) Add(completion);
            else if (root.TryGetProperty("completions", out var list)) foreach (var item in list.EnumerateArray()) Add(item);
            else if (root.TryGetProperty("statuses", out var statuses)) foreach (var item in statuses.EnumerateArray()) Add(item);
            else Add(root);
        }
        return values.ToArray();
    }
    public static bool Contains(AgentRun parent, Guid session, Guid source, Guid request) => Read(parent).Contains((session, source, request));
}
