using System.Text.Json;

namespace AgentCore.Domain.Events;

public static class BuiltInEventDefinitions
{
    public static string ExampleData(string key, Guid instanceId) => JsonSerializer.Serialize(key switch
    {
        "run.completed" => (object)new { agentRunId = Guid.Parse("00000000-0000-4000-8000-000000000001"), sessionId = Guid.Parse("00000000-0000-4000-8000-000000000002"), activationKind = "UserTurn", outcomeKind = "Response" },
        "run.failed" => new { agentRunId = Guid.Parse("00000000-0000-4000-8000-000000000001"), sessionId = Guid.Parse("00000000-0000-4000-8000-000000000002"), activationKind = "UserTurn", failureCode = "provider-unavailable" },
        "session.completed" => new { sessionId = Guid.Parse("00000000-0000-4000-8000-000000000002"), previousLifecycle = "Active", lifecycle = "Completed" },
        "session.ended" => new { sessionId = Guid.Parse("00000000-0000-4000-8000-000000000002"), previousLifecycle = "Active", lifecycle = "Ended" },
        "instance.config_changed" => new { agentInstanceId = instanceId, revision = 2, changedSections = new[] { "persona" }, actor = "guardedWrite" },
        _ => new { agentInstanceId = instanceId, definitionId = "secretary", previousVersion = 8, activeVersion = 9 }
    });
    public static string Description(string key) => key switch
    {
        "run.completed" => "An owned Run committed a completed result.",
        "run.failed" => "An owned Run committed a terminal failure.",
        "session.completed" => "An owned Session was completed.",
        "session.ended" => "An owned Session was ended.",
        "instance.config_changed" => "A guarded write changed effective Instance configuration.",
        "harness.definition_adopted" => "An Instance adopted a Definition version.",
        _ => throw new ArgumentException("Built-in Event is unavailable.")
    };
}
