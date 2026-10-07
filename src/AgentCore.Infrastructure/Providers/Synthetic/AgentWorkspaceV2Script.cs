using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Infrastructure.Providers.Synthetic;

internal static class AgentWorkspaceV2Script
{
    public const string Marker = "synthetic-agent-workspace-v2:";
    public static IReadOnlyList<ModelGenerationEvent>? Generate(ModelRequest request)
    {
        var last = request.Messages.ToList().FindLastIndex(m => m.Role == ModelRole.User);
        if (last < 0 || !request.Messages[last].Text.StartsWith(Marker, StringComparison.Ordinal)) return null;
        var command = request.Messages[last].Text[Marker.Length..].Trim();
        var results = request.Messages.Skip(last + 1).Where(m => m.Role == ModelRole.Tool).ToArray();
        string? name = null; object? args = null;
        if (command == "project")
        {
            (string? Name, object? Args) step = results.Length switch
            {
                0 => (ToolCatalog.WorkspaceCwd, new { operation = "get" }),
                1 => (ToolCatalog.WorkspaceMkdir, new { path = "/working/c#/CsvTool/empty" }),
                2 => (ToolCatalog.WorkspaceWrite, new { path = "/working/c#/CsvTool/CsvTool.csproj", content = "<Project Sdk=\"Microsoft.NET.Sdk\"/>\r\n" }),
                3 => (ToolCatalog.WorkspaceWrite, new { path = "/working/c#/CsvTool/CsvUtil.cs", content = "// Csv utility café\r\n" }),
                4 => (ToolCatalog.WorkspaceWrite, new { path = "/working/c#/CsvTool/Program.cs", content = "Console.WriteLine(\"CSV\");\n" }),
                5 => (ToolCatalog.WorkspaceWrite, new { path = "/working/c#/CsvTool/README.md", content = "# CsvTool\r\nExact project bytes.\n" }),
                6 => (ToolCatalog.WorkspaceCopy, new { source = "/working/c#", destination = "/home/c#" }),
                7 => (ToolCatalog.WorkspaceList, new { path = "/home" }),
                8 => (ToolCatalog.WorkspaceMove, new { source = "/home/c#", destination = "/home/csharp", expectedTreeSha256 = Token(results[7].Text) }),
                _ => (null, null)
            };
            (name, args) = step;
        }
        if (command == "project-edit")
        {
            (string? Name, object? Args) step = results.Length switch
            {
                0 => (ToolCatalog.WorkspaceCwd, new { operation = "get" }),
                1 => (ToolCatalog.WorkspaceRead, new { path = "csharp/CsvTool/README.md" }),
                2 => (ToolCatalog.WorkspacePatch, new { path = "csharp/CsvTool/README.md", expectedSha256 = HomeHash(results[1].Text), edits = new[] { new { oldText = "Exact project bytes.", newText = "Verified durable project." } } }),
                3 => (ToolCatalog.ArtifactsCreateFromWorkspace, new { path = "csharp/CsvTool/README.md", displayName = "README.md" }),
                _ => (null, null)
            };
            (name, args) = step;
        }
        if (command == "cwd" && results.Length == 0) { name = ToolCatalog.WorkspaceCwd; args = new { operation = "get" }; }
        if (command == "restore")
        {
            if (results.Length == 0) { name = ToolCatalog.WorkspaceCwd; args = new { operation = "get" }; }
            if (results.Length == 1) { name = ToolCatalog.WorkspaceRead; args = new { path = "projects/customer-a/result.md" }; }
            if (results.Length == 2) { name = ToolCatalog.ArtifactsCreateFromWorkspace; args = new { path = "projects/customer-a/result.md", displayName = "result.md" }; }
        }
        if (command == "setup")
        {
            (string? Name, object? Args) step = results.Length switch
            {
                0 => (ToolCatalog.WorkspaceCwd, new { operation = "get" }),
                1 => (ToolCatalog.WorkspaceList, new { path = "/home" }),
                2 => (ToolCatalog.WorkspaceMkdir, new { path = "projects/customer-a", expectedTreeSha256 = Token(results[1].Text) }),
                3 => (ToolCatalog.WorkspaceCwd, new { operation = "set", path = "/home/projects/customer-a" }),
                4 => (ToolCatalog.WorkspaceWrite, new { path = "draft.md", content = "Workspace v2 exact café\r\n" }),
                5 => (ToolCatalog.WorkspaceCopy, new { source = "draft.md", destination = "/working/draft.md" }),
                6 => (ToolCatalog.WorkspaceCwd, new { operation = "set", path = "/working" }),
                7 => (ToolCatalog.WorkspaceRead, new { path = "draft.md" }),
                8 => (ToolCatalog.WorkspaceWrite, new { path = "result.md", content = "Durable result café\r\n" }),
                9 => (ToolCatalog.WorkspaceCopy, new { source = "result.md", destination = "/home/projects/customer-a/result.md" }),
                10 => (ToolCatalog.ArtifactsCreateFromWorkspace, new { path = "/home/projects/customer-a/result.md", displayName = "result.md" }),
                _ => (null, null)
            };
            (name, args) = step;
        }
        if (name is null)
        {
            var text = "Agent Workspace v2 results:\n" + string.Join("\n", results.Select(r => r.Text));
            if (results.Length > 0)
            {
                using var json = JsonDocument.Parse(results[^1].Text);
                if (json.RootElement.TryGetProperty("artifactId", out var artifact))
                    text += $"\n[[artifact:{artifact.GetString()}]]";
            }
            return [new ModelTextDelta(text), new ModelCompleted(ModelStopReason.Completed)];
        }
        if (request.Tools?.Any(t => t.Name == name) != true)
            return [new ModelTextDelta("Agent Workspace v2 capability unavailable: " + name), new ModelCompleted(ModelStopReason.Completed)];
        return [new ModelToolCallEvent(new("workspace-v2-proof", name, JsonSerializer.Serialize(args))), new ModelCompleted(ModelStopReason.ToolCalls)];
    }
    private static string? Token(string json) { using var doc = JsonDocument.Parse(json); return doc.RootElement.TryGetProperty("treeSha256", out var token) ? token.GetString() : null; }
    private static string HomeHash(string json) { using var doc = JsonDocument.Parse(json); return doc.RootElement.GetProperty("homeItem").GetProperty("Sha256Hex").GetString()!; }
}
