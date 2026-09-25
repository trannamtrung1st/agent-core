using System.Collections.ObjectModel;
using System.Text.Json;
using AgentCore.Application.Sessions;

namespace AgentCore.Application.Admin;

public static class AdminEventSummaryPolicy
{
    public const int MaxSummaryJsonLength = 4096;

    private static readonly string[] SecretSentinels =
    [
        "OPENROUTER_API_KEY",
        "OPENROUTER_SECRET",
        "OWNER_CAPABILITY_SENTINEL"
    ];

    private static readonly IReadOnlySet<string> PublicationChangedSectionIdsBacking = new ReadOnlySet<string>(
        new HashSet<string>(StringComparer.Ordinal)
        {
            "instructions",
            "goals",
            "identity",
            "behavior",
            "conversation",
            "initiative",
            "providers",
            "modelDefaults",
            "capabilities",
            "memoryPolicy",
            "triggerPolicy",
            "resources"
        });

    public static IReadOnlySet<string> PublicationChangedSectionIds => PublicationChangedSectionIdsBacking;

    private static readonly HashSet<string> PublicationSummaryPropertyNames = new(StringComparer.Ordinal)
    {
        "definitionId",
        "draftId",
        "version",
        "changedSections"
    };

    public static void ValidateAppend(AdminEventAppend append)
    {
        if (string.IsNullOrWhiteSpace(append.SummaryJson))
        {
            throw AgentCoreErrors.Validation("Admin event summary metadata is required.");
        }

        if (append.SummaryJson.Length > MaxSummaryJsonLength)
        {
            throw AgentCoreErrors.Validation("Admin event summary metadata is too large.");
        }

        RejectSecretSentinels(append.SummaryJson);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(append.SummaryJson);
        }
        catch (JsonException)
        {
            throw AgentCoreErrors.Validation("Admin event summary metadata must be valid JSON.");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw AgentCoreErrors.Validation("Admin event summary metadata must be a JSON object.");
            }

            if (append.Operation == AdminEventOperationKind.PublicationCreated)
            {
                ValidatePublicationCreatedSummary(document.RootElement);
                return;
            }

            if (document.RootElement.GetPropertyCount() != 0)
            {
                throw AgentCoreErrors.Validation("Admin event summary metadata must be an empty object for this operation.");
            }
        }
    }

    private static void ValidatePublicationCreatedSummary(JsonElement root)
    {
        EnsureExactProperties(root, PublicationSummaryPropertyNames);
        RequireString(root, "definitionId");
        RequireString(root, "draftId");
        if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number)
        {
            throw AgentCoreErrors.Validation("Publication event summary must include version.");
        }

        if (!root.TryGetProperty("changedSections", out var sections))
        {
            throw AgentCoreErrors.Validation("Publication event summary must include changedSections.");
        }

        if (sections.ValueKind != JsonValueKind.Array)
        {
            throw AgentCoreErrors.Validation("Publication changedSections must be an array.");
        }

        var count = 0;
        foreach (var item in sections.EnumerateArray())
        {
            count++;
            if (count > PublicationChangedSectionIds.Count)
            {
                throw AgentCoreErrors.Validation("Publication changedSections exceeds the allowlist size.");
            }

            if (item.ValueKind != JsonValueKind.String)
            {
                throw AgentCoreErrors.Validation("Publication changedSections entries must be strings.");
            }

            var id = item.GetString();
            if (string.IsNullOrWhiteSpace(id) || !PublicationChangedSectionIds.Contains(id))
            {
                throw AgentCoreErrors.Validation("Publication changedSections contains an unknown section id.");
            }
        }
    }

    private static void EnsureExactProperties(JsonElement root, IReadOnlySet<string> allowed)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw AgentCoreErrors.Validation("Admin event summary metadata contains an unknown property.");
            }
        }

        foreach (var required in allowed)
        {
            if (!root.TryGetProperty(required, out _))
            {
                throw AgentCoreErrors.Validation($"Publication event summary must include {required}.");
            }
        }
    }

    private static void RejectSecretSentinels(string summaryJson)
    {
        foreach (var sentinel in SecretSentinels)
        {
            if (summaryJson.Contains(sentinel, StringComparison.Ordinal))
            {
                throw AgentCoreErrors.Validation("Admin event summary metadata must not contain secret sentinels.");
            }
        }
    }

    private static void RequireString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw AgentCoreErrors.Validation($"Publication event summary must include {name}.");
        }
    }
}
