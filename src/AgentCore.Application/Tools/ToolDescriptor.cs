using AgentCore.Application.Ports;

namespace AgentCore.Application.Tools;

public sealed record ToolDescriptor(
    ModelToolDefinition ModelDefinition,
    ToolEffect Effect,
    ToolOfferRule OfferRule = ToolOfferRule.RoleAllowlist)
{
    public string Name => ModelDefinition.Name;
}
