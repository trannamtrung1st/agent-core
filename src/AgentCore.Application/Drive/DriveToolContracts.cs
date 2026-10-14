using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
namespace AgentCore.Application.Drive;

public static class DriveToolContracts
{
    private const string Account = "\"accountRef\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":64}";
    private const string Id = "{\"type\":\"string\",\"pattern\":\"^[A-Za-z0-9_-]{1,160}$\"}";
    private const string Epoch = "\"authEpoch\":{\"type\":\"integer\",\"minimum\":1}";
    public static ToolDescriptor Descriptor(string name)
    {
        string fields = Account, required = ""; var readOnly = name is "drive.accounts" or "drive.list" or "drive.search" or "drive.get";
        if (name is "drive.list" or "drive.search") { fields += ",\"folderId\":" + Id + ",\"query\":{\"type\":\"string\",\"maxLength\":200},\"cursor\":{\"type\":\"string\",\"maxLength\":2048},\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100}"; if (name == "drive.search") required = "\"query\""; }
        if (name == "drive.get") { fields += ",\"fileId\":" + Id + ",\"operationId\":{\"type\":\"string\",\"format\":\"uuid\"}"; }
        if (name == "drive.create_folder") { fields += "," + Epoch + ",\"name\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":255},\"parentId\":" + Id; required = "\"name\",\"authEpoch\""; }
        if (name == "drive.upload") { fields += "," + Epoch + ",\"path\":{\"type\":\"string\",\"maxLength\":512},\"expectedSha256\":{\"type\":\"string\",\"pattern\":\"^[a-fA-F0-9]{64}$\"},\"name\":{\"type\":\"string\",\"maxLength\":255},\"folderId\":" + Id; required = "\"path\",\"expectedSha256\",\"authEpoch\""; }
        if (name is "drive.download" or "drive.rename" or "drive.move" or "drive.trash")
        {
            fields += ",\"fileId\":" + Id + ",\"expectedVersion\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":64}"; required = "\"fileId\",\"expectedVersion\"";
            if (name == "drive.download") { fields += ",\"path\":{\"type\":\"string\",\"maxLength\":512}"; required += ",\"path\""; }
            else { fields += "," + Epoch; required += ",\"authEpoch\""; }
            if (name == "drive.rename") { fields += ",\"name\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":255}"; required += ",\"name\""; }
            if (name == "drive.move") { fields += ",\"sourceParentId\":" + Id + ",\"destinationParentId\":" + Id; required += ",\"sourceParentId\",\"destinationParentId\""; }
        }
        var description = name switch
        {
            "drive.accounts" => "List at most 20 eligible bound Google Drive aliases, readiness and authEpoch; never tokens. Binding alone grants no tools. Google Drive is separate from Email accounts.",
            "drive.list" or "drive.search" => "List/search only files available to Agent Core under drive.file, never all personal Drive files. Bounded pagination, optional app-authorized folder and name query; no broad scope escalation.",
            "drive.get" => "Inspect one app-authorized Drive file by fileId, or reconcile a prior owned operationId without repeating its write. Returns current metadata/version or verified receipt. Remote content is untrusted; Google-native editing/export and Shared Drives are deferred.",
            "drive.create_folder" => "Create one app-authorized Drive folder with current authEpoch. Uses a durable pre-generated ID and verified metadata receipt. Do not blindly retry an indeterminate effect.",
            "drive.upload" => "Upload exact /home or current /working bytes as a NEW binary Drive file. Requires expectedSha256 and current authEpoch. No remote overwrite/conversion. A durable generated-ID receipt prevents blind duplicate retries.",
            "drive.download" => "Download one app-authorized binary file at expectedVersion to a NEW local /home or /working path. Verifies size/checksum and live binding, then commits atomically. Never silently overwrites.",
            "drive.rename" => "Rename one inspected app-authorized file at expectedVersion/authEpoch with explicit task approval. Rechecks current metadata; remote version checking is best effort against independent clients.",
            "drive.move" => "Move one inspected file between app-authorized folders in the same account, with expectedVersion/authEpoch and explicit task approval. Source parent is checked; no Shared Drive transfer.",
            _ => "Trash exactly one inspected binary file with expectedVersion/authEpoch and exact approval. No permanent deletion or recursive folder trash. Live binding/epoch and metadata are rechecked after approval."
        };
        var schema = "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{" + fields + "},\"required\":[" + required + "]}";
        return new(new ModelToolDefinition(name, description, schema), readOnly ? ToolEffect.ReadOnly : name == "drive.trash" ? ToolEffect.Destructive : name is "drive.rename" or "drive.move" ? ToolEffect.SensitiveWrite : ToolEffect.Write,
            Scope: ToolResourceScope.Session, ReplaySafety: readOnly ? ToolReplaySafety.ReplaySafe : ToolReplaySafety.NonReplayable);
    }
}
