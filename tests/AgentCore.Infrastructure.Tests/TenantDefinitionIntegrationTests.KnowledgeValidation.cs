using System.Text;
using AgentCore.Application.Admin;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    [Theory]
    [InlineData(false, "legacy")] [InlineData(true, "legacy")]
    [InlineData(false, "explicit")] [InlineData(true, "explicit")]
    [InlineData(false, "missing")] [InlineData(true, "missing")]
    [InlineData(false, "kind")] [InlineData(true, "kind")]
    [InlineData(false, "media")] [InlineData(true, "media")]
    public async Task Owned_Knowledge_publication_requires_actual_same_owner_bindings_without_rewriting_source_paths(bool sqlite, string mode)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var draft = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "Owned Knowledge validation", await Revision());
        var path = mode == "legacy" ? null : "knowledge/policy.md";
        var candidate = draft.Candidate with { Environment = (draft.Candidate.Environment ?? RoleEnvironment.Empty) with {
            KnowledgeSources = [new("policy", "Policy", "cite-policy", path)] } };
        draft = await f.Service.UpdateAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, candidate, await Revision(), draft.Revision);
        var bytes = Encoding.UTF8.GetBytes("Private owned policy content.");
        if (mode != "missing")
        {
            await f.Service.BindResourcesAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, await Revision(), draft.Revision,
                [new(null, path ?? "knowledge/policy", mode == "kind" ? AgentDefinitionResourceKind.Reference : AgentDefinitionResourceKind.Knowledge,
                    mode == "media" ? "image/png" : "text/markdown", bytes)]);
        }
        var current = await f.Service.ReadDraftStateAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId);
        Assert.Equal(path, current.Draft.Candidate.Environment!.KnowledgeList.Single().ResourcePath);
        Assert.Null(await f.Admin.GetDraftAsync(draft.DraftId));
        var finding = DefinitionDraftResourceValidation.CollectFindings(current.Draft.Candidate, current.Resources).SingleOrDefault(f => f.Code.Contains("knowledge", StringComparison.Ordinal));
        var before = await Revision(); var audits = (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count;
        if (mode is "legacy" or "explicit")
        {
            Assert.Null(finding);
            var publication = await f.Service.PublishAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, before, current.Draft.Revision);
            Assert.Equal(path, publication.Payload.Environment!.KnowledgeList.Single().ResourcePath);
            var resources = await f.Service.PublicationResourcesAsync(owner.OwnerPrincipalId, owner.TenantId, Guid.Parse(draft.DefinitionId), publication.Version);
            Assert.Equal(DefinitionResourceContentHasher.ComputeSha256Hex(bytes), Assert.Single(resources).ContentSha256);
            Assert.Null(await f.Admin.GetPublicationAsync(draft.DefinitionId, publication.Version));
        }
        else
        {
            Assert.Equal(mode switch { "missing" => "missing_knowledge_resource", "kind" => "wrong_knowledge_resource_kind", _ => "non_textual_knowledge_resource" }, finding!.Code);
            var refused = await Assert.ThrowsAsync<DefinitionPublicationNotReadyException>(() => f.Service.PublishAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, before, current.Draft.Revision).AsTask());
            Assert.Equal("definition_resources_invalid", refused.ReasonCode); Assert.Equal(before, await Revision());
            Assert.Equal(audits, (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count);
            var retained = await f.Service.ReadDraftAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId);
            Assert.Equal(current.Draft.Revision, retained.Revision); Assert.Equal(path, retained.Candidate.Environment!.KnowledgeList.Single().ResourcePath);
            Assert.Empty(await f.Service.PublicationsAsync(owner.OwnerPrincipalId, owner.TenantId, Guid.Parse(draft.DefinitionId)));
        }
    }
}
