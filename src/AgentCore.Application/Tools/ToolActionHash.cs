using System.Text.Json;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Tools;

public static class ToolActionHash
{
    public static string Compute(string toolName, JsonElement args) =>
        CanonicalToolActionHash.Compute(toolName, args);

    public static string Normalize(JsonElement element) =>
        CanonicalToolActionHash.Normalize(element);
}
