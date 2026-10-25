using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Models;

public static class ModelReferenceQueries
{
    public static void Validate(ModelReferenceQuery query)
    {
        if (query.TenantId == Guid.Empty || query.OfferingId == Guid.Empty || query.ConnectionId == Guid.Empty
            || (query.OfferingId is null) == (query.ConnectionId is null) || !Enum.IsDefined(query.Kind)
            || query.Page is < 0 or > 100_000 || query.PageSize is < 1 or > 100 || query.Visible is null)
            throw new ArgumentException("Invalid model reference query.");
    }
    public static string State(ModelReferenceKind kind, string value) => kind switch
    {
        ModelReferenceKind.Runs when Enum.TryParse<AgentRunStatus>(value, out var run) && Enum.IsDefined(run) => run.ToString(),
        ModelReferenceKind.Automations when Enum.TryParse<AutomationStatus>(value, out var automation) && Enum.IsDefined(automation) => automation.ToString(),
        _ => value
    };
}
