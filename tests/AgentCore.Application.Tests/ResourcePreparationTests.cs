using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tests;

public sealed class ResourcePreparationTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
    private static AgentDefinition Definition => SampleDefinitions.Examiner with {
        SelfManagement = new(HarnessManagementMode.Assisted, [HarnessManagementScope.Resources], [], [])
    };
    private static AgentInstance Owner => new(Guid.NewGuid(), Definition.Id, Definition.Version, Definition.Identity,
        AgentInstanceLifecycle.Active, At, At, 3);
    private static AgentDefinitionPublicationResource Published(byte[] bytes, AgentDefinitionResourceKind kind = AgentDefinitionResourceKind.Reference) =>
        new(Definition.Id, Definition.Version, Guid.NewGuid(), "source.dat", kind, kind == AgentDefinitionResourceKind.StaticAsset ? "image/png" : "text/plain",
            DefinitionResourceContentHasher.ComputeSha256Hex(bytes), bytes.Length);

    [Fact]
    public void Copy_preserves_actual_binary_source_provenance_and_creates_an_independent_enabled_binding()
    {
        var owner = Owner; byte[] bytes = [0, 255, 1, 128]; var source = Published(bytes, AgentDefinitionResourceKind.StaticAsset);
        var state = new AgentDefinitionResourceState(owner.InstanceId, source.ResourceId, false, 1, At);
        var snapshot = new InstanceResourceSnapshot([state], []); var allocated = Guid.NewGuid();
        var prepared = TenantAgentResourcePreparation.Upsert(owner, Definition, snapshot, new([], []), [source],
            new(null, null, "local/copied.dat", source.Kind, source.MediaType, bytes), bytes, SkillAuthor.Agent, allocated, At.AddMinutes(1), source);
        var written = prepared.Change!.Resource!.Resource!;
        Assert.Equal(allocated, written.ResourceId); Assert.Equal(owner.InstanceId, written.InstanceId); Assert.Equal(SkillAuthor.Agent, written.CreatedBy);
        Assert.Equal(source.ContentSha256, written.ContentSha256); Assert.Equal(bytes.Length, written.ByteLength); Assert.True(written.Enabled);
        Assert.Equal(source.DefinitionId, written.SourceDefinitionId); Assert.Equal(source.Version, written.SourceDefinitionVersion); Assert.Equal(source.ResourceId, written.SourceDefinitionResourceId);
        Assert.Equal(state, Assert.Single(prepared.Next.DefinitionStates)); Assert.Empty(snapshot.InstanceResources);
    }

    [Theory]
    [InlineData("missing-binding")] [InlineData("definition")] [InlineData("version")] [InlineData("hash")]
    [InlineData("length")] [InlineData("kind")] [InlineData("media")] [InlineData("update")] [InlineData("eval")]
    public void Copy_rejects_forged_manifest_content_or_destination_provenance(string altered)
    {
        var owner = Owner; byte[] bytes = [65]; var source = Published(bytes);
        source = altered switch {
            "definition" => source with { DefinitionId = "unrelated" }, "version" => source with { Version = source.Version + 1 },
            "hash" => source with { ContentSha256 = new('0', 64) }, "length" => source with { ByteLength = 2 },
            "eval" => source with { Kind = AgentDefinitionResourceKind.EvalFixture }, _ => source
        };
        var input = new TenantAgentResourceInput(altered == "update" ? Guid.NewGuid() : null, altered == "update" ? 1 : null,
            "copy.txt", altered == "kind" ? AgentDefinitionResourceKind.Template : source.Kind,
            altered == "media" ? "text/markdown" : source.MediaType, bytes);
        Assert.ThrowsAny<AgentCoreException>(() => TenantAgentResourcePreparation.Upsert(owner, Definition, new([], []), new([], []),
            altered == "missing-binding" ? [] : [source], input, bytes, SkillAuthor.Agent, Guid.NewGuid(), At, source));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Resource_choices_preserve_active_Skill_file_dependencies(bool reset)
    {
        var owner = Owner; var source = Published([65]);
        var definition = Definition with { Skills = [new("review", "Review", "Read evidence", "Read source.dat.", SkillProjection.OnDemand, true, ["chat.respond"], ["source.dat"])] };
        var snapshot = reset ? new InstanceResourceSnapshot([new(owner.InstanceId, source.ResourceId, true, 1, At)], []) : new([], []);
        var skills = new InstanceSkillSnapshot([new(owner.InstanceId, "review", null, 1, At)], []);
        if (!reset)
        {
            var error = Assert.Throws<AgentCoreException>(() => TenantAgentResourcePreparation.Choice(owner, definition, snapshot, skills, [source],
                "definition:" + source.ResourceId.ToString("D"), false, false, 0, SkillAuthor.Agent, At));
            Assert.Contains("depend on disabled resources", error.Message, StringComparison.Ordinal);
        }
        else
        {
            var prepared = TenantAgentResourcePreparation.Choice(owner, definition, snapshot, skills, [source],
                "definition:" + source.ResourceId.ToString("D"), false, null, 1, SkillAuthor.Agent, At);
            Assert.True(prepared.Resource!.Enabled); Assert.Null(prepared.Resource.EnabledOverride); Assert.NotNull(prepared.Change);
        }
    }

    [Theory]
    [InlineData("count")] [InlineData("bytes")] [InlineData("duplicate")]
    public void Resource_proposals_reject_collection_limits_before_the_effect_boundary(string limit)
    {
        var owner = Owner;
        var locals = Enumerable.Range(0, limit == "count" ? 64 : 8).Select(i => new AgentInstanceResource(owner.InstanceId, Guid.NewGuid(),
            $"file-{i}.txt", AgentDefinitionResourceKind.Reference, "text/plain", new('a', 64),
            limit == "bytes" ? AgentResourceLimits.MaxItemBytes : 1, true, 1, At, At, SkillAuthor.Admin)).ToArray();
        Assert.Throws<AgentCoreException>(() => TenantAgentResourcePreparation.Upsert(owner, Definition, new([], locals), new([], []), [],
            new(null, null, limit == "duplicate" ? "file-0.txt" : "new.txt", AgentDefinitionResourceKind.Reference, "text/plain", [65]),
            [65], SkillAuthor.Agent, Guid.NewGuid(), At));
    }

    [Theory]
    [InlineData("off")] [InlineData("scope")] [InlineData("frozen")] [InlineData("archived")]
    public void Resource_preparation_rechecks_current_management_and_lifecycle_even_for_unchanged_choices(string fence)
    {
        var owner = Owner; var source = Published([65]);
        var definition = Definition with { SelfManagement = fence switch {
            "off" => new(HarnessManagementMode.Disabled, [], [], []),
            "scope" => new(HarnessManagementMode.Assisted, [HarnessManagementScope.Skills], [], []),
            _ => Definition.SelfManagement
        } };
        if (fence == "frozen") owner = owner with { HarnessManagement = new(Definition.SelfManagement! with { Frozen = true }) };
        if (fence == "archived") owner = owner with { Lifecycle = AgentInstanceLifecycle.Archived };
        Assert.Throws<AgentCoreException>(() => TenantAgentResourcePreparation.Choice(owner, definition, new([], []), new([], []), [source],
            "definition:" + source.ResourceId.ToString("D"), false, null, 0, SkillAuthor.Agent, At));
    }
}
