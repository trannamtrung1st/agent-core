using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;

namespace AgentCore.Application.Tools;

public static class WorkspaceStructureArguments
{
    public static (IReadOnlyList<WorkspaceStructuralOperation> Operations, string? TreeSha256) Parse(string name, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || System.Text.Encoding.UTF8.GetByteCount(args.GetRawText()) > WorkspaceStructureLimits.MaxRequestBytes)
            throw AgentCoreErrors.Validation("Filesystem arguments require a bounded object (16 KiB).");
        var batch = name == ToolCatalog.WorkspaceBatch;
        Keys(args, batch ? ["operations", "expectedTreeSha256"] : ["path", "source", "destination", "recursive", "expectedTreeSha256"]);
        string? token = Text(args,"expectedTreeSha256");
        if (token is not null && (token.Length != 64 || token.Any(c => !char.IsAsciiHexDigitLower(c))))
            throw AgentCoreErrors.Validation("expectedTreeSha256 requires a lowercase SHA-256 from workspace.list.");
        if (!batch) return ([Read(args,name["workspace.".Length..],false)], token);
        if (!args.TryGetProperty("operations",out var operations) || operations.ValueKind != JsonValueKind.Array || operations.GetArrayLength() is < 1 or > WorkspaceStructureLimits.MaxOperations)
            throw AgentCoreErrors.Validation("A batch requires 1–16 operations.");
        return (operations.EnumerateArray().Select(e => Read(e,null,true)).ToArray(),token);
    }
    private static WorkspaceStructuralOperation Read(JsonElement args,string? op,bool nested)
    {
        Keys(args,nested ? ["op","path","source","destination","recursive"] : ["path","source","destination","recursive","expectedTreeSha256"]);
        op ??= Text(args,"op");
        if (op is not ("mkdir" or "copy" or "move" or "delete")) throw AgentCoreErrors.Validation("Only mkdir/copy/move/delete are structural operations.");
        var path=Text(args,"path"); var source=Text(args,"source"); var destination=Text(args,"destination"); bool recursive=false;
        if(args.TryGetProperty("recursive",out var r)) {if(r.ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw AgentCoreErrors.Validation("recursive must be boolean."); recursive=r.GetBoolean();}
        if(op is "mkdir" or "delete")
        {if(path is null || source is not null || destination is not null || op == "mkdir" && args.TryGetProperty("recursive",out _))throw AgentCoreErrors.Validation("mkdir/delete require path; only delete accepts recursive.");}
        else if(source is null || destination is null || path is not null || args.TryGetProperty("recursive",out _))throw AgentCoreErrors.Validation("copy/move require only source and destination.");
        return new(op,path,source,destination,recursive);
    }
    private static string? Text(JsonElement args,string key)
    {if(!args.TryGetProperty(key,out var value))return null;if(value.ValueKind!=JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))throw AgentCoreErrors.Validation("Filesystem path/token fields require non-empty strings.");return value.GetString();}
    private static void Keys(JsonElement args,string[] allowed)
    {if(args.ValueKind!=JsonValueKind.Object)throw AgentCoreErrors.Validation("Filesystem operation requires an object.");var seen=new HashSet<string>(StringComparer.Ordinal);foreach(var p in args.EnumerateObject())if(!allowed.Contains(p.Name) || !seen.Add(p.Name))throw AgentCoreErrors.Validation("Unknown or duplicate filesystem argument.");}
}
