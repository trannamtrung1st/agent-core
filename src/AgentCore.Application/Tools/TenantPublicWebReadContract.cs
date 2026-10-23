using System.Text;
using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public sealed record TenantPublicWebReadArguments(string Tool, string? Query, int Limit, Uri? Url, string CanonicalArguments)
{
    public string Hash => TenantWorkspaceMutationContract.Hash(Tool + "\n" + CanonicalArguments);
}

// Public observations have no private source grant. Their executing Agent/Session and
// current capability/configuration still require the canonical tenant boundary.
public static class TenantPublicWebReadContract
{
    public static bool IsRead(string tool) => tool is ToolCatalog.WebSearch or ToolCatalog.WebFetch;
    public static bool Allows(AgentDefinition definition, string tool) => IsRead(tool) && RolePermissions.AllowsTool(definition, tool);
    public static TenantPublicWebReadArguments Parse(string tool, string json)
    {
        if (!IsRead(tool) || string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > 32768) throw new ArgumentException("Use bounded public web arguments.");
        using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
        if (tool == ToolCatalog.WebFetch)
        {
            TenantApprovalToolContract.Require(root, ["url"]);
            var url = PublicUrl(root.GetProperty("url").ValueKind == JsonValueKind.String ? root.GetProperty("url").GetString()! : "");
            return new(tool, null, 0, url, JsonSerializer.Serialize(new { url = url.AbsoluteUri }));
        }
        var withLimit = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("limit", out _);
        TenantApprovalToolContract.Require(root, withLimit ? ["query", "limit"] : ["query"]);
        var value = root.GetProperty("query");
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > 600) throw new ArgumentException("Use a query of 1–600 characters.");
        var query = value.GetString()!.Trim(); var limit = 5;
        if (withLimit && (root.GetProperty("limit").ValueKind != JsonValueKind.Number || !root.GetProperty("limit").TryGetInt32(out limit) || limit is < 1 or > 10)) throw new ArgumentException("Use a result limit of 1–10.");
        return new(tool, query, limit, null, JsonSerializer.Serialize(new { query, limit }));
    }
    public static Uri PublicUrl(string text)
    {
        var normalized = TenantPublicDownloadContract.Url(text);
        var uri = new Uri(normalized);
        if (!PublicNetworkPolicy.IsAllowedHostName(uri.Host)) throw new ArgumentException("Use a public HTTP(S) host.");
        return uri;
    }
    public static ModelToolDefinition ForModel(ModelToolDefinition tool) => tool with {
        Description = tool.Description + " Current tenant execution checks independent Agent and Session read authority, current capability and configuration. Public observations grant no private resource, browser, Credential or mutation authority.",
        ParametersJson = tool.Name == ToolCatalog.WebSearch
            ? """{"type":"object","additionalProperties":false,"properties":{"query":{"type":"string","minLength":1,"maxLength":600},"limit":{"type":"integer","minimum":1,"maximum":10}},"required":["query"]}"""
            : """{"type":"object","additionalProperties":false,"properties":{"url":{"type":"string","minLength":1,"maxLength":4096}},"required":["url"]}"""
    };
}
