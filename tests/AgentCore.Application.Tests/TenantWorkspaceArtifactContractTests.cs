using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Tests;

public sealed class TenantWorkspaceArtifactContractTests
{
    [Fact]
    public async Task Source_projection_offers_only_the_admitted_variant_in_a_current_human_message()
    {
        var definition = await CapabilityProjectionTests.Definition(ToolCatalog.ArtifactsCreateFromWorkspace);
        var session = Guid.NewGuid(); var activation = Guid.NewGuid();
        var context = CapabilityProjectionTests.Context(definition) with { AgentInstanceId = Guid.NewGuid(), OwnedSessionId = session,
            ExecutionContext = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), session, Guid.NewGuid(), activation, activation, null, null, AgentCore.Domain.Conversation.ExecutionOrigin.UserMessage) };
        var tools = ToolProjectionService.Project(definition, context, ToolConfigurationGates.AllowAll);
        var source = Assert.Single(tools, t => t.Name == ToolCatalog.ArtifactsCreateFromWorkspace);
        Assert.Equal(TenantWorkspaceArtifactContract.ForModel(), source);
        Assert.DoesNotContain(tools, t => t.Name == ToolCatalog.ArtifactsCreate);
        using var schema = JsonDocument.Parse(Assert.Single(tools, t => t.Name == ToolCatalog.RequestAuthorization).ParametersJson);
        var variants = schema.RootElement.GetProperty("oneOf").EnumerateArray().Select(b => b.GetProperty("properties").GetProperty("operation").GetProperty("enum")[0].GetString()).ToArray();
        Assert.Contains("publish_workspace_artifact", variants); Assert.DoesNotContain("publish_artifact", variants);
        foreach (var invalid in new[] { context with { ModelSupportsTools = false }, context with { OwnedSessionId = Guid.NewGuid() },
            context with { ExecutionContext = new(context.ExecutionContext!.TenantId, context.ExecutionContext.AgentPrincipalId, context.ExecutionContext.OriginalInitiatorPrincipalId, session, Guid.NewGuid(), Guid.NewGuid(), activation, context.ExecutionContext.RunId, activation, AgentCore.Domain.Conversation.ExecutionOrigin.ApprovalContinuation) } })
            Assert.DoesNotContain(ToolProjectionService.Project(definition, invalid, ToolConfigurationGates.AllowAll), t => t.Name == ToolCatalog.ArtifactsCreateFromWorkspace);
    }

    [Fact]
    public void Source_contract_preserves_exact_path_and_canonicalizes_only_metadata_and_field_order()
    {
        var a = TenantWorkspaceArtifactContract.Parse("""{"displayName":"../report.docx","path":"/working/Báo cáo.docx"}""");
        var b = TenantWorkspaceArtifactContract.Parse("""{"path":"/working/Báo cáo.docx","displayName":"report.docx"}""");
        Assert.Equal(a, b); Assert.Null(a.ContentType); Assert.Equal("/working/Báo cáo.docx", a.Path);
        Assert.Equal(a, TenantWorkspaceArtifactContract.Parse(a.CanonicalArguments));
        var typed = TenantWorkspaceArtifactContract.Parse("""{"path":"/working/Báo cáo.docx","displayName":"report.docx","contentType":" text/plain "}""");
        Assert.Equal("text/plain", typed.ContentType); Assert.NotEqual(a.ParametersHash, typed.ParametersHash);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"path\":\"/home/file\",\"path\":\"/home/other\",\"displayName\":\"file\"}")]
    [InlineData("{\"path\":\"relative\",\"displayName\":\"file\"}")]
    [InlineData("{\"path\":\"/home\",\"displayName\":\"file\"}")]
    [InlineData("{\"path\":\"/working\",\"displayName\":\"file\"}")]
    [InlineData("{\"path\":\"/working/a/../file\",\"displayName\":\"file\"}")]
    [InlineData("{\"path\":\"/home/*\",\"displayName\":\"file\"}")]
    [InlineData("{\"path\":\"/other/file\",\"displayName\":\"file\"}")]
    [InlineData("{\"path\":\"/home/file\",\"displayName\":\"file\",\"content\":\"caller bytes\"}")]
    [InlineData("{\"path\":\"/home/file\",\"displayName\":\"file\",\"contentType\":null}")]
    [InlineData("{\"path\":\"/home/file\",\"displayName\":\"file\",\"contentType\":\"bad type\"}")]
    [InlineData("{\"path\":\"/home/file\",\"displayName\":\"file\",\"contentType\":\"\\r\\ntext/plain\"}")]
    public void Source_contract_refuses_ambiguous_nonconcrete_or_mixed_arguments(string json) => Assert.Throws<ArgumentException>(() => TenantWorkspaceArtifactContract.Parse(json));

    [Theory] [InlineData("tenantId")] [InlineData("sessionId")] [InlineData("instanceId")] [InlineData("principalId")]
    [InlineData("artifactId")] [InlineData("permission")] [InlineData("source")] [InlineData("sha256Hex")] [InlineData("observationHash")]
    public void Source_contract_does_not_accept_caller_authority_or_source_evidence(string field)
    {
        var input = new Dictionary<string, object> { ["path"] = "/home/file", ["displayName"] = "report", [field] = "caller-selected" };
        Assert.Throws<ArgumentException>(() => TenantWorkspaceArtifactContract.Parse(JsonSerializer.Serialize(input)));
    }

    [Fact]
    public void Source_contract_enforces_UTF8_argument_and_native_path_bounds()
    {
        var longPath = "/home/" + new string('é', 128);
        Assert.Throws<ArgumentException>(() => TenantWorkspaceArtifactContract.Parse(JsonSerializer.Serialize(new { path = longPath, displayName = "report" })));
        Assert.Throws<ArgumentException>(() => TenantWorkspaceArtifactContract.Parse(new string(' ', TenantArtifactToolContract.MaxArgumentBytes + 1)));
    }

    [Fact]
    public void Source_action_binds_current_bytes_while_semantic_root_correlation_preserves_the_exact_source_intent()
    {
        var instance = Guid.Parse("00000000-0000-0000-0000-000000000001"); var session = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var args = TenantWorkspaceArtifactContract.Parse("""{"path":"/home/file.bin","displayName":"report.bin"}""");
        var source = new PreparedArtifactSource(false, args.Path, "application/octet-stream", 4, new('a', 64), new('b', 64));
        var a = TenantApprovalRules.PrepareArtifact(Guid.NewGuid(), instance, new(session, args.CanonicalArguments, source), 1, 1);
        var b = TenantApprovalRules.PrepareArtifact(Guid.NewGuid(), instance, new(session, args.CanonicalArguments, source with { Sha256Hex = new('c', 64), ObservationHash = new('d', 64) }), 2, 1);
        Assert.Equal(a.OperationFingerprint, b.OperationFingerprint); Assert.NotEqual(a.ActionHash, b.ActionHash);
        var other = TenantWorkspaceArtifactContract.Parse("""{"path":"/home/other.bin","displayName":"report.bin"}""");
        Assert.NotEqual(a.OperationFingerprint, TenantApprovalRules.PrepareArtifact(Guid.NewGuid(), instance,
            new(session, other.CanonicalArguments, source with { Path = other.Path }), 1, 1).OperationFingerprint);
        Assert.DoesNotContain("Source", a.SemanticActionJson);
    }

    [Fact]
    public void Adding_optional_source_evidence_preserves_the_existing_inline_action_and_root_fingerprint()
    {
        var instance = Guid.Parse("00000000-0000-0000-0000-000000000001"); var session = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var args = TenantArtifactToolContract.Parse("""{"displayName":"report.txt","content":"Exact text"}""");
        var oldPayload = new { SessionId = session, ArgumentsJson = args.CanonicalArguments };
        var oldAction = JsonSerializer.Serialize(new { operation = "artifacts.publish", permission = "artifacts.create", resourceType = "session", resourceId = session.ToString("D"),
            artifact = oldPayload, expectedInstanceRevision = 1L, expectedPersonaRevision = 1L });
        var oldSemantic = JsonSerializer.Serialize(new { operation = "artifacts.publish", permission = "artifacts.create", resourceType = "session", resourceId = session.ToString("D"), artifact = oldPayload });
        var current = TenantApprovalRules.PrepareArtifact(Guid.NewGuid(), instance, new(session, args.CanonicalArguments), 1, 1);
        Assert.Equal(oldAction, current.CanonicalActionJson); Assert.Equal(oldSemantic, current.SemanticActionJson);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(oldSemantic))), current.OperationFingerprint);
        Assert.Equal(oldPayload.ArgumentsJson, JsonSerializer.Deserialize<PreparedArtifactPublication>(JsonSerializer.Serialize(oldPayload))!.ArgumentsJson);
    }
    [Theory] [InlineData("/agent/resources/policy.md")] [InlineData("/agent/instance/resources/literal[resource]?.docx")]
    public void Resource_publication_uses_literal_path_and_exact_source_witness(string path)
    {
        var args = TenantWorkspaceArtifactContract.Parse(JsonSerializer.Serialize(new { path, displayName = "copy.bin" }));
        var key = "instance:" + Guid.NewGuid().ToString("D");
        var source = new PreparedArtifactSource(false, path, "text/plain", 4, new('a', 64), new('b', 64), key, 1);
        var first = TenantApprovalRules.PrepareArtifact(Guid.NewGuid(), Guid.NewGuid(), new(Guid.NewGuid(), args.CanonicalArguments, source), 1, 1);
        Assert.Equal(path, args.Path); Assert.Equal(key, first.ArtifactPublication!.Source!.ResourceKey);
        Assert.Throws<ArgumentException>(() => TenantArtifactSourceRules.ValidateWitness(source with { ResourceKey = null }, path));
        Assert.Throws<ArgumentException>(() => TenantArtifactSourceRules.ValidateWitness(source with { ResourceRevision = -1 }, path));
        Assert.Throws<ArgumentException>(() => TenantArtifactSourceRules.ValidateWitness(source with { Working = true }, path));
    }

    [Fact]
    public void Resource_source_fields_are_omitted_for_existing_workspace_witnesses()
    {
        var old = new { Working = false, Path = "/home/file.txt", ContentType = "text/plain", ByteSize = 4L, Sha256Hex = new string('a', 64), ObservationHash = new string('b', 64) };
        var value = new PreparedArtifactSource(old.Working, old.Path, old.ContentType, old.ByteSize, old.Sha256Hex, old.ObservationHash);
        Assert.Equal(JsonSerializer.Serialize(old), JsonSerializer.Serialize(value));
    }

}
