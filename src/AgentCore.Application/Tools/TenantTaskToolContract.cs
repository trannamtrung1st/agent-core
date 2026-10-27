using System.Text.Json;

namespace AgentCore.Application.Tools;

public static class TenantTaskToolContract
{
    public static IReadOnlyList<string> Names { get; } = ["tasks.delegate", "tasks.list", "tasks.inspect", "tasks.messages.list", "tasks.messages.send", "tasks.cancel", "tasks.acknowledge", "agent-messages.send", "agent-messages.list"];
    public static bool IsTool(string name) => Names.Contains(name, StringComparer.Ordinal);
    public static string Permission(string name) => name switch {
        "tasks.delegate" => "tasks.delegate", "tasks.cancel" => "tasks.cancel", "tasks.messages.send" or "agent-messages.send" => "agent-messages.send",
        "tasks.messages.list" or "agent-messages.list" => "agent-messages.read", _ => "tasks.read" };
    public static IEnumerable<ToolDescriptor> Descriptors() => Names.Select(name => new ToolDescriptor(
        new(name, name switch {
            "tasks.delegate" => "Assign one bounded objective to one eligible same-tenant Agent. Core binds actual source and independent worker identity/Space. Request keys identify immutable actions. Work executes through the existing Run coordinator; no credentials or source files are shared. Inbox-only by default.",
            "tasks.list" => "Read currently authorized created, assigned or pending Tasks. IDs are selectors, never authority.",
            "tasks.inspect" => "Inspect exact saved Task facts and separately authorized outcome sources. Task success does not verify a Goal.",
            "tasks.messages.list" => "Read bounded ordered untrusted correspondence on one authorized Task.",
            "tasks.messages.send" => "Send inert progress, question, answer or note as this real claimed Agent. This does not suspend work or start another Run.",
            "tasks.cancel" => "Request cancellation of one exact Task revision. Unknown external effects require attention; cancellation is not proof of rollback.",
            "tasks.acknowledge" => "Mark one saved completion notification handled. This does not verify, publish or accept the result.",
            "agent-messages.send" => "Send bounded untrusted evidence in an opted-in private exactly-two-Agent thread as this claimed Agent. Inbox delivery does not imply model execution.",
            _ => "Read private two-Agent thread metadata or ordered messages under current independent rights." }, Schema(name)),
        name is "tasks.list" or "tasks.inspect" or "tasks.messages.list" or "agent-messages.list" ? ToolEffect.ReadOnly : ToolEffect.Write,
        ToolOfferRule.RoleAllowlist, ToolResourceScope.Session, ToolReplaySafety.IntegrationIdempotent));
    private static string Schema(string name)
    {
        object Id() => new { type = "string", format = "uuid" };
        var properties = new Dictionary<string, object>(); var required = new List<string>();
        void Field(string key, object schema, bool needed = false) { properties[key] = schema; if (needed) required.Add(key); }
        if (name == "tasks.delegate")
        {
            Field("objective", new { type = "string", minLength = 1, maxLength = 4000 }, true); Field("title", new { type = "string", maxLength = 80 });
            Field("target", new { oneOf = new object[] {
                new { type = "object", additionalProperties = false, required = new[] { "instanceId" }, properties = new { instanceId = Id() } },
                new { type = "object", additionalProperties = false, required = new[] { "mode", "candidateInstanceIds" }, properties = new {
                    mode = new { type = "string", @enum = new[] { "eligible" } }, candidateInstanceIds = new { type = "array", uniqueItems = true, minItems = 1, maxItems = 100, items = Id() } } } } }, true);
            Field("spaceId", Id()); Field("environmentId", Id()); Field("dependsOnTaskIds", new { type = "array", uniqueItems = true, maxItems = 8, items = Id() });
            Field("retryOfTaskId", Id());
            Field("reportOnCompletion", new { type = "boolean", @default = false }); Field("deadlineAt", new { type = "string", format = "date-time" });
        }
        else if (name.StartsWith("tasks.", StringComparison.Ordinal) && name != "tasks.list") Field("taskId", Id(), true);
        if (name == "tasks.list") Field("scope", new { type = "string", @enum = new[] { "created", "assigned", "pending" } }, true);
        if (name is "tasks.list" or "tasks.messages.list" or "agent-messages.list")
        {
            Field("cursor", Id()); Field("limit", new { type = "integer", minimum = 1, maximum = name == "tasks.list" ? 100 : 50 });
        }
        if (name == "agent-messages.list") Field("threadId", Id());
        if (name == "agent-messages.send") { Field("recipientInstanceId", Id(), true); Field("threadId", Id()); }
        if (name == "tasks.messages.send") Field("kind", new { type = "string", @enum = new[] { "progress", "question", "answer", "note" } }, true);
        if (name is "tasks.messages.send" or "agent-messages.send") Field("text", new { type = "string", minLength = 1, maxLength = 2000 }, true);
        if (name is "tasks.cancel" or "tasks.acknowledge") Field("expectedRevision", new { type = "integer", minimum = 1 }, true);
        if (name == "tasks.cancel") Field("reason", new { type = "string", maxLength = 160 });
        if (name == "tasks.acknowledge") Field("notificationId", Id(), true);
        if (name is "tasks.delegate" or "tasks.messages.send" or "agent-messages.send" or "tasks.cancel" or "tasks.acknowledge")
            Field("requestKey", new { type = "string", minLength = 1, maxLength = 128 }, true);
        return JsonSerializer.Serialize(new { type = "object", additionalProperties = false, properties, required });
    }
}
