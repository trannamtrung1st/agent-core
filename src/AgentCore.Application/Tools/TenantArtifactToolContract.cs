using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Agents;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public sealed record TenantArtifactArguments(string DisplayName, string ContentType, string Content, string CanonicalArguments, string ParametersHash);

public static class TenantArtifactToolContract
{
    public const string Permission = "artifacts.create";
    public const int MaxArgumentBytes = 65_536;
    public const int MaxContentBytes = 32_768;
    private static readonly JsonSerializerOptions CanonicalJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static ModelToolDefinition ForModel() => new(ToolCatalog.ArtifactsCreate,
        "Publish one immutable file, up to 32 KiB UTF-8 content and 64 KiB JSON arguments, in the current human-message Run under independent Agent IAM and policy. Core commits one durable result per Run/call. Requires Allowed creation authority on this Session and independent Session read; Review or missing authority produces no file. When offered, request publish_artifact through approvals.request_authorization with artifact containing these exact arguments as the sole call. A refused operation must not be repeated in this task. This result is an effect receipt, not Artifact read/download authority. Workspace export and rich references are separate.",
        """{"type":"object","additionalProperties":false,"properties":{"displayName":{"type":"string","minLength":1,"maxLength":200},"contentType":{"type":"string","minLength":1,"maxLength":200},"content":{"type":"string","maxLength":32768}},"required":["displayName","content"]}""");
    public static TenantArtifactArguments Parse(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments) || Encoding.UTF8.GetByteCount(arguments) > MaxArgumentBytes) throw new ArgumentException("Artifact arguments exceed the bound.");
        using var doc = JsonDocument.Parse(arguments); var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("Use exact Artifact fields.");
        var properties = root.EnumerateObject().ToArray();
        if (properties.Length is < 2 or > 3 || properties.Any(p => p.Name is not ("displayName" or "contentType" or "content"))
            || properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length) throw new ArgumentException("Use exact Artifact fields.");
        string Read(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : throw new ArgumentException("Invalid Artifact field.");
        var name = Read("displayName"); var content = Read("content"); var type = root.TryGetProperty("contentType", out _) ? Read("contentType") : "text/markdown";
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || Encoding.UTF8.GetByteCount(content) > MaxContentBytes
            || string.IsNullOrWhiteSpace(type) || type.Length > 200 || type.Any(char.IsControl)
            || !System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(type, out _)) throw new ArgumentException("Invalid Artifact content.");
        name = WorkspaceFileNames.Sanitize(name); type = type.Trim();
        var json = JsonSerializer.Serialize(new { displayName = name, contentType = type, content }, CanonicalJson);
        return new(name, type, content, json, Hash(ToolCatalog.ArtifactsCreate + "\n" + json));
    }
    public static bool Allows(AgentDefinition definition) => RolePermissions.AllowsTool(definition, ToolCatalog.ArtifactsCreate);
    public static string ActionHash(Guid session, string parametersHash) => Hash("artifacts.create\n" + session.ToString("D") + "\n" + parametersHash);
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
