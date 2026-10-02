using System.Text.Json;
using AgentCore.Domain.Work;

namespace AgentCore.Application.Tools;

public static class ToolActionHash
{
    public static string Compute(string toolName, JsonElement args) =>
        WorkActionHash.Compute(toolName, args);

    public static string Normalize(JsonElement element) =>
        WorkActionHash.Normalize(element);
}
