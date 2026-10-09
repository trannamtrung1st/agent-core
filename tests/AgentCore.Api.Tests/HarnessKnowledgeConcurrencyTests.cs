using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Definitions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed class HarnessKnowledgeConcurrencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Knowledge_resource_sequence_does_not_overwrite_a_concurrent_owner_environment_edit(bool remove)
    {
        await using var factory = new InterleavingFactory();
        var service = factory.Services.GetRequiredService<HarnessManagementService>();
        var instance = await factory.Services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 21);
        instance = await service.ConfigureAsync(instance.InstanceId, instance.Revision,
            new(HarnessManagementMode.Managed, [HarnessManagementScope.KnowledgeResources], ["knowledge:support-order-policy"], []));
        instance = await service.StartAsync(instance.InstanceId, instance.Revision, "Prepare knowledge.");
        var prepId = instance.HarnessManagement!.Preparation!.PreparationId;
        var review = await service.ReviewAsync(instance.InstanceId);
        await service.RequestOperationAsync(instance.InstanceId, prepId,
            new("knowledge.upsert", review.Draft!.Revision, Id: "reference", Content: "Existing policy", Source: "knowledge:support-order-policy"));
        review = await service.ReviewAsync(instance.InstanceId);
        factory.Services.GetRequiredService<InterleavingResources>().Armed = true;
        await Assert.ThrowsAsync<AgentCoreException>(async () => await service.RequestOperationAsync(instance.InstanceId, prepId,
            remove ? new("knowledge.remove", review.Draft!.Revision, Id: "reference")
                : new("knowledge.upsert", review.Draft!.Revision, Id: "reference", Content: "Updated policy", Source: "knowledge:support-order-policy")));
        review = await service.ReviewAsync(instance.InstanceId);
        Assert.True(review.Draft!.Candidate.Environment!.Attachments!.AllowUnreadUnsupportedTypes);
        Assert.Equal(21, review.ActiveVersion);
    }

    private sealed class InterleavingFactory : AgentCoreApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<InterleavingResources>();
                services.RemoveAll<IAgentDefinitionResourceAdminStore>();
                services.AddSingleton<IAgentDefinitionResourceAdminStore>(sp => sp.GetRequiredService<InterleavingResources>());
            });
        }
    }

    // Deterministically place a separately committed owner edit between the two authoring commits.
    private sealed class InterleavingResources(InMemoryAgentDefinitionResourceAdminStore inner, IAgentDefinitionAdminStore drafts,
        TimeProvider time) : IAgentDefinitionResourceAdminStore
    {
        public bool Armed { get; set; }
        private async ValueTask Interleave(Guid draftId, CancellationToken ct)
        {
            if (!Armed) return;
            Armed = false;
            var draft = (await drafts.GetDraftAsync(draftId, ct))!;
            await drafts.UpdateDraftAsync(new(draftId, draft.Revision, draft.Candidate with
            {
                Environment = draft.Candidate.Environment! with { Attachments = new(true) }
            }, time.GetUtcNow()), ct);
        }
        public async ValueTask<AgentDefinitionDraftResource> UpsertDraftResourceAsync(AgentDefinitionDraftResourceUpsert upsert, CancellationToken ct = default)
        {
            var result = await inner.UpsertDraftResourceAsync(upsert, ct);
            await Interleave(upsert.DraftId, ct);
            return result;
        }
        public async ValueTask<AgentDefinitionDraftResource> RemoveDraftResourceAsync(AgentDefinitionDraftResourceRemove remove, CancellationToken ct = default)
        {
            var result = await inner.RemoveDraftResourceAsync(remove, ct);
            await Interleave(remove.DraftId, ct);
            return result;
        }
        public ValueTask<IReadOnlyList<AgentDefinitionDraftResource>> ListDraftResourcesAsync(Guid id, CancellationToken ct = default) => inner.ListDraftResourcesAsync(id, ct);
        public ValueTask<AgentDefinitionDraftResourceBatchBound> BindDraftResourcesAsync(AgentDefinitionDraftResourceBatchBind bind, CancellationToken ct = default) => inner.BindDraftResourcesAsync(bind, ct);
        public ValueTask<byte[]?> ReadDraftResourceContentAsync(Guid id, Guid resource, CancellationToken ct = default) => inner.ReadDraftResourceContentAsync(id, resource, ct);
        public ValueTask<IReadOnlyList<AgentDefinitionPublicationResource>> ListPublicationResourcesAsync(string id, int version, CancellationToken ct = default) => inner.ListPublicationResourcesAsync(id, version, ct);
        public ValueTask<byte[]?> ReadPublicationResourceContentAsync(string id, int version, Guid resource, CancellationToken ct = default) => inner.ReadPublicationResourceContentAsync(id, version, resource, ct);
    }
}
