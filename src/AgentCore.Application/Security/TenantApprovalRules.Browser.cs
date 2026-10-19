using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record BrowserReviewTarget(BrowserProfileMode ProfileMode, Guid? ContextId, string? TabRef, int? PageGeneration,
    Guid? DialogId, string? LocationHash, long CommandRevision, string? Origin);

public static partial class TenantApprovalRules
{
    public static (BrowserCommand Command, string Arguments, bool ReadOnly) BrowserIntent(Guid session, string tool, string json)
    {
        if (session == Guid.Empty || Encoding.UTF8.GetByteCount(json) > 16384) throw new ArgumentException("Invalid bounded Browser intent.");
        using var doc = BrowserJson(json, 32);
        if (!Unique(doc.RootElement) || !BrowserToolArguments.TryRequest(session, tool, doc.RootElement, out var request, out _))
            throw new ArgumentException("Use the exact supported Browser command schema.");
        var command = request.Command;
        // These require their own canonical resource/credential/attached-input adapters.
        if (command is BrowserScreenshot or BrowserMouse or BrowserSetGeolocation
            || command is BrowserDrop { ArtifactId: not null }) throw new AccessDeniedException("browser_resource_adapter_unavailable");
        if (command is BrowserFillCredential password && password.CredentialRef != AgentCore.Domain.Credentials.CredentialRules.Reference(password.CredentialRef))
            throw new ArgumentException("Use the canonical bound Credential reference.");
        // The legacy tool effect classification treats navigation as ReadOnly. Tenant IAM must
        // reserve navigation, closure, storage and context changes for sensitive browser.use.
        if (command is BrowserUploadCommand upload) _ = TenantBrowserUploadRules.Ids(upload);
        var read = TenantBrowserInspectionContract.ReadOnly(command);
        return (command, ToolActionHash.Normalize(doc.RootElement), read);
        static bool Unique(JsonElement value) => value.ValueKind switch {
            JsonValueKind.Object => value.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == value.EnumerateObject().Count()
                && value.EnumerateObject().All(p => Unique(p.Value)),
            JsonValueKind.Array => value.EnumerateArray().All(Unique), _ => true };
    }
    public static string BrowserTarget(TenantBrowserCommandWitness witness, string? origin) => JsonSerializer.Serialize(new BrowserReviewTarget(
        witness.ProfileMode, witness.ContextId, witness.TabRef, witness.PageGeneration, witness.DialogId, witness.LocationHash,
        witness.ContextId is null ? 0 : witness.CommandRevision, origin));
    public static BrowserReviewTarget BrowserTarget(PreparedBrowserCommand value)
    {
        if (value.TargetJson.Length > 4096) throw new ArgumentException("Invalid Browser target.");
        using var doc = BrowserJson(value.TargetJson, 16);
        BrowserReviewTarget target;
        try { target = doc.RootElement.Deserialize<BrowserReviewTarget>() ?? throw new ArgumentException("Missing Browser target."); }
        catch (JsonException e) { throw new ArgumentException("Use the exact Browser target shape.", e); }
        if (!Enum.IsDefined(target.ProfileMode) || target.ContextId == Guid.Empty || target.DialogId == Guid.Empty || target.CommandRevision < 0
            || target.PageGeneration is < 0 || target.ContextId is null && (target.TabRef is not null || target.PageGeneration is not null || target.DialogId is not null || target.LocationHash is not null || target.CommandRevision != 0 || target.Origin is not null)
            || target.TabRef is { } tab && (!tab.StartsWith("pg_", StringComparison.Ordinal) || tab.Length > 128)
            || target.LocationHash is { } hash && (hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigitLower(c)))
            || target.Origin is { } origin && !BrowserOrigin(origin) || JsonSerializer.Serialize(target) != value.TargetJson)
            throw new ArgumentException("Use an exact canonical Browser review target.");
        return target;
    }
    private static JsonDocument BrowserJson(string json, int depth)
    {
        try { return JsonDocument.Parse(json, new() { MaxDepth = depth }); }
        catch (JsonException e) { throw new ArgumentException("Use bounded valid Browser JSON.", e); }
    }
    public static bool BrowserOrigin(string origin) => origin == "about:blank" || origin.Length <= 2048
        && Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0
        && uri.GetLeftPart(UriPartial.Authority) == origin;
    // Display every effective parameter (including defaults) as escaped plain text, never raw action JSON.
    public static string BrowserDescription(PreparedBrowserCommand value)
    {
        var command = BrowserIntent(value.SessionId, value.ToolName, value.ArgumentsJson).Command;
        var effective = JsonSerializer.SerializeToElement(command, command.GetType(), BrowserToolArguments.JsonOptions);
        var lines = new List<string>();
        void Describe(JsonElement item, string path)
        {
            if (item.ValueKind == JsonValueKind.Object)
                foreach (var property in item.EnumerateObject().Where(p => p.Name != "kind"))
                    Describe(property.Value, path.Length == 0 ? Label(property.Name) : path + " / " + Label(property.Name));
            else if (item.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var entry in item.EnumerateArray()) Describe(entry, path + " " + (++index));
                if (index == 0) lines.Add(path + ": None");
            }
            else lines.Add(path + ": " + (item.ValueKind switch { JsonValueKind.True => "Yes", JsonValueKind.False => "No",
                JsonValueKind.Null => "Not set", JsonValueKind.String => item.GetString(), _ => item.GetRawText() }));
        }
        Describe(effective, "");
        if (value.Credential is { } credential)
        {
            lines.Add("Credential: " + credential.DisplayName);
            lines.Add("Credential origin: " + credential.Origin);
            lines.Add("Protected field fill only; sign-in is not confirmed.");
        }
        foreach (var source in TenantBrowserUploadRules.Sources(value))
            lines.Add($"File: {source.DisplayName} ({source.ContentType}, {source.ByteSize} bytes)\nArtifact: {source.ArtifactId:D}\nSHA-256: {source.Sha256Hex}");
        if (command is BrowserUploadCommand) lines.Add("File input origin: " + value.FileInputOrigin + "\nSet the selected file input only; server submission is not confirmed.");
        return lines.Count == 0 ? "No additional parameters" : string.Join("\n", lines);
        static string Label(string key) => key switch { "url" => "URL", "by" => "Match by", "frameRef" => "Frame", "tabRef" => "Tab",
            "within" => "Within", "cwd" => "Working directory", "mimeType" => "Media type", _ => string.Concat(key.Select((c, i) =>
                i == 0 ? char.ToUpperInvariant(c).ToString() : char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : c.ToString())) };
    }
    public static PreparedTenantOperation PrepareBrowser(Guid operationId, Guid instance, PreparedBrowserCommand value, long instanceRevision, long personaRevision)
    {
        if (operationId == Guid.Empty || instance == Guid.Empty || instanceRevision < 1 || personaRevision < 1) throw new ArgumentException("Invalid Browser operation.");
        var parsed = BrowserIntent(value.SessionId, value.ToolName, value.ArgumentsJson); _ = BrowserTarget(value);
        if (parsed.Arguments != value.ArgumentsJson || parsed.ReadOnly != value.ReadOnly || BrowserCommandIdentity.Hash(parsed.Command) != value.CommandHash)
            throw new ArgumentException("Browser command parameters must be exact and canonical.");
        TenantBrowserCredentialRules.Shape(value, parsed.Command);
        _ = TenantBrowserUploadRules.Sources(value);
        if (parsed.Command is BrowserUploadCommand && (value.FileInputOrigin is null || value.FileInputOrigin == "about:blank" || !BrowserOrigin(value.FileInputOrigin)))
            throw new ArgumentException("Upload requires the exact file input origin.");
        var json = JsonSerializer.Serialize(new { operation = "browser.command", permission = value.Credential is not null ? "credentials.use" : value.ReadOnly ? "browser.read" : "browser.use",
            resourceType = value.Credential is not null ? "credential" : "agent", resourceId = (value.Credential?.CredentialId ?? instance).ToString("D"),
            expectedInstanceRevision = instanceRevision, expectedPersonaRevision = personaRevision, browserCommand = value });
        return new(operationId, instance, null, instanceRevision, personaRevision, json,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))), BrowserCommand: value);
    }
}
