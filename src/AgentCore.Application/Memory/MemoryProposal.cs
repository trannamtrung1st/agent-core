using System.Text.Json;
using AgentCore.Domain.Memory;

namespace AgentCore.Application.Memory;

public enum MemoryProposalOperation
{
    Upsert = 0,
    Delete = 1
}

public enum MemoryProposalSource
{
    UserExplicit = 0,
    AgentInferred = 1,
    Application = 2,
    Admin = 3
}

public enum MemoryScopeHint
{
    Session = 0,
    IdentityUser = 1,
    User = 2
}

public enum MemoryAdmissionStatus
{
    None = 0,
    Stored,
    Updated,
    AlreadyStored,
    StoredSessionOnly,
    UpdatedSessionOnly,
    Rejected,
    Unavailable,
    Deleted
}

/// <summary>
/// Agent or application intent to change learned memory. It carries no owner, profile, or memory id.
/// ScopeHint is advisory. MemoryAdmission decides whether a write is allowed.
/// </summary>
public sealed record MemoryProposal(
    MemoryProposalOperation Operation,
    MemoryKind Kind,
    string Subject,
    string Content,
    MemoryScopeHint? ScopeHint,
    MemoryProposalSource Source);

public sealed record MemoryAdmissionResult(
    MemoryAdmissionStatus Status,
    MemoryProposal Proposal,
    /// <summary>
    /// Memory layers successfully established or confirmed by this admission (session, identityUser, user).
    /// </summary>
    IReadOnlyList<string>? AffectedScopes = null);

public static class MemoryProposalCodec
{
    public const int MaxProposalsPerTurn = 4;

    public static string Marker(IReadOnlyList<MemoryProposal> proposals)
    {
        var payload = JsonSerializer.Serialize(proposals.Select(ToDto).ToArray());
        return "[[memory:" + payload + " ]]";
    }

    public static IReadOnlyList<MemoryProposal> ParseMarkerPayload(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var proposals = new List<MemoryProposal>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
            if (TryRead(item, out var proposal)
                && proposal is not null
                && IsConversationalSource(proposal.Source))
            {
                proposals.Add(proposal);
            }
            }

            return proposals;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static bool TryRead(JsonElement item, out MemoryProposal? proposal)
    {
        proposal = null;
        if (item.ValueKind != JsonValueKind.Object
            || !TryEnum(item, "operation", out MemoryProposalOperation operation)
            || !TryEnum(item, "kind", out MemoryKind kind)
            || !TryEnum(item, "source", out MemoryProposalSource source)
            || !item.TryGetProperty("subject", out var subjectEl)
            || subjectEl.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var subject = subjectEl.GetString() ?? string.Empty;
        var content = string.Empty;
        if (item.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
        {
            content = contentEl.GetString() ?? string.Empty;
        }
        else if (item.TryGetProperty("content", out contentEl) && contentEl.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            return false;
        }

        MemoryScopeHint? scope = null;
        if (item.TryGetProperty("scopeHint", out var scopeEl) && scopeEl.ValueKind == JsonValueKind.String)
        {
            if (!Enum.TryParse<MemoryScopeHint>(scopeEl.GetString(), ignoreCase: true, out var parsedScope))
            {
                return false;
            }

            scope = parsedScope;
        }

        proposal = new MemoryProposal(operation, kind, subject, content, scope, source);
        return true;
    }

    public static bool IsConversationalSource(MemoryProposalSource source) =>
        source is MemoryProposalSource.UserExplicit or MemoryProposalSource.AgentInferred;

    private static bool TryEnum<TEnum>(JsonElement item, string name, out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;
        return item.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.String
            && Enum.TryParse(element.GetString(), ignoreCase: true, out value)
            && Enum.IsDefined(value);
    }

    private static object ToDto(MemoryProposal proposal) => new
    {
        operation = Name(proposal.Operation),
        kind = Name(proposal.Kind),
        subject = proposal.Subject,
        content = proposal.Content,
        scopeHint = proposal.ScopeHint is { } hint ? Name(hint) : null,
        source = Name(proposal.Source)
    };

    private static string Name<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        var text = value.ToString();
        return char.ToLowerInvariant(text[0]) + text[1..];
    }
}

public static class MemoryAdmissionPrompt
{
    public const string SelectivityGuidance = """
        Memory proposals are optional structured intents. Propose durable memory only when it is reasonably useful beyond this turn: stable preferences, durable facts, user goals, decisions, open loops, or working context likely to recur. Do not propose incidental small talk, ephemeral current state, duplicates, guesses stated as fact, assistant-invented details, secrets, or information the memory policy disallows. Do not say that information was saved, remembered, or forgotten. That wording rule is guidance. After a response completes successfully, the runtime records the admission outcome apart from the reply. It does not append that outcome to the answer. The receipt is the authoritative outcome. The runtime does not rewrite earlier sentences.
        """;

}
