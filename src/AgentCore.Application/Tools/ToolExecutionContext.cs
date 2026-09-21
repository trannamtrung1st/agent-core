using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public sealed record ToolExecutionContext(
    Guid SessionId,
    Guid ResponseId,
    Guid OperationId,
    AgentDefinition Agent,
    ModelCapabilities ModelCapabilities);
