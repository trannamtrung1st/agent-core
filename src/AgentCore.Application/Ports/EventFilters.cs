using System.Text.Json;
using AgentCore.Domain.Events;

namespace AgentCore.Application.Ports;

public interface IEventFilterEvaluator
{
    string? Validate(string? expression);
    EventFilterResult Evaluate(string? expression, JsonElement envelope, CancellationToken cancellationToken = default);
}
