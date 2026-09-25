using System.Collections.ObjectModel;
using System.Text.Json;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

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

    private static readonly HashSet<string> PublicationDeprecatedSummaryPropertyNames = new(StringComparer.Ordinal)
    {
        "definitionId",
        "version",
        "metadataRevision"
    };

    private static readonly HashSet<string> DraftCreatedSummaryPropertyNames = new(StringComparer.Ordinal)
    {
        "definitionId",
        "draftId",
        "sourceKind",
        "sourceVersion"
    };

    private static readonly HashSet<string> ManagedInstanceCreatedSummaryPropertyNames = new(StringComparer.Ordinal)
    {
        "definitionId",
        "instanceId",
        "version"
    };

    private static readonly HashSet<string> InstanceDefinitionVersionChangedSummaryPropertyNames = new(StringComparer.Ordinal)
    {
        "definitionId",
        "instanceId",
        "fromVersion",
        "toVersion"
    };

    private static readonly HashSet<string> PersonaChangedSummaryPropertyNames = new(StringComparer.Ordinal)
    {
        "definitionId",
        "instanceId",
        "fromPersonaRevision",
        "personaRevision",
        "personaFingerprint"
    };

    private static readonly HashSet<string> InstanceLifecycleChangedSummaryPropertyNames = new(StringComparer.Ordinal)
    {
        "definitionId",
        "instanceId",
        "fromLifecycle",
        "toLifecycle"
    };

    private static readonly HashSet<string> MemoryItemDeletedSummaryPropertyNames = new(StringComparer.Ordinal)
    {
        "instanceId",
        "memoryId",
        "scope",
        "sessionId"
    };

    private static readonly HashSet<string> MemoryScopeResetSummaryPropertyNames = new(StringComparer.Ordinal)
    {
        "instanceId",
        "scope",
        "itemsRemoved",
        "sessionId"
    };

    private static readonly HashSet<string> TriggerRegistrationRevokedSummaryPropertyNames = new(StringComparer.Ordinal)
    {
        "instanceId",
        "registrationId",
        "revision"
    };

    private static readonly HashSet<string> LearnedMemoryScopeNames = new(StringComparer.Ordinal)
    {
        nameof(AdminLearnedMemoryScope.Session),
        nameof(AdminLearnedMemoryScope.IdentityUser),
        nameof(AdminLearnedMemoryScope.User)
    };

    private static readonly HashSet<string> InstanceLifecycleNames = new(StringComparer.Ordinal)
    {
        nameof(AgentInstanceLifecycle.Active),
        nameof(AgentInstanceLifecycle.Archived)
    };

    private static readonly HashSet<string> DraftCreatedSourceKinds = new(StringComparer.Ordinal)
    {
        nameof(DefinitionDraftSourceKind.New),
        nameof(DefinitionDraftSourceKind.ForkBuiltIn),
        nameof(DefinitionDraftSourceKind.ForkDurable)
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

            if (append.Operation == AdminEventOperationKind.PublicationDeprecated)
            {
                ValidatePublicationDeprecatedSummary(document.RootElement);
                return;
            }

            if (append.Operation == AdminEventOperationKind.DraftCreated)
            {
                ValidateDraftCreatedSummary(document.RootElement);
                return;
            }

            if (append.Operation == AdminEventOperationKind.ManagedInstanceCreated)
            {
                ValidateManagedInstanceCreatedSummary(document.RootElement);
                return;
            }

            if (append.Operation == AdminEventOperationKind.InstanceDefinitionVersionChanged)
            {
                ValidateInstanceDefinitionVersionChangedSummary(document.RootElement);
                return;
            }

            if (append.Operation == AdminEventOperationKind.PersonaChanged)
            {
                ValidatePersonaChangedSummary(document.RootElement);
                return;
            }

            if (append.Operation is AdminEventOperationKind.InstanceArchived or AdminEventOperationKind.InstanceUnarchived)
            {
                ValidateInstanceLifecycleChangedSummary(document.RootElement);
                return;
            }

            if (append.Operation == AdminEventOperationKind.MemoryItemDeleted)
            {
                ValidateMemoryItemDeletedSummary(document.RootElement);
                return;
            }

            if (append.Operation == AdminEventOperationKind.MemoryScopeReset)
            {
                ValidateMemoryScopeResetSummary(document.RootElement);
                return;
            }

            if (append.Operation == AdminEventOperationKind.TriggerRegistrationRevoked)
            {
                ValidateTriggerRegistrationRevokedSummary(document.RootElement);
                return;
            }

            if (document.RootElement.GetPropertyCount() != 0)
            {
                throw AgentCoreErrors.Validation("Admin event summary metadata must be an empty object for this operation.");
            }
        }
    }

    private static void ValidateManagedInstanceCreatedSummary(JsonElement root)
    {
        EnsureExactProperties(root, ManagedInstanceCreatedSummaryPropertyNames);
        RequireString(root, "definitionId");
        RequireString(root, "instanceId");
        if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number)
        {
            throw AgentCoreErrors.Validation("Managed instance created event summary must include version.");
        }
    }

    private static void ValidateInstanceDefinitionVersionChangedSummary(JsonElement root)
    {
        EnsureExactProperties(root, InstanceDefinitionVersionChangedSummaryPropertyNames);
        RequireString(root, "definitionId");
        RequireString(root, "instanceId");
        if (!root.TryGetProperty("fromVersion", out var fromVersion) || fromVersion.ValueKind != JsonValueKind.Number)
        {
            throw AgentCoreErrors.Validation("Instance definition version changed event summary must include fromVersion.");
        }

        if (!root.TryGetProperty("toVersion", out var toVersion) || toVersion.ValueKind != JsonValueKind.Number)
        {
            throw AgentCoreErrors.Validation("Instance definition version changed event summary must include toVersion.");
        }
    }

    private static void ValidatePersonaChangedSummary(JsonElement root)
    {
        EnsureExactProperties(root, PersonaChangedSummaryPropertyNames);
        RequireString(root, "definitionId");
        RequireString(root, "instanceId");
        if (!root.TryGetProperty("fromPersonaRevision", out var fromRevision) || fromRevision.ValueKind != JsonValueKind.Number)
        {
            throw AgentCoreErrors.Validation("Persona changed event summary must include fromPersonaRevision.");
        }

        if (!root.TryGetProperty("personaRevision", out var toRevision) || toRevision.ValueKind != JsonValueKind.Number)
        {
            throw AgentCoreErrors.Validation("Persona changed event summary must include personaRevision.");
        }

        RequireString(root, "personaFingerprint");
        var fingerprint = root.GetProperty("personaFingerprint").GetString();
        if (string.IsNullOrEmpty(fingerprint)
            || fingerprint.Length != 64
            || !fingerprint.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw AgentCoreErrors.Validation("Persona changed event summary must include a lowercase SHA-256 persona fingerprint.");
        }
    }

    private static void ValidateMemoryItemDeletedSummary(JsonElement root)
    {
        EnsureExactProperties(root, MemoryItemDeletedSummaryPropertyNames);
        RequireString(root, "instanceId");
        RequireString(root, "memoryId");
        RequireLearnedMemoryScope(root, "scope");
        RequireNullableGuidString(root, "sessionId");
    }

    private static void ValidateMemoryScopeResetSummary(JsonElement root)
    {
        EnsureExactProperties(root, MemoryScopeResetSummaryPropertyNames);
        RequireString(root, "instanceId");
        RequireLearnedMemoryScope(root, "scope");
        if (!root.TryGetProperty("itemsRemoved", out var removed) || removed.ValueKind != JsonValueKind.Number)
        {
            throw AgentCoreErrors.Validation("Memory scope reset event summary must include itemsRemoved.");
        }

        if (removed.GetInt32() < 0)
        {
            throw AgentCoreErrors.Validation("Memory scope reset itemsRemoved must be non-negative.");
        }

        RequireNullableGuidString(root, "sessionId");
    }

    private static void ValidateTriggerRegistrationRevokedSummary(JsonElement root)
    {
        EnsureExactProperties(root, TriggerRegistrationRevokedSummaryPropertyNames);
        RequireString(root, "instanceId");
        RequireString(root, "registrationId");
        if (!root.TryGetProperty("revision", out var revision) || revision.ValueKind != JsonValueKind.Number)
        {
            throw AgentCoreErrors.Validation("Trigger registration revoked event summary must include revision.");
        }

        if (revision.GetInt64() < 1)
        {
            throw AgentCoreErrors.Validation("Trigger registration revoked revision must be positive.");
        }
    }

    private static void RequireLearnedMemoryScope(JsonElement root, string propertyName)
    {
        RequireString(root, propertyName);
        var value = root.GetProperty(propertyName).GetString();
        if (string.IsNullOrWhiteSpace(value) || !LearnedMemoryScopeNames.Contains(value))
        {
            throw AgentCoreErrors.Validation($"Memory admin event summary must include a valid {propertyName}.");
        }
    }

    private static void RequireNullableGuidString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            throw AgentCoreErrors.Validation($"Publication event summary must include {propertyName}.");
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw AgentCoreErrors.Validation($"{propertyName} must be null or a string.");
        }

        var text = value.GetString();
        if (text is not null && !Guid.TryParse(text, out _))
        {
            throw AgentCoreErrors.Validation($"{propertyName} must be null or a GUID string.");
        }
    }

    private static void ValidateInstanceLifecycleChangedSummary(JsonElement root)
    {
        EnsureExactProperties(root, InstanceLifecycleChangedSummaryPropertyNames);
        RequireString(root, "definitionId");
        RequireString(root, "instanceId");
        var from = RequireLifecycleName(root, "fromLifecycle");
        var to = RequireLifecycleName(root, "toLifecycle");
        if (from == to)
        {
            throw AgentCoreErrors.Validation("Instance lifecycle change summary must include distinct lifecycle values.");
        }
    }

    private static string RequireLifecycleName(JsonElement root, string propertyName)
    {
        RequireString(root, propertyName);
        var value = root.GetProperty(propertyName).GetString();
        if (string.IsNullOrWhiteSpace(value) || !InstanceLifecycleNames.Contains(value))
        {
            throw AgentCoreErrors.Validation($"Instance lifecycle change summary must include a valid {propertyName}.");
        }

        return value;
    }

    private static void ValidateDraftCreatedSummary(JsonElement root)
    {
        EnsureExactProperties(root, DraftCreatedSummaryPropertyNames);
        RequireString(root, "definitionId");
        RequireString(root, "draftId");
        if (!root.TryGetProperty("sourceKind", out var sourceKind) || sourceKind.ValueKind != JsonValueKind.String)
        {
            throw AgentCoreErrors.Validation("Draft created event summary must include sourceKind.");
        }

        var kind = sourceKind.GetString();
        if (string.IsNullOrWhiteSpace(kind) || !DraftCreatedSourceKinds.Contains(kind))
        {
            throw AgentCoreErrors.Validation("Draft created event summary contains an unknown sourceKind.");
        }

        if (!root.TryGetProperty("sourceVersion", out var sourceVersion))
        {
            throw AgentCoreErrors.Validation("Draft created event summary must include sourceVersion.");
        }

        if (sourceVersion.ValueKind != JsonValueKind.Null
            && sourceVersion.ValueKind != JsonValueKind.Number)
        {
            throw AgentCoreErrors.Validation("Draft created sourceVersion must be null or a number.");
        }
    }

    private static void ValidatePublicationDeprecatedSummary(JsonElement root)
    {
        EnsureExactProperties(root, PublicationDeprecatedSummaryPropertyNames);
        RequireString(root, "definitionId");
        if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number)
        {
            throw AgentCoreErrors.Validation("Publication deprecated event summary must include version.");
        }

        if (!root.TryGetProperty("metadataRevision", out var revision) || revision.ValueKind != JsonValueKind.Number)
        {
            throw AgentCoreErrors.Validation("Publication deprecated event summary must include metadataRevision.");
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
