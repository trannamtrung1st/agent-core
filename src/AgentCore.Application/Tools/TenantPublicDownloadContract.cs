using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;

namespace AgentCore.Application.Tools;

public static class TenantPublicDownloadContract
{
    public const string Directory = "/home/downloads";
    public static TenantWorkspaceMutationArguments Parse(Guid session, JsonElement root)
    {
        TenantApprovalToolContract.Require(root, root.ValueKind == JsonValueKind.Object && root.TryGetProperty("path", out _) ? ["url", "path"] : ["url"]);
        if (!root.TryGetProperty("url", out var value) || value.ValueKind != JsonValueKind.String) throw new ArgumentException("Use one public HTTP(S) URL.");
        var url = Url(value.GetString()!);
        string? path = null;
        if (root.TryGetProperty("path", out value))
        {
            if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > 1024)
                throw new ArgumentException("Use an exact owned file destination.");
            path = value.GetString()!;
            if (!path.StartsWith("/home/", StringComparison.Ordinal) && !path.StartsWith("/working/", StringComparison.Ordinal)) throw new ArgumentException("Use an owned home or working file.");
            TenantWorkspaceRules.Path(new(Guid.Empty, Guid.Empty, path.StartsWith("/working/", StringComparison.Ordinal) ? session : null), path, file: true);
        }
        var canonical = path is null ? JsonSerializer.Serialize(new { url }) : JsonSerializer.Serialize(new { url, path });
        return new(ToolCatalog.WebDownload, path ?? Directory, null, [], [], canonical, TenantWorkspaceMutationContract.Hash(ToolCatalog.WebDownload + "\n" + canonical));
    }
    public static string Url(string value)
    {
        if (value.Length > 4096 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo) || string.IsNullOrEmpty(uri.Host) || uri.AbsoluteUri.Length > 4096) throw new ArgumentException("Use one public HTTP(S) URL without credentials.");
        return uri.AbsoluteUri;
    }
    public static (Uri Url, string? Path) Read(TenantWorkspaceMutationArguments args)
    {
        using var doc = JsonDocument.Parse(args.CanonicalArguments);
        return (new(Url(doc.RootElement.GetProperty("url").GetString()!)), doc.RootElement.TryGetProperty("path", out var path) ? path.GetString() : null);
    }
    public static string SafeName(string? suggested, Uri url)
    {
        var name = suggested;
        if (string.IsNullOrWhiteSpace(name)) name = Uri.UnescapeDataString(url.AbsolutePath.Split('/').LastOrDefault() ?? "");
        name = new string((name ?? "").Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_').Take(120).ToArray()).Trim('.');
        return string.IsNullOrWhiteSpace(name) ? "download.bin" : name;
    }
    public static ModelToolDefinition ForModel(ModelToolDefinition tool) => tool with { Description = tool.Description +
        " Current tenant execution uses exact workspace mutation authority and durable dispatch/receipts. Use as the sole call. If policy requires consent, request mutate_workspace with mutation.tool=web.download and these exact mutation.arguments. Omitted path creates a unique safe file in /home/downloads; explicit paths replace present bytes. A receipt grants no read or Artifact download authority. Unknown results must never be replayed." };
}
