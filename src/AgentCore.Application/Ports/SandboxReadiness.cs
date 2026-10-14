namespace AgentCore.Application.Ports;

public interface ISandboxReadiness
{
    bool IsAvailable { get; }
    string State { get; }
    string SafeMessage { get; }
}
