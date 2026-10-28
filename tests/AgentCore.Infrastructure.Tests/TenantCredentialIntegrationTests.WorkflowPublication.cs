using AgentCore.Application.Ports;
using AgentCore.Application.Workflows;
using AgentCore.Domain.Workflows;
using AgentCore.Infrastructure.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Workflows_interrupted_publication_recovers_same_prepared_hash_without_exposing_unpublished_version(bool sqlite)
    {
        var clock = new NativeEvaluationClock(); await using var f = await Fixture.Create(sqlite, clock);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Publication recovery", "workflow-publish");
        var login = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var human = new HumanSessionReference(owner.AccountId, login.SessionId, owner.OwnerPrincipalId); var tenant = owner.TenantId;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        await using var provider = WorkflowRuntimeProvider(f, sqlite);
        if (sqlite) await provider.MigrateFormalWorkflowRuntimeAsync(); await provider.PopulateFormalWorkflowActivitiesAsync();
        var engine = provider.GetRequiredService<IFormalWorkflowRuntime>(); var interrupted = new InterruptedPublication(engine);
        var service = Workflows(f, interrupted); var id = Guid.NewGuid();
        await service.SaveAsync(tenant, id, human, new("Interrupted publication", "", HumanWorkflow(human.PrincipalId), false, 0, await Revision(), "draft"));
        var request = new WorkflowPublish(1, await Revision(), "publish-once");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PublishAsync(tenant, id, human, request).AsTask());
        Assert.Null(await ((ITenantWorkflowCatalogStore)f.Iam).FindWorkflowVersionAsync(tenant, id, 1));
        Assert.Equal(0, (await service.ReadAsync(tenant, id, human)).PublishedVersion);
        Assert.Null(await service.RecoverAsync(tenant, human, request.RequestKey));
        var prepared = interrupted.Prepared!; clock.Advance(TimeSpan.FromMinutes(1));
        await f.Access.SaveRoleAsync(human.PrincipalId, tenant, null, "Concurrent access change", ["agents.read"], await Revision());
        await Assert.ThrowsAsync<AgentCore.Application.Security.SecurityConflictException>(() => Workflows(f, engine).PublishAsync(tenant, id, human, request).AsTask());
        request = request with { ExpectedSecurityRevision = await Revision() };
        var recovered = await Workflows(f, engine).PublishAsync(tenant, id, human, request);
        Assert.Equal(prepared, recovered.Version); Assert.Equal(prepared.PublishedAt, recovered.Definition.UpdatedAt);
        Assert.Equal(recovered, await Workflows(f, engine).PublishAsync(tenant, id, human, request));
        Assert.Equal(prepared.Graph.Hash, (await Workflows(f, engine).EligibleVersionAsync(tenant, id, 1, human)).Graph.Hash);
    }
    private sealed class InterruptedPublication(IFormalWorkflowRuntime runtime) : IFormalWorkflowRuntime
    {
        public WorkflowVersion? Prepared { get; private set; }
        public ValueTask<bool> IsReadyAsync(CancellationToken ct = default) => runtime.IsReadyAsync(ct);
        public async ValueTask EnsureCompiledAsync(WorkflowVersion version, CancellationToken ct = default)
        { Prepared = version; await runtime.EnsureCompiledAsync(version, ct); throw new InvalidOperationException("Injected interruption after registered definition."); }
        public ValueTask<FormalWorkflowState> StartAsync(WorkflowVersion version, Guid id, CancellationToken ct = default) => runtime.StartAsync(version, id, ct);
        public ValueTask<FormalWorkflowState?> ReadAsync(Guid tenant, Guid id, CancellationToken ct = default) => runtime.ReadAsync(tenant, id, ct);
        public ValueTask<FormalWorkflowState> ResumeAsync(Guid tenant, Guid id, string bookmark, string node, string outcome, CancellationToken ct = default) => runtime.ResumeAsync(tenant, id, bookmark, node, outcome, ct);
        public ValueTask CancelAsync(Guid tenant, Guid id, CancellationToken ct = default) => runtime.CancelAsync(tenant, id, ct);
    }
}
