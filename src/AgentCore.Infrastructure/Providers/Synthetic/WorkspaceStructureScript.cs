using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Infrastructure.Providers.Synthetic;

// Exercises native tools and their ordinary approval boundary; never bypasses storage or policy.
internal static class WorkspaceStructureScript
{
    public const string Marker = "synthetic-workspace-filesystem:";
    public static IReadOnlyList<ModelGenerationEvent>? Generate(ModelRequest request)
    {
        var last = request.Messages.ToList().FindLastIndex(m => m.Role == ModelRole.User);
        if (last < 0 || !request.Messages[last].Text.StartsWith(Marker,StringComparison.Ordinal)) return null;
        var command = request.Messages[last].Text[Marker.Length..].Trim();
        var home = command.StartsWith("home",StringComparison.Ordinal);
        var root = home ? "/home" : "/working";
        var results = request.Messages.Skip(last+1).Where(m => m.Role == ModelRole.Tool).ToArray();
        if (results.Length==0) return Call(request,ToolCatalog.WorkspaceList,JsonSerializer.Serialize(new {path=root}));
        if(results.Length==1)
        {
            using var list=JsonDocument.Parse(results[0].Text);
            var token=home && list.RootElement.TryGetProperty("treeSha256",out var hash) ? hash.GetString() : null;
            if(command.EndsWith("delete",StringComparison.Ordinal))
                return Call(request,ToolCatalog.WorkspaceDelete,JsonSerializer.Serialize(new {path=root+"/backup",recursive=true,expectedTreeSha256=token},Options));
            return Call(request,ToolCatalog.WorkspaceBatch,JsonSerializer.Serialize(new
            {
                operations=new WorkspaceStructuralOperation[] {
                    new("mkdir",Path:root+"/projects"),
                    new("move",Source:root+"/inbox",Destination:root+"/projects/project"),
                    new("copy",Source:root+"/projects/project",Destination:root+"/backup"),
                    new("mkdir",Path:root+"/backup/empty/deep")},
                expectedTreeSha256=token
            },Options));
        }
        return [new ModelTextDelta("Filesystem result: "+results[^1].Text),new ModelCompleted(ModelStopReason.Completed)];
    }
    private static readonly JsonSerializerOptions Options=new(JsonSerializerDefaults.Web) {DefaultIgnoreCondition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault};
    private static IReadOnlyList<ModelGenerationEvent> Call(ModelRequest request,string name,string args) => request.Tools?.Any(t=>t.Name==name)==true
        ? [new ModelToolCallEvent(new("filesystem-proof",name,args)),new ModelCompleted(ModelStopReason.ToolCalls)]
        : [new ModelTextDelta("Filesystem capability unavailable."),new ModelCompleted(ModelStopReason.Completed)];
}
