using System.Text.Json;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Spaces;

namespace AgentCore.Domain.Tests;

public sealed class SpaceResourceIdentityTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-25T00:00:00Z");
    private static AgentDefinition Definition() => new(2, "examiner", 1, new("Alex", "role", "desc", "tone"), ["goal"], "instructions",
        new("acknowledgeThenContinue", true, true), new("concise", true, "en", 256), new(true, 8000, 30000, 1, ["longSilence"]),
        new(true, "default", 1.0), new("primary-llm", "primary-stt", "primary-tts"), new Dictionary<string, string>());
    private static RunSpaceContext Context(Guid tenant, Guid space) => new(tenant, space, Guid.NewGuid(), null, 1, 1, null, 0, "Shared", "", "", "", false, null, 1);
    private static SpaceFileResource File(Guid tenant, Guid space, Guid? resource = null) => new(tenant, space, resource ?? Guid.NewGuid(), "knowledge/project.txt",
        AgentDefinitionResourceKind.Knowledge, "text/plain", new string('a', 64), 12, "/home/project.txt", 1, At, At);
    [Fact]
    public void Space_manifest_requires_its_exact_captured_owner_and_never_uses_an_Instance_origin()
    {
        var tenant = Guid.NewGuid(); var space = Guid.NewGuid(); var file = File(tenant, space); var manifest = file.Effective();
        var captured = Context(tenant, space); var config = new AgentRunConfiguration(Definition(), 1, 1, [manifest], spaceContext: captured);
        Assert.Equal(file.Origin, config.Resources[0].SpaceSource);
        Assert.True(SpaceResourceKeys.TryParse(manifest.Key, out var owner, out var identity)); Assert.Equal(space, owner); Assert.Equal(file.ResourceId, identity);
        Assert.Throws<ArgumentException>(() => new AgentRunConfiguration(Definition(), 1, 1, [manifest]));
        Assert.Throws<ArgumentException>(() => new AgentRunConfiguration(Definition(), 1, 1, [manifest], spaceContext: Context(tenant, Guid.NewGuid())));
        Assert.Throws<ArgumentException>(() => new AgentRunConfiguration(Definition(), 1, 1, [manifest with { Key = "instance:" + file.ResourceId }], spaceContext: captured));
        Assert.Throws<ArgumentException>(() => new AgentRunConfiguration(Definition(), 1, 1, [manifest with { SpaceSource = null }], spaceContext: captured));
        Assert.Throws<ArgumentException>(() => new AgentRunConfiguration(Definition(), 1, 1, [manifest with { SpaceSource = file.Origin with { TenantId = Guid.NewGuid() } }], spaceContext: captured));
        var updated = file with { Revision = 2, ContentSha256 = new string('b', 64), UpdatedAt = At.AddSeconds(1) };
        Assert.NotEqual(config.ConfigurationHash, new AgentRunConfiguration(Definition(), 1, 1, [updated.Effective()], spaceContext: captured).ConfigurationHash);
    }
    [Theory]
    [InlineData("space:display-name:project.txt")]
    [InlineData("space:00000000-0000-0000-0000-000000000000:11111111-1111-4111-8111-111111111111")]
    [InlineData("space:AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA:11111111-1111-4111-8111-111111111111")]
    public void Space_file_binding_refuses_paths_empty_or_noncanonical_source_ids(string key) =>
        Assert.Throws<ArgumentException>(() => new SpaceResourceBinding(SpaceBindingKind.SpaceFile, key).Validate());
    [Fact]
    public void Legacy_manifest_bytes_omit_new_origin_and_Space_snapshot_reopens_with_the_real_owner()
    {
        var legacy = new EffectiveAgentResource("instance:" + Guid.NewGuid(), "guide.txt", AgentDefinitionResourceKind.Knowledge, "text/plain", new string('a', 64), 12, "/agent/instance/resources/guide.txt");
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.DoesNotContain("spaceSource", JsonSerializer.Serialize(legacy, json), StringComparison.Ordinal);
        var source = File(Guid.NewGuid(), Guid.NewGuid()); var reopened = JsonSerializer.Deserialize<SpaceFileResource>(JsonSerializer.Serialize(source, json), json)!;
        reopened.Validate(); Assert.Equal(source.Effective(), reopened.Effective());
        Assert.DoesNotContain("instance:", reopened.Key, StringComparison.Ordinal);
    }
}
