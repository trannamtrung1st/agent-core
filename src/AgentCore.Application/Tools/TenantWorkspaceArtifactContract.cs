using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Tools;

public sealed record TenantWorkspaceArtifactArguments(string Path, string DisplayName, string? ContentType, string CanonicalArguments, string ParametersHash);

public static class TenantWorkspaceArtifactContract
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static ModelToolDefinition ForModel() => new(ToolCatalog.ArtifactsCreateFromWorkspace,
        "Publish one immutable copy of an exact existing owned /home, /private or /working file or admitted /agent/resources/..., /agent/instance/resources/... or /space/resources/... file or bound /attachments/{id} original in the current owned conversational or background task. Core derives the actual Agent, Session and source evidence. Copies raw bytes without interpreting Office packages, up to the configured file bound and 50 MiB ceiling; source files are unchanged. Requires current independent source reads, export capability and Allowed creation/source policy. HomeAccess applies to home files; resource files require its exact source resources.read; bound Attachment originals require exact attachments.read without a workspace permission or reread-tool capability. Review or missing authority creates no file. When offered, request publish_workspace_artifact through approvals.request_authorization with artifact containing these exact arguments as the sole call. Consent binds the saved source bytes and metadata; a changed source cannot be adapted or replayed. A refused operation must not be repeated in this task. After an applied publication, operationId is the Artifact identity. In a conversational response you may propose it as an artifact reference; background completion records the observed outcome and leaves the file in its owning Session; Core separately checks current Agent read authority and policy, then each recipient needs independent file access. A receipt or approval grants no read/download authority.",
        """{"type":"object","additionalProperties":false,"properties":{"path":{"type":"string","description":"Exact existing /home/ or /working/ file or admitted resource file path or bound /attachments/{canonical UUID} original; no traversal. Resource paths match literally.","minLength":7,"maxLength":4095},"displayName":{"type":"string","minLength":1,"maxLength":200},"contentType":{"type":"string","minLength":1,"maxLength":200}},"required":["path","displayName"]}""");
    public static TenantWorkspaceArtifactArguments Parse(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments) || Encoding.UTF8.GetByteCount(arguments) > TenantArtifactToolContract.MaxArgumentBytes)
            throw new ArgumentException("Artifact source arguments exceed the bound.");
        using var doc = JsonDocument.Parse(arguments); var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("Use exact Artifact source fields.");
        var properties = root.EnumerateObject().ToArray();
        if (properties.Length is < 2 or > 3 || properties.Any(p => p.Name is not ("path" or "displayName" or "contentType"))
            || properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length) throw new ArgumentException("Use exact Artifact source fields.");
        string Read(string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : throw new ArgumentException("Invalid Artifact source field.");
        var path = Read("path"); var name = Read("displayName"); var type = root.TryGetProperty("contentType", out _) ? Read("contentType") : null;
        if (TenantAttachmentArtifactRules.IsPath(path)) _ = TenantAttachmentArtifactRules.Id(path);
        else if (TenantWorkspaceReadContract.IsResourcePath(path)) TenantResourceSourceTarget.ValidatePath(path);
        else
        {
            if (!path.StartsWith("/home/", StringComparison.Ordinal) && !path.StartsWith("/private/", StringComparison.Ordinal) && !path.StartsWith("/working/", StringComparison.Ordinal)
                || path.IndexOfAny(['*', '?', '[', ']']) >= 0) throw new ArgumentException("Use an exact owned workspace file path.");
            try { TenantWorkspaceRules.ValidatePath(path, file: true); }
            catch (Sessions.AgentCoreException) { throw new ArgumentException("Use an exact owned workspace file path."); }
        }
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || type is not null && (string.IsNullOrWhiteSpace(type) || type.Length > 200
            || type.Any(char.IsControl) || !System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(type, out _))) throw new ArgumentException("Invalid Artifact source metadata.");
        name = WorkspaceFileNames.Sanitize(name);
        type = type?.Trim();
        var canonical = JsonSerializer.Serialize(new { path, displayName = name, contentType = type }, Json);
        return new(path, name, type, canonical, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ToolCatalog.ArtifactsCreateFromWorkspace + "\n" + canonical))));
    }
    public static bool Allows(AgentDefinition definition) => RolePermissions.AllowsTool(definition, ToolCatalog.ArtifactsCreateFromWorkspace);
}
