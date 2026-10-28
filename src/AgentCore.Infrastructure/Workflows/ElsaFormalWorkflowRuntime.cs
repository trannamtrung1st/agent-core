using AgentCore.Application.Ports;
using AgentCore.Domain.Workflows;
using Elsa.Workflows;
using Elsa.Workflows.Management;
using WorkflowDefinition = Elsa.Workflows.Management.Entities.WorkflowDefinition;
using Elsa.Workflows.Management.Filters;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Elsa.Persistence.EFCore.Modules.Management;
using Elsa.Persistence.EFCore.Modules.Runtime;

namespace AgentCore.Infrastructure.Workflows;

internal sealed class ElsaFormalWorkflowRuntime(IServiceScopeFactory scopes, FormalWorkflowRuntimeStorage storage) : IFormalWorkflowRuntime
{
    // Single-host, short persistence operations only. No model, Task execution or human wait holds this gate.
    private readonly SemaphoreSlim gate = new(1, 1);
    internal static string DefinitionId(WorkflowVersion version) => $"core-{version.TenantId:N}-{version.WorkflowId:N}-{version.Version}-{version.Graph.Hash}";
    private static string InstanceId(Guid tenant, Guid execution) => tenant != Guid.Empty && execution != Guid.Empty
        ? $"core-{tenant:N}-{execution:N}" : throw new ArgumentException("Real tenant/execution identities are required.");
    public async ValueTask<bool> IsReadyAsync(CancellationToken ct = default)
    {
        try {
            await using var scope = scopes.CreateAsyncScope(); var services = scope.ServiceProvider;
            var managementFactory = services.GetService<IDbContextFactory<ManagementElsaDbContext>>();
            var runtimeFactory = services.GetService<IDbContextFactory<RuntimeElsaDbContext>>();
            if (!storage.Durable) return managementFactory is null && runtimeFactory is null;
            if (managementFactory is null || runtimeFactory is null) return false;
            await using var management = await managementFactory.CreateDbContextAsync(ct);
            await using var persistence = await runtimeFactory.CreateDbContextAsync(ct);
            if (!(await management.Database.GetAppliedMigrationsAsync(ct)).SequenceEqual(management.Database.GetMigrations(), StringComparer.Ordinal)
                || !(await persistence.Database.GetAppliedMigrationsAsync(ct)).SequenceEqual(persistence.Database.GetMigrations(), StringComparer.Ordinal)) return false;
            _ = await management.WorkflowDefinitions.Take(1).AnyAsync(ct);
            _ = await persistence.Bookmarks.Take(1).AnyAsync(ct);
            return true;
        }
        catch (Exception error) when (error is System.Data.Common.DbException or DbUpdateException) { return false; }
    }
    public async ValueTask EnsureCompiledAsync(WorkflowVersion version, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { await using var scope = scopes.CreateAsyncScope(); await Compile(scope.ServiceProvider, version, ct); }
        catch (Exception error) when (error is System.Data.Common.DbException or DbUpdateException) { throw new FormalWorkflowRuntimeUnavailableException(error); }
        finally { gate.Release(); }
    }
    private static async Task Compile(IServiceProvider services, WorkflowVersion version, CancellationToken ct)
    {
        if (version.TenantId == Guid.Empty || version.WorkflowId == Guid.Empty || version.Version < 1) throw new ArgumentException("Invalid workflow version identity.");
        _ = new PublishedWorkflowGraph(version.Graph.CanonicalJson, version.Graph.Hash);
        var id = DefinitionId(version); var store = services.GetRequiredService<IWorkflowDefinitionStore>();
        var existing = await store.FindAsync(new WorkflowDefinitionFilter { Id = id }, ct);
        if (existing is not null)
        {
            if (existing.MaterializerName != CoreWorkflowMaterializer.MaterializerName || existing.MaterializerContext != version.Graph.Hash || existing.StringData != version.Graph.CanonicalJson)
                throw new InvalidOperationException("Compiled workflow integrity mismatch.");
            return;
        }
        await store.SaveAsync(new WorkflowDefinition {
            Id = id, DefinitionId = id, Version = 1, IsLatest = true, IsPublished = true, IsReadonly = true,
            Name = "Core formal workflow", CreatedAt = version.PublishedAt,
            MaterializerName = CoreWorkflowMaterializer.MaterializerName, MaterializerContext = version.Graph.Hash, StringData = version.Graph.CanonicalJson
        }, ct);
    }
    public async ValueTask<FormalWorkflowState> StartAsync(WorkflowVersion version, Guid executionId, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try {
            await using var scope = scopes.CreateAsyncScope(); var services = scope.ServiceProvider;
            await Compile(services, version, ct);
            var client = await services.GetRequiredService<IWorkflowRuntime>().CreateClientAsync(InstanceId(version.TenantId, executionId), ct);
            if (!await client.InstanceExistsAsync(ct))
                await client.CreateInstanceAsync(new CreateWorkflowInstanceRequest {
                    WorkflowDefinitionHandle = WorkflowDefinitionHandle.ByDefinitionVersionId(DefinitionId(version)), CorrelationId = InstanceId(version.TenantId, executionId)
                }, ct);
            var state = await client.ExportStateAsync(ct);
            if (state.DefinitionVersionId != DefinitionId(version) || state.CorrelationId != InstanceId(version.TenantId, executionId))
                throw new InvalidOperationException("Workflow instance does not match the accepted version/source correlation.");
            // Core activities only reserve bookmarks. Re-entering a persisted incomplete execution cycle
            // cannot invoke an agent/tool or replay an external effect; those live in the Core bridge.
            if (state.Status != WorkflowStatus.Finished && state.SubStatus is WorkflowSubStatus.Pending or WorkflowSubStatus.Interrupted or WorkflowSubStatus.Executing)
                await client.RunInstanceAsync(new RunWorkflowInstanceRequest(), ct);
            return await Snapshot(client, ct);
        }
        catch (Exception error) when (error is System.Data.Common.DbException or DbUpdateException) { throw new FormalWorkflowRuntimeUnavailableException(error); }
        finally { gate.Release(); }
    }
    public async ValueTask<FormalWorkflowState?> ReadAsync(Guid tenant, Guid executionId, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try {
            await using var scope = scopes.CreateAsyncScope(); var client = await scope.ServiceProvider.GetRequiredService<IWorkflowRuntime>().CreateClientAsync(InstanceId(tenant, executionId), ct);
            return await client.InstanceExistsAsync(ct) ? await Snapshot(client, ct) : null;
        }
        catch (Exception error) when (error is System.Data.Common.DbException or DbUpdateException) { throw new FormalWorkflowRuntimeUnavailableException(error); }
        finally { gate.Release(); }
    }
    public async ValueTask<FormalWorkflowState> ResumeAsync(Guid tenant, Guid executionId, string bookmarkId, string nodeId, string outcome, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try {
            await using var scope = scopes.CreateAsyncScope(); var client = await scope.ServiceProvider.GetRequiredService<IWorkflowRuntime>().CreateClientAsync(InstanceId(tenant, executionId), ct);
            if (!await client.InstanceExistsAsync(ct)) throw new InvalidOperationException("Workflow instance unavailable.");
            var before = await Snapshot(client, ct);
            if (!before.Bookmarks.Any(b => b.BookmarkId == bookmarkId && b.NodeId == nodeId)) return before;
            await client.RunInstanceAsync(new RunWorkflowInstanceRequest { BookmarkId = bookmarkId,
                Input = new Dictionary<string, object> { ["coreNodeId"] = nodeId, ["coreOutcome"] = outcome } }, ct);
            return await Snapshot(client, ct);
        }
        catch (Exception error) when (error is System.Data.Common.DbException or DbUpdateException) { throw new FormalWorkflowRuntimeUnavailableException(error); }
        finally { gate.Release(); }
    }
    public async ValueTask CancelAsync(Guid tenant, Guid executionId, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try {
            await using var scope = scopes.CreateAsyncScope(); var client = await scope.ServiceProvider.GetRequiredService<IWorkflowRuntime>().CreateClientAsync(InstanceId(tenant, executionId), ct);
            if (await client.InstanceExistsAsync(ct)) await client.CancelAsync(ct);
        }
        catch (Exception error) when (error is System.Data.Common.DbException or DbUpdateException) { throw new FormalWorkflowRuntimeUnavailableException(error); }
        finally { gate.Release(); }
    }
    private static async Task<FormalWorkflowState> Snapshot(IWorkflowClient client, CancellationToken ct)
    {
        var state = await client.ExportStateAsync(ct);
        if (state.Bookmarks.Count > 1) throw new InvalidOperationException("A closed workflow cannot have concurrent actionable steps.");
        return new(state.Status == WorkflowStatus.Finished, state.SubStatus == WorkflowSubStatus.Cancelled, state.SubStatus == WorkflowSubStatus.Faulted,
            state.Properties.TryGetValue("coreTerminalNodeId", out var terminal) ? terminal.ToString() : null,
            state.Bookmarks.Select(b => new FormalWorkflowBookmark(b.Id,
                b.Metadata is not null && b.Metadata.TryGetValue("coreNodeId", out var node) && node is not null ? node.ToString()! : throw new InvalidOperationException("Unknown workflow bookmark."))).ToArray());
    }
}
