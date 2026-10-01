namespace AgentCore.Application.Agents;

/// <summary>
/// Core defaults for intermediate application messaging per execution.
/// Values are not Admin-configurable yet; keep policy-shaped for later execution settings.
/// </summary>
public sealed record ApplicationMessagePolicy(
    int MaxAdmittedPerExecution = 12,
    int MaxCharactersPerMessage = 2000,
    int MaxAggregateCharactersPerExecution = 8000)
{
    public static ApplicationMessagePolicy Default { get; } = new();
}
