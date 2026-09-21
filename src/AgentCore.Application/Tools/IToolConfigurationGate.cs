namespace AgentCore.Application.Tools;

public interface IToolConfigurationGate
{
    bool IsConfigured(string toolName);
}
