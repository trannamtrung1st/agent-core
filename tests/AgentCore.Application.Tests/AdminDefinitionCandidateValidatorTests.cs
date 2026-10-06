using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tests;

public sealed class AdminDefinitionCandidateValidatorTests
{
    [Fact]
    public void CollectPublicationFindings_allows_attachments_read_on_tool_allowlist_without_host_configuration()
    {
        var candidate = AgentDefinitionCandidate.FromDefinition(SampleDefinitions.Support) with
        {
            Environment = RoleEnvironment.Empty with
            {
                ToolAllowlist = [ToolCatalog.AttachmentsRead, ToolCatalog.WorkspaceRead]
            }
        };

        var findings = AgentDefinitionCandidateValidator.CollectPublicationFindings(
            candidate,
            SyntheticProviderAliases.Default,
            TestModelCatalogs.Synthetic(),
            ToolConfigurationGates.AllowAll);

        Assert.DoesNotContain(
            findings,
            finding => finding.Code == "unconfigured_tool"
                && finding.Message.Contains(ToolCatalog.AttachmentsRead, StringComparison.Ordinal));
        Assert.DoesNotContain(findings, finding => finding.Code == "unconfigured_tool");
    }

    [Fact]
    public void CollectPublicationFindings_blocks_configuration_gated_tools_when_host_is_not_configured()
    {
        var candidate = AgentDefinitionCandidate.FromDefinition(SampleDefinitions.Support) with
        {
            Environment = RoleEnvironment.Empty with
            {
                ToolAllowlist = [ToolCatalog.WebSearch]
            }
        };

        var findings = AgentDefinitionCandidateValidator.CollectPublicationFindings(
            candidate,
            SyntheticProviderAliases.Default,
            TestModelCatalogs.Synthetic(),
            ToolConfigurationGates.Unconfigured);

        Assert.Contains(
            findings,
            finding => finding.Code == "unconfigured_tool"
                && finding.Field == "environment.toolAllowlist"
                && finding.Message.Contains(ToolCatalog.WebSearch, StringComparison.Ordinal));
    }

    [Fact]
    public void CollectPublicationFindings_reports_skill_secrets_on_author_fields()
    {
        var candidate = Candidate(
            new SkillSpec(
                "refund.handle",
                "OPENROUTER_API_KEY",
                "OPENROUTER_API_KEY",
                "OPENROUTER_API_KEY",
                ["OPENROUTER_API_KEY"],
                [SkillCapabilities.ChatRespond],
                []));

        var findings = Publish(candidate);
        Assert.Contains(findings, finding => finding.Field == "skills[0].name" && finding.Code == "secret_reference");
        Assert.Contains(findings, finding => finding.Field == "skills[0].description" && finding.Code == "secret_reference");
        Assert.Contains(findings, finding => finding.Field == "skills[0].procedure" && finding.Code == "secret_reference");
        Assert.Contains(
            findings,
            finding => finding.Field == "skills[0].activationKeywords[0]" && finding.Code == "secret_reference");
    }

    [Fact]
    public void CollectPublicationFindings_rejects_unknown_and_unlisted_capabilities_without_adding_chat()
    {
        var allowlist = new[] { ToolCatalog.WorkspaceRead };
        var candidate = Candidate(
            new SkillSpec(
                "refund.handle",
                "Refunds",
                "",
                "Confirm the order.",
                ["refund"],
                [SkillCapabilities.ChatRespond, "orders.read", ToolCatalog.KnowledgeRetrieve],
                []),
            allowlist);

        var findings = Publish(candidate);
        Assert.Contains(findings, finding => finding.Code == "unknown_capability" && finding.Field == "skills[0].requiredCapabilities[1]");
        Assert.Contains(
            findings,
            finding => finding.Code == "capability_not_allowed" && finding.Field == "skills[0].requiredCapabilities[2]");
        Assert.DoesNotContain(findings, finding => finding.Message.Contains(SkillCapabilities.ChatRespond, StringComparison.Ordinal));
        Assert.Equal(allowlist, candidate.Environment!.ToolList);
        Assert.False(ToolRegistry.TryGet(SkillCapabilities.ChatRespond, out _));
    }

    [Theory]
    [InlineData("duplicate", "duplicate_tool")]
    [InlineData("malformed", "invalid_tool_name")]
    [InlineData("unknown", "unregistered_tool")]
    public void Tool_validation_returns_precise_field_codes(string scenario, string code)
    {
        IReadOnlyList<string> tools = scenario switch
        {
            "duplicate" => ["workspace.read", "workspace.read"],
            "malformed" => ["Workspace Read"],
            _ => ["unknown.tool"]
        };
        var candidate = AgentDefinitionCandidate.FromDefinition(SampleDefinitions.Support) with
        { Environment = new RoleEnvironment(ToolAllowlist: tools) };
        Assert.Contains(Publish(candidate), f => f.Field == "environment.toolAllowlist" && f.Code == code);
    }

    [Fact]
    public void Capability_authoring_separates_authorization_configuration_and_projection_errors()
    {
        var candidate = AgentDefinitionCandidate.FromDefinition(SampleDefinitions.Support) with
        { Environment = new(Capabilities: new("Selected", [ToolCatalog.EmailSearch]), Projection: new([])) };
        Assert.Empty(AgentDefinitionCandidateValidator.CollectPublicationFindings(candidate, SyntheticProviderAliases.Default,
            TestModelCatalogs.Synthetic(), ToolConfigurationGates.Unconfigured));
        Assert.Contains(Publish(candidate with { Environment = candidate.Environment with { Projection = new([ToolCatalog.WorkspaceRead]) } }),
            f => f.Field == "environment.projection.alwaysCapabilities" && f.Code == "unauthorized_projection");
        Assert.Contains(Publish(candidate with { Environment = new(Capabilities: new("Selected", [ToolCatalog.WorkComplete]), Projection: new([ToolCatalog.WorkComplete])) }),
            f => f.Code == "context_only_capability");
        Assert.Contains(Publish(candidate with { Environment = new(Capabilities: new("Selected", ["unregistered.name"])) }), f => f.Code == "unregistered_capability");
        Assert.Contains(Publish(candidate with { Environment = new(Capabilities: new("Selected", [ToolCatalog.EmailSearch, ToolCatalog.EmailSearch])) }), f => f.Code == "duplicate_tool");
        Assert.Contains(Publish(candidate with { Environment = new(Capabilities: new("Selected", null!)) }), f => f.Code == "domain_shape");
        Assert.Contains(Publish(candidate with { SystemInstructions = new string('x', AgentDefinitionCandidateValidator.MaxCandidateBytes + 1) }), f => f.Code == "document_too_large");
        foreach (var names in new IReadOnlyList<string>[] { [], [ToolCatalog.WorkspaceRead], ToolRegistry.AllKnownNames().Where(n => n != ToolCatalog.WorkspaceCwd).ToArray() })
        {
            var findings = Publish(candidate with { Environment = new(Capabilities: new("Selected", names)) });
            Assert.Empty(findings);
        }
    }

    private static AgentDefinitionCandidate Candidate(SkillSpec skill, IReadOnlyList<string>? tools = null) =>
        AgentDefinitionCandidate.FromDefinition(SampleDefinitions.Support) with
        {
            Environment = RoleEnvironment.Empty with { ToolAllowlist = tools ?? [ToolCatalog.WorkspaceRead] },
            Skills = [skill]
        };

    private static IReadOnlyList<DefinitionValidationFinding> Publish(AgentDefinitionCandidate candidate) =>
        AgentDefinitionCandidateValidator.CollectPublicationFindings(
            candidate,
            SyntheticProviderAliases.Default,
            TestModelCatalogs.Synthetic(),
            ToolConfigurationGates.AllowAll);
}
