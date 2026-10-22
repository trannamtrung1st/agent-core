using System.Text.Json;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public sealed record TenantManagementInspectionItem(string Kind, string Key, string Fingerprint, AccessScope Scope, JsonElement Metadata);
public sealed record TenantManagementInspectionCatalog(IReadOnlyList<TenantManagementInspectionItem> Items);
