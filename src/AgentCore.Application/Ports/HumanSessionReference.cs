namespace AgentCore.Application.Ports;

// Identity references only. Each protected boundary resolves the original live human/session again.
public record HumanSessionReference(Guid AccountId, Guid AuthenticationSessionId, Guid PrincipalId);
