using AgentCore.Application.Ports;

namespace AgentCore.Application.Tools;

public static class IamToolContracts
{
    public const int MaxArgumentBytes = 4096;
    public static bool IsInspection(string name) => name is ToolCatalog.IamCheckAccess or ToolCatalog.IamEffectivePermissions;
    public static ToolDescriptor Descriptor(string name)
    {
        if (!IsInspection(name)) throw new ArgumentException("Unknown IAM inspection tool.");
        var permission = name == ToolCatalog.IamCheckAccess ? "\"permission\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":128}," : "";
        var required = name == ToolCatalog.IamCheckAccess ? "\"permission\"," : "";
        var schema = "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{" + permission
            + "\"resourceType\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":32},\"resourceId\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":200}},\"required\":["
            + required + "\"resourceType\",\"resourceId\"]}";
        return new(new ModelToolDefinition(name,
            name == ToolCatalog.IamCheckAccess ? "Inspect the current executing agent's exact access and review requirement at one owned resource. Identity and policy come from the saved Run. This grants no permission and performs no action or approval request."
                : "Inspect only the current executing agent's allowed or reviewable operations at one owned resource. No other principal, member roles, secret values or authority snapshots are returned. Results are not permission grants.", schema),
            ToolEffect.ReadOnly, ToolOfferRule.TenantExecutionAuthority, ToolResourceScope.Session);
    }
}
