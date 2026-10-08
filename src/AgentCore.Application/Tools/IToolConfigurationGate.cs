namespace AgentCore.Application.Tools;

public interface IToolConfigurationGate
{
    bool IsConfigured(string toolName);

    // Offering includes provider feature support. Execution retains authority checks but
    // lets an authorized forced call return the provider-neutral unsupported result.
    bool IsExecutionConfigured(string toolName) => IsConfigured(toolName);
}
