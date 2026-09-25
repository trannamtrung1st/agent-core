using AgentCore.Application.Admin;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tests;

public sealed class AdminEventSummaryPolicyTests
{
    [Fact]
    public void PublicationChangedSectionIds_exposes_read_only_allowlist()
    {
        var allowlist = AdminEventSummaryPolicy.PublicationChangedSectionIds;
        Assert.Contains("triggerPolicy", allowlist);
        Assert.DoesNotContain("apiKey", allowlist);
        Assert.IsAssignableFrom<IReadOnlySet<string>>(allowlist);
        Assert.Null(allowlist as HashSet<string>);
    }

    [Fact]
    public void PublicationDeprecated_summary_includes_metadata_revision()
    {
        var append = AdminEventFactory.PublicationDeprecated(
            Guid.NewGuid(),
            DateTimeOffset.Parse("2026-09-25T12:00:00Z"),
            "examiner",
            2,
            3);
        Assert.Contains("\"metadataRevision\":3", append.SummaryJson, StringComparison.Ordinal);
        AdminEventSummaryPolicy.ValidateAppend(append);
    }

    [Fact]
    public void PublicationCreated_summary_includes_allowlisted_changed_sections()
    {
        var append = AdminEventFactory.PublicationCreated(
            Guid.NewGuid(),
            DateTimeOffset.Parse("2026-09-25T12:00:00Z"),
            "examiner",
            2,
            Guid.NewGuid(),
            3,
            ["triggerPolicy", "instructions"]);
        Assert.Contains("\"changedSections\"", append.SummaryJson, StringComparison.Ordinal);
        Assert.Contains("triggerPolicy", append.SummaryJson, StringComparison.Ordinal);
        AdminEventSummaryPolicy.ValidateAppend(append);
    }

    [Fact]
    public void ValidateAppend_rejects_non_object_summary()
    {
        var append = new AdminEventAppend(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            AdminEventActorKind.LocalOwner,
            AdminEventOperationKind.PublicationCreated,
            "definition.publication",
            "examiner:1",
            1,
            1,
            "[]");
        var error = Assert.Throws<AgentCoreException>(() => AdminEventSummaryPolicy.ValidateAppend(append));
        Assert.Equal(400, error.StatusCode);
    }

    [Fact]
    public void ValidateAppend_rejects_extra_publication_summary_properties()
    {
        var append = new AdminEventAppend(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            AdminEventActorKind.LocalOwner,
            AdminEventOperationKind.PublicationCreated,
            "definition.publication",
            "examiner:1",
            1,
            1,
            """{"definitionId":"examiner","draftId":"019944af-00d1-7000-8000-000000000099","version":1,"changedSections":["instructions"],"apiKey":"x"}""");
        var error = Assert.Throws<AgentCoreException>(() => AdminEventSummaryPolicy.ValidateAppend(append));
        Assert.Equal(400, error.StatusCode);
    }

    [Fact]
    public void ValidateAppend_rejects_secret_sentinel_in_summary()
    {
        var append = new AdminEventAppend(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            AdminEventActorKind.LocalOwner,
            AdminEventOperationKind.PublicationCreated,
            "definition.publication",
            "examiner:1",
            1,
            1,
            """{"definitionId":"examiner","draftId":"019944af-00d1-7000-8000-000000000099","version":1,"changedSections":["instructions"],"note":"OPENROUTER_API_KEY"}""");
        var error = Assert.Throws<AgentCoreException>(() => AdminEventSummaryPolicy.ValidateAppend(append));
        Assert.Equal(400, error.StatusCode);
    }

    [Fact]
    public void InstanceDefinitionVersionChanged_summary_includes_version_transition()
    {
        var append = AdminEventFactory.InstanceDefinitionVersionChanged(
            Guid.NewGuid(),
            DateTimeOffset.Parse("2026-09-25T12:00:00Z"),
            "examiner",
            Guid.Parse("019944af-00d1-7000-8000-000000000097"),
            1,
            2);
        Assert.Contains("\"fromVersion\":1", append.SummaryJson, StringComparison.Ordinal);
        AdminEventSummaryPolicy.ValidateAppend(append);
    }

    [Fact]
    public void ManagedInstanceCreated_summary_includes_definition_metadata()
    {
        var append = AdminEventFactory.ManagedInstanceCreated(
            Guid.NewGuid(),
            DateTimeOffset.Parse("2026-09-25T12:00:00Z"),
            "examiner",
            Guid.Parse("019944af-00d1-7000-8000-000000000098"),
            2);
        Assert.Contains("\"definitionId\":\"examiner\"", append.SummaryJson, StringComparison.Ordinal);
        AdminEventSummaryPolicy.ValidateAppend(append);
    }

    [Fact]
    public void DraftCreated_summary_includes_source_metadata()
    {
        var append = AdminEventFactory.DraftCreated(
            Guid.NewGuid(),
            DateTimeOffset.Parse("2026-09-25T12:00:00Z"),
            "examiner",
            Guid.Parse("019944af-00d1-7000-8000-000000000099"),
            DefinitionDraftSourceKind.ForkBuiltIn,
            1);
        Assert.Contains("\"sourceKind\":\"ForkBuiltIn\"", append.SummaryJson, StringComparison.Ordinal);
        AdminEventSummaryPolicy.ValidateAppend(append);
    }

    [Fact]
    public void ValidateAppend_requires_empty_object_for_unimplemented_operations()
    {
        var append = new AdminEventAppend(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            AdminEventActorKind.LocalOwner,
            AdminEventOperationKind.InstanceArchived,
            "agent.instance",
            "019944af-00d1-7000-8000-000000000099",
            1,
            null,
            """{"instanceId":"x"}""");
        var error = Assert.Throws<AgentCoreException>(() => AdminEventSummaryPolicy.ValidateAppend(append));
        Assert.Equal(400, error.StatusCode);
    }
}
