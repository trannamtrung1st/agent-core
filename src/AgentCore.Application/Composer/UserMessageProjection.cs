using System.Text.Json;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Composer;

/// <summary>Semantic views of validated parts; never changes stored identity or activates Skills.</summary>
public static class UserMessageProjection
{
    public static string ForModel(ConversationEntry entry, bool currentTurn) =>
        Project(entry, currentTurn, includeIdentity: true);

    public static string ForSummary(ConversationEntry entry) =>
        Project(entry, currentTurn: false, includeIdentity: false);

    public static string ForTitle(ConversationEntry entry)
    {
        if (entry.Parts is null) return entry.Text;
        var skills = entry.Parts.Where(p => p.Kind == "invocation")
            .Select(p => "/" + SkillTitle(p)).ToArray();
        var task = string.Concat(entry.Parts.Where(p => p.Kind != "invocation")
            .Select(p => p.Kind == "text" ? p.Text : ReferenceTitle(p))).Trim();
        return skills.Length == 0 ? task : string.Join(" + ", skills) + (task.Length == 0 ? "" : " · " + task);
    }

    private static string Project(ConversationEntry entry, bool currentTurn, bool includeIdentity) =>
        entry.Parts is null ? entry.Text : string.Concat(entry.Parts.Select(p => p.Kind switch
        {
            "text" => p.Text,
            "invocation" => "[" + (currentTurn ? "Explicitly invoked Skill: " : "Previously invoked Skill (historical, not activated): ")
                + JsonSerializer.Serialize(p.Label ?? SkillTitle(p), UserMessageContent.Json)
                + (includeIdentity ? "; key=" + p.SkillKey : "") + "]",
            "reference" => "[Referenced " + p.Reference!.Kind + ": " + JsonSerializer.Serialize(ReferenceName(p), UserMessageContent.Json)
                + (includeIdentity ? "; locator=" + p.Reference.Locator : "") + "]",
            _ => throw new ArgumentException("Invalid message part.")
        }));

    private static string SkillTitle(UserMessagePart part)
    {
        // Identity always comes from SkillKey. A UUID has no readable title; use its stored server label.
        var key = part.SkillKey!;
        var id = key[(key.IndexOf(':') + 1)..];
        return Guid.TryParseExact(id, "D", out _) ? part.Label ?? "Skill" : id;
    }

    private static string ReferenceTitle(UserMessagePart part) => part.Label is { } label
        ? part.Reference!.Kind == "homeFile" ? label[(label.LastIndexOf('/') + 1)..] : label
        : part.Reference!.Kind;

    private static string ReferenceName(UserMessagePart part) => part.Label ?? part.Reference!.Kind;
}
